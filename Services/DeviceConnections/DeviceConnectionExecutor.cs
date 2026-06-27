using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 执行单台设备的连接、断开和带重试连接，供启动、手动重连和后台监控复用。
/// 每个设备有独立互斥锁，允许 PLC、万用表、扫描枪并行连接。
/// </summary>
public sealed class DeviceConnectionExecutor : IDisposable
{
    private readonly ILogger<DeviceConnectionExecutor> _logger;
    private readonly DeviceConnectionRetryOptions _options;
    private readonly SemaphoreSlim _plcLock = new(1, 1);
    private readonly SemaphoreSlim _dmmLock = new(1, 1);
    private readonly SemaphoreSlim _scannerLock = new(1, 1);

    public DeviceConnectionExecutor(
        ILogger<DeviceConnectionExecutor> logger,
        DeviceConnectionRetryOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// 带指数退避重试连接指定设备。
    /// 返回 true 表示连接成功，false 表示达到最大重试次数后失败。
    /// </summary>
    public async Task<bool> ConnectWithRetryAsync(
        ICommunicationDevice device,
        string deviceType,
        CancellationToken ct)
    {
        var deviceLock = GetLock(deviceType);
        _logger.LogInformation("[设备连接][{DeviceType}] 开始连接流程", deviceType);

        await deviceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (device.IsConnected)
            {
                _logger.LogInformation("[设备连接][{DeviceType}] 硬件报告已连接", deviceType);
                return true;
            }

            for (var attempt = 1; attempt <= _options.MaxReconnectAttempts && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    LogAttempt(device, deviceType, attempt);
                    await device.DisconnectAsync().ConfigureAwait(false);
                    await Task.Delay(_options.CleanDisconnectDelayMs, ct).ConfigureAwait(false);

                    var connected = await device.ConnectAsync(ct).ConfigureAwait(false);
                    if (connected)
                    {
                        _logger.LogInformation("[设备连接][{DeviceType}] 连接成功", deviceType);
                        return true;
                    }

                    _logger.LogWarning("[设备连接][{DeviceType}] ConnectAsync 返回 false，尝试 {Attempt}/{MaxAttempts}",
                        deviceType, attempt, _options.MaxReconnectAttempts);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[设备连接][{DeviceType}] 连接被取消", deviceType);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[设备连接][{DeviceType}] 连接异常，尝试 {Attempt}/{MaxAttempts}",
                        deviceType, attempt, _options.MaxReconnectAttempts);
                }

                if (attempt < _options.MaxReconnectAttempts)
                {
                    var delay = Math.Min(
                        _options.ReconnectBaseDelayMs * (int)Math.Pow(2, attempt - 1),
                        _options.ReconnectMaxDelayMs);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }

            _logger.LogWarning("[设备连接][{DeviceType}] 达到最大重试次数，连接失败", deviceType);
            return false;
        }
        finally
        {
            deviceLock.Release();
            _logger.LogInformation("[设备连接][{DeviceType}] 连接流程结束", deviceType);
        }
    }

    /// <summary>
    /// 安全断开指定设备，异常被捕获记录不向上抛出。
    /// </summary>
    public async Task DisconnectAsync(ICommunicationDevice device, string deviceType)
    {
        var deviceLock = GetLock(deviceType);
        await deviceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await device.DisconnectAsync().ConfigureAwait(false);
            _logger.LogInformation("[设备连接][{DeviceType}] 已断开连接", deviceType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[设备连接][{DeviceType}] 断开连接失败", deviceType);
        }
        finally
        {
            deviceLock.Release();
        }
    }

    private SemaphoreSlim GetLock(string deviceType) => deviceType switch
    {
        DeviceTypeNames.Plc => _plcLock,
        DeviceTypeNames.Dmm => _dmmLock,
        DeviceTypeNames.Scanner => _scannerLock,
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, "未知设备类型")
    };

    /// <summary>
    /// 连接尝试日志：扫描枪设备额外输出当前串口配置与系统可用串口列表。
    /// </summary>
    private void LogAttempt(ICommunicationDevice device, string deviceType, int attempt)
    {
        _logger.LogInformation("[设备连接][{DeviceType}] 连接尝试 {Attempt}/{MaxAttempts}",
            deviceType, attempt, _options.MaxReconnectAttempts);

        if (deviceType == DeviceTypeNames.Scanner && device is HoneywellH1900Scanner scanner)
        {
            _logger.LogInformation("[设备连接][扫描枪] 当前配置 - 端口:{Port}, 波特率:{BaudRate}",
                scanner.PortName, scanner.BaudRate);
            try
            {
                var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
                _logger.LogInformation("[设备连接][扫描枪] 系统可用串口: {Ports}", string.Join(", ", availablePorts));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[设备连接][扫描枪] 无法枚举系统串口");
            }
        }
    }

    public void Dispose()
    {
        _plcLock.Dispose();
        _dmmLock.Dispose();
        _scannerLock.Dispose();
    }
}
