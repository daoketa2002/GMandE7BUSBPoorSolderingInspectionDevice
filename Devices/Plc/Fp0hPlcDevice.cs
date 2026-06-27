using Microsoft.Extensions.Logging;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Devices.Plc;

/// <summary>
/// 松下 FP0H (AFP0HC32ET) PLC 设备实现。
///
/// 职责：
///   1. 实现 IPlcDevice 接口，将 FP0H 检测业务动作翻译为 Modbus 操作
///   2. 内部依赖 IModbusTcpClient 执行实际通信，不直接操作功能码和裸地址
///   3. 转发连接状态和通知事件
///   4. 使用 Warning 级别记录审计相关操作（连接、写入关键信号）
///
/// 生命周期：由 DI 容器作为单例管理，ConnectAsync/DisconnectAsync 可重复调用。
/// </summary>
public class Fp0hPlcDevice : IPlcDevice, IDisposable
{
    private readonly IModbusTcpClient _modbusClient;
    private readonly ILogger<Fp0hPlcDevice> _logger;
    private readonly PlcAddressMap _addressMap = new();
    private FP0HCommunicationConfig? _config;
    private bool _disposed;

    private const byte DefaultUnitId = 1;
    private const int DefaultTimeoutMs = 3000;

    public bool IsConnected => _modbusClient.IsConnected;

    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<PlcNotification>? NotificationReceived;
    public event EventHandler<AlarmState>? AlarmStateChanged;

    public Fp0hPlcDevice(
        IModbusTcpClient modbusClient,
        ILogger<Fp0hPlcDevice> logger)
    {
        _modbusClient = modbusClient ?? throw new ArgumentNullException(nameof(modbusClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 转发底层 Modbus 客户端事件
        _modbusClient.ConnectionStateChanged += (_, connected)
            => ConnectionStateChanged?.Invoke(this, connected);
        _modbusClient.NotificationReceived += (_, notification)
            => NotificationReceived?.Invoke(this, notification);
    }

    #region 配置

    /// <summary>
    /// 应用 FP0H 通信配置。
    /// 在 ConnectAsync 之前调用，保存配置供后续连接使用。
    /// </summary>
    public void ApplyConfig(FP0HCommunicationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _logger.LogDebug("[设备连接][PLC] FP0H PLC 配置已接收: {Host}:{Port}", config.IpAddress, config.Port);
    }

    #endregion

    #region 连接生命周期

    /// <summary>
    /// 连接 PLC。将保存的配置转换为 ModbusTcpClientOptions 后委托给 IModbusTcpClient。
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (_config is null)
        {
            _logger.LogWarning("[设备连接][PLC] 连接失败: 未调用 ApplyConfig，缺少连接参数");
            return false;
        }

        var options = ModbusTcpClientOptions.From(_config);
        _logger.LogInformation("[设备连接][PLC] 开始连接: {Host}:{Port}, UnitId={UnitId}",
            options.Host, options.Port, options.UnitId);

        bool result = await _modbusClient.ConnectAsync(options, ct).ConfigureAwait(false);

        if (result)
            _logger.LogWarning("[设备连接][PLC] 连接成功: {Host}:{Port}", options.Host, options.Port);
        else
            _logger.LogWarning("[设备连接][PLC] 连接失败: {Host}:{Port}", options.Host, options.Port);

        return result;
    }

    /// <summary>
    /// 断开 PLC 连接。
    /// </summary>
    public async Task DisconnectAsync()
    {
        _logger.LogWarning("[设备连接][PLC] 断开连接");
        await _modbusClient.DisconnectAsync().ConfigureAwait(false);
    }

    #endregion

    #region 业务操作

    public async Task<PlcOperationResult> ReadStartSignalAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _modbusClient.ReadCoilsAsync(
                DefaultUnitId, _addressMap.StartFlag, 1, ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("读取启动信号失败: 无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"读取启动信号失败: Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success("读取启动信号成功", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("读取启动信号被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 读取启动信号异常");
            return PlcOperationResult.Failure($"读取启动信号异常: {ex.Message}");
        }
    }

    public async Task<PlcOperationResult> SetBusyAsync(bool value, CancellationToken ct = default)
    {
        return await WriteCoilAsync("Busy", _addressMap.BusyFlag, value, ct).ConfigureAwait(false);
    }

    public async Task<PlcOperationResult> SetOkAsync(bool value, CancellationToken ct = default)
    {
        return await WriteCoilAsync("OK", _addressMap.OkFlag, value, ct).ConfigureAwait(false);
    }

    public async Task<PlcOperationResult> SetNgAsync(bool value, CancellationToken ct = default)
    {
        return await WriteCoilAsync("NG", _addressMap.NgFlag, value, ct).ConfigureAwait(false);
    }

    public async Task<PlcOperationResult> SetErrorAsync(bool value, CancellationToken ct = default)
    {
        return await WriteCoilAsync("Error", _addressMap.ErrorFlag, value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 选择测试点：将测试点编号（index + 1）写入 D100 寄存器。
    /// PLC 收到编号后切换对应继电器接入测试回路。
    /// </summary>
    public async Task<PlcOperationResult> SelectTestPointAsync(int testPointIndex, CancellationToken ct = default)
    {
        try
        {
            ushort value = (ushort)(testPointIndex + 1);
            _logger.LogWarning("[PLC动作] 选择测试点: 索引={Index}, D100值={Value}", testPointIndex, value);

            var response = await _modbusClient.WriteSingleRegisterAsync(
                DefaultUnitId, _addressMap.TestPointSelectRegister, value, ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("写入测试点选择失败: 无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"写入测试点选择失败: Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success($"测试点选择 D100 = {value}", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("写入测试点选择被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 写入测试点选择异常");
            return PlcOperationResult.Failure($"写入测试点选择异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 控制指定通道的继电器开合（RelayBaseAddress + channel, FC 0x05）。
    /// channel 从 0 开始，对应 M{RelayBaseAddress + channel} 线圈。
    /// </summary>
    public async Task<PlcOperationResult> SetRelayAsync(int channel, bool value, CancellationToken ct = default)
    {
        if (channel < 0)
        {
            _logger.LogWarning("[PLC动作] 继电器通道号无效: {Channel}", channel);
            return PlcOperationResult.Failure($"继电器通道号无效: {channel}");
        }

        ushort address = (ushort)(_addressMap.RelayBaseAddress + channel);
        _logger.LogWarning("[PLC动作] 继电器: 通道={Channel}, 地址=M{Address}, 值={Value}", channel, address, value);

        return await WriteCoilCoreAsync($"继电器{channel}", address, value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入单个测试点检测结果到保持寄存器（TestResultBaseRegister + index）。
    /// index 从 0 开始，对应 D{TestResultBaseRegister + index} 寄存器。
    /// resultValue 通常为 1（OK）或 0（NG）。
    /// </summary>
    public async Task<PlcOperationResult> WriteTestResultAsync(int index, ushort resultValue, CancellationToken ct = default)
    {
        if (index < 0)
        {
            _logger.LogWarning("[PLC动作] 检测结果索引无效: {Index}", index);
            return PlcOperationResult.Failure($"检测结果索引无效: {index}");
        }

        try
        {
            ushort address = (ushort)(_addressMap.TestResultBaseRegister + index);
            _logger.LogWarning("[PLC动作] 写入检测结果: 索引={Index}, D{Address}={Value}", index, address, resultValue);

            var response = await _modbusClient.WriteSingleRegisterAsync(
                DefaultUnitId, address, resultValue, ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure($"检测结果[{index}]写入失败: 无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"检测结果[{index}]写入失败: Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success($"检测结果 D{address} = {resultValue}", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure($"检测结果[{index}]写入被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 检测结果[{Index}]写入异常", index);
            return PlcOperationResult.Failure($"检测结果[{index}]写入异常: {ex.Message}");
        }
    }

    #endregion

    #region 内部辅助

    /// <summary>
    /// 通用线圈写入（用于 SetBusy/SetOk/SetNg/SetError 四个信号）
    /// </summary>
    private async Task<PlcOperationResult> WriteCoilAsync(string signalName, ushort address, bool value, CancellationToken ct)
    {
        _logger.LogWarning("[PLC动作] 设置 {Signal} = {Value} (M{Address})", signalName, value, address);
        return await WriteCoilCoreAsync(signalName, address, value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写单线圈（FC 0x05）核心实现，包含异常处理
    /// </summary>
    private async Task<PlcOperationResult> WriteCoilCoreAsync(string name, ushort address, bool value, CancellationToken ct)
    {
        try
        {
            var response = await _modbusClient.WriteSingleCoilAsync(
                DefaultUnitId, address, value, ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure($"{name}写入失败: 无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"{name}写入失败: Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success($"{name} = {value}", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure($"{name}写入被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] {Name}写入异常 (M{Address})", name, address);
            return PlcOperationResult.Failure($"{name}写入异常: {ex.Message}");
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // _modbusClient 是 DI 单例，不由 Fp0hPlcDevice 释放
        GC.SuppressFinalize(this);
    }

    #endregion
}
