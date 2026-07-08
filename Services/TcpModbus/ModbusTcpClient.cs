using Microsoft.Extensions.Logging;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;

/// <summary>
/// Modbus TCP 客户端实现 — Fp0hPlcDevice 内部使用的 TcpClientPLCMotionService 适配器。
/// 不作为业务层入口，不直接注入 ViewModel。
///
/// 职责：
///   1. 将 TcpClientPLCMotionService 的 Action&lt;bool&gt; 事件适配为 EventHandler&lt;bool&gt;
///   2. 将 CommunicationNotification 转发为 PlcNotification
///   3. 将 ModbusTcpClientOptions 应用到内部的 TcpClientPLCMotionService 属性
///   4. 提供 IModbusTcpClient 接口的标准 Modbus 操作委托
///
/// 不重写 TcpClientPLCMotionService 的成熟收发逻辑，
/// 仅做接口适配以分离 Modbus TCP 通信层与 FP0H PLC 业务层。
/// </summary>
public class ModbusTcpClient : IModbusTcpClient, IDisposable
{
    private readonly TcpClientPLCMotionService _inner;
    private readonly ILogger<ModbusTcpClient> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ModbusTcpClientOptions? _options;
    private bool _disposed;

    private const int DefaultModbusTimeoutMs = 3000;

    public bool IsConnected => _inner.IsConnected;

    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<PlcNotification>? NotificationReceived;

    public ModbusTcpClient(
        TcpClientPLCMotionService inner,
        ILogger<ModbusTcpClient> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 订阅内部服务的 Action<bool> 事件，转发为 EventHandler<bool>
        _inner.ConnectionStateChanged += OnInnerConnectionStateChanged;

        // 订阅内部服务的通信通知，转发为 PlcNotification
        _inner.OnNotification += OnInnerNotification;
    }

    private void OnInnerConnectionStateChanged(bool connected)
        => ConnectionStateChanged?.Invoke(this, connected);

    private void OnInnerNotification(object? sender, CommunicationNotification notification)
    {
        NotificationReceived?.Invoke(this, new PlcNotification(
            type: notification.Type.ToString(),
            message: notification.Message,
            operationName: notification.Source));
    }

    /// <summary>
    /// 建立 Modbus TCP 连接。
    /// 将 ModbusTcpClientOptions 写入内部 TcpClientPLCMotionService 属性，然后启动服务。
    /// </summary>
    public async Task<bool> ConnectAsync(ModbusTcpClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 已连接则直接返回
            if (_inner.IsConnected)
            {
                _logger.LogDebug("ModbusTcpClient 已连接，跳过重复连接");
                return true;
            }

            // 写入配置到内部服务属性
            ApplyOptionsToInner(options);
            _options = options;

            _logger.LogInformation("Modbus TCP 开始连接: {Host}:{Port}, UnitId={UnitId}",
                options.Host, options.Port, options.UnitId);

            // 如果内部服务正在运行，先停止再启动（确保干净状态）
            if (_inner.IsRunning)
            {
                await _inner.StopAsync().ConfigureAwait(false);
            }

            await _inner.StartAsync().ConfigureAwait(false);

            bool connected = _inner.IsConnected;
            _logger.LogInformation("Modbus TCP 连接结果: {Result}", connected ? "成功" : "失败");
            return connected;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Modbus TCP 连接被取消");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modbus TCP 连接异常");
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_inner.IsRunning)
            {
                _logger.LogInformation("Modbus TCP 断开连接");
                await _inner.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modbus TCP 断开连接异常");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    #region Modbus 标准操作委托

    // 现状记录：这些接口保留 CancellationToken 参数，但当前底层 TcpClientPLCMotionService
    // 仍按请求超时控制等待周期；Stop/Reset 不应被日志伪装成已传入外部取消。
    public Task<ModbusResponse?> ReadCoilsAsync(byte unitId, ushort startAddress, ushort quantity, CancellationToken ct = default)
        => _inner.ExecuteReadOperationAsync(0x01, unitId, startAddress, quantity, DefaultModbusTimeoutMs);

    public Task<ModbusResponse?> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort quantity, CancellationToken ct = default)
        => _inner.ExecuteReadOperationAsync(0x03, unitId, startAddress, quantity, DefaultModbusTimeoutMs);

    public Task<ModbusResponse?> WriteSingleCoilAsync(byte unitId, ushort address, bool value, CancellationToken ct = default)
        => _inner.ExecuteWriteOperationAsync(0x05, unitId, address,
            new ushort[] { value ? (ushort)0xFF00 : (ushort)0x0000 }, DefaultModbusTimeoutMs);

    public Task<ModbusResponse?> WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, CancellationToken ct = default)
        => _inner.ExecuteWriteOperationAsync(0x06, unitId, address,
            new ushort[] { value }, DefaultModbusTimeoutMs);

    public Task<ModbusResponse?> WriteMultipleRegistersAsync(byte unitId, ushort startAddress, ushort[] values, CancellationToken ct = default)
        => _inner.ExecuteWriteOperationAsync(0x10, unitId, startAddress, values, DefaultModbusTimeoutMs);

    public Task<ModbusResponse?> SendCustomRequestAsync(byte[] requestFrame, CancellationToken ct = default)
        => _inner.SendCustomModbusRequestAsync(requestFrame, DefaultModbusTimeoutMs);

    public Task<bool> TestConnectionAsync(string host, int port, int timeoutMs, CancellationToken ct = default)
        => _inner.TestConnectionAsync(host, port, timeoutMs, ct);

    #endregion

    #region 配置应用

    /// <summary>将 ModbusTcpClientOptions 写入 TcpClientPLCMotionService 属性</summary>
    private void ApplyOptionsToInner(ModbusTcpClientOptions options)
    {
        _inner.Host = options.Host;
        _inner.Port = options.Port;
        _inner.ReceiveTimeoutMs = options.ReceiveTimeoutMs;
        _inner.SendTimeoutMs = options.SendTimeoutMs;
        _inner.ReconnectDelayMs = options.ReconnectDelayMs;
        // 重连节奏由 DeviceConnectionService 统一调度；底层只做一次 TCP 尝试，避免外层和内层 12x12 叠加阻塞其他设备恢复。
        _inner.MaxReconnectAttempts = 1;
        _inner.HealthCheckMode = options.HealthCheckMode;
        _inner.HealthCheckIntervalSeconds = options.HealthCheckIntervalSeconds;
        _inner.LastDataTimeoutSeconds = options.LastDataTimeoutSeconds;

        _logger.LogDebug("ModbusTcpClient 配置已应用: {Host}:{Port}, UnitId={UnitId}",
            options.Host, options.Port, options.UnitId);
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inner.ConnectionStateChanged -= OnInnerConnectionStateChanged;
        _inner.OnNotification -= OnInnerNotification;

        _connectLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #endregion
}
