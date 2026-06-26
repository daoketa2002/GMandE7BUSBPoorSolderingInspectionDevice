using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 集中保存三台设备的连接状态、状态文本和重连统计。
/// </summary>
public sealed class DeviceConnectionStateStore
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, DeviceState> _states = new()
    {
        [DeviceTypeNames.Plc] = new DeviceState(),
        [DeviceTypeNames.Dmm] = new DeviceState(),
        [DeviceTypeNames.Scanner] = new DeviceState()
    };

    /// <summary>
    /// 缓存全部设备就绪状态，在 UpdateConnectionState 内原子更新。
    /// </summary>
    private volatile bool _areAllDevicesReady;

    public bool IsPlcConnected => IsConnected(DeviceTypeNames.Plc);
    public bool IsDmmConnected => IsConnected(DeviceTypeNames.Dmm);
    public bool IsScannerConnected => IsConnected(DeviceTypeNames.Scanner);
    public bool AreAllDevicesReady => _areAllDevicesReady;

    public string PlcStatusText => GetStatusText(DeviceTypeNames.Plc);
    public string DmmStatusText => GetStatusText(DeviceTypeNames.Dmm);
    public string ScannerStatusText => GetStatusText(DeviceTypeNames.Scanner);

    public bool IsConnected(string deviceType)
    {
        lock (_syncRoot)
        {
            return _states[deviceType].IsConnected;
        }
    }

    public string GetStatusText(string deviceType)
    {
        lock (_syncRoot)
        {
            return _states[deviceType].StatusText;
        }
    }

    public void SetConnecting(string deviceType)
    {
        lock (_syncRoot)
        {
            _states[deviceType].StatusText = "连接中...";
        }
    }

    /// <summary>
    /// 更新设备连接状态。
    /// 连接成功时清零连续失败次数；
    /// 连接失败时自动递增连续失败次数（调用方无需额外调用 IncrementFailure）。
    /// 内部原子更新全部设备就绪缓存。
    /// </summary>
    public DeviceConnectionStateChangedEventArgs UpdateConnectionState(string deviceType, bool isConnected)
    {
        lock (_syncRoot)
        {
            var state = _states[deviceType];
            state.IsConnected = isConnected;
            state.StatusText = isConnected ? "已连接" : "未连接";

            if (isConnected)
            {
                state.ConsecutiveFailures = 0;
            }
            else
            {
                state.ConsecutiveFailures++;
            }

            // 在同一个锁内原子计算全部设备就绪状态，避免三次独立锁导致的快照不一致
            _areAllDevicesReady = _states[DeviceTypeNames.Plc].IsConnected
                && _states[DeviceTypeNames.Dmm].IsConnected
                && _states[DeviceTypeNames.Scanner].IsConnected;

            return new DeviceConnectionStateChangedEventArgs(deviceType, isConnected, state.StatusText);
        }
    }

    public int GetFailureCount(string deviceType)
    {
        lock (_syncRoot)
        {
            return _states[deviceType].ConsecutiveFailures;
        }
    }

    public bool ShouldAttemptReconnect(string deviceType, DeviceConnectionRetryOptions options, DateTime now)
    {
        lock (_syncRoot)
        {
            var state = _states[deviceType];
            var cooldownMs = Math.Min(
                options.InitialReconnectIntervalMs + state.ConsecutiveFailures * options.BackoffReconnectIntervalMs,
                options.MaxReconnectIntervalMs);

            if ((now - state.LastReconnectAttempt).TotalMilliseconds < cooldownMs)
            {
                return false;
            }

            state.LastReconnectAttempt = now;
            return true;
        }
    }

    private sealed class DeviceState
    {
        public bool IsConnected { get; set; }
        public string StatusText { get; set; } = "未连接";
        public int ConsecutiveFailures { get; set; }
        public DateTime LastReconnectAttempt { get; set; } = DateTime.MinValue;
    }
}
