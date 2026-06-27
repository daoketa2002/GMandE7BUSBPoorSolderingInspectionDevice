using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 后台监控设备断线并触发重连，连接细节委托给 DeviceConnectionExecutor。
/// 定时检查各设备 IsConnected，达到重连条件后调用执行器，不在此类中重新实现连接逻辑。
/// </summary>
public sealed class DeviceConnectionMonitor : IDisposable
{
    private readonly ILogger<DeviceConnectionMonitor> _logger;
    private readonly DeviceConnectionRetryOptions _options;
    private readonly DeviceConnectionExecutor _executor;
    private readonly DeviceConnectionStateStore _stateStore;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    public DeviceConnectionMonitor(
        ILogger<DeviceConnectionMonitor> logger,
        DeviceConnectionRetryOptions options,
        DeviceConnectionExecutor executor,
        DeviceConnectionStateStore stateStore)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    /// <summary>
    /// 启动后台监控。如果已在运行则忽略。
    /// </summary>
    public void Start(
        IReadOnlyDictionary<string, ICommunicationDevice> devices,
        Func<string, bool, Task> onStateChangedAsync,
        Func<Task> onScannerConnectedAsync)
    {
        if (_monitorCts is not null)
        {
            _logger.LogDebug("[设备连接] 后台监控已在运行，忽略重复启动");
            return;
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(devices, onStateChangedAsync, onScannerConnectedAsync, _monitorCts.Token));
        _logger.LogInformation("[设备连接] 后台设备监控已启动，检查间隔 {Interval}ms", _options.MonitorIntervalMs);
    }

    /// <summary>
    /// 停止后台监控，等待监控线程退出（最长 5 秒）。
    /// </summary>
    public void Stop()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        if (_monitorTask is not null)
        {
            try
            {
                _monitorTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[设备连接] 等待监控线程结束时出现异常");
            }

            _monitorTask = null;
        }

        _logger.LogInformation("[设备连接] 后台设备监控已停止");
    }

    private async Task MonitorLoopAsync(
        IReadOnlyDictionary<string, ICommunicationDevice> devices,
        Func<string, bool, Task> onStateChangedAsync,
        Func<Task> onScannerConnectedAsync,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.MonitorIntervalMs, ct).ConfigureAwait(false);

                foreach (var pair in devices)
                {
                    await CheckAndReconnectAsync(pair.Key, pair.Value, onStateChangedAsync, onScannerConnectedAsync, ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[设备连接] 后台监控被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[设备连接] 后台监控异常");
        }
    }

    /// <summary>
    /// 检查单台设备：已连接则跳过；未连接且冷却时间已到则调用执行器重连。
    /// 扫描枪重连成功后回调 onScannerConnectedAsync 初始化条码服务。
    /// </summary>
    private async Task CheckAndReconnectAsync(
        string deviceType,
        ICommunicationDevice device,
        Func<string, bool, Task> onStateChangedAsync,
        Func<Task> onScannerConnectedAsync,
        CancellationToken ct)
    {
        if (device.IsConnected)
        {
            return;
        }

        if (!_stateStore.ShouldAttemptReconnect(deviceType, _options, DateTime.Now))
        {
            return;
        }

        _stateStore.SetConnecting(deviceType);
        _logger.LogWarning("[设备连接][{DeviceType}] 后台监控检测到设备未连接，开始重连", deviceType);

        var connected = await _executor.ConnectWithRetryAsync(device, deviceType, ct).ConfigureAwait(false);
        await onStateChangedAsync(deviceType, connected).ConfigureAwait(false);

        if (connected && deviceType == DeviceTypeNames.Scanner)
        {
            await onScannerConnectedAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
