namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 设备连接与后台重连的时间参数，集中放置便于联调时调整。
/// </summary>
public sealed class DeviceConnectionRetryOptions
{
    public int ReconnectBaseDelayMs { get; init; } = 2000;
    public int ReconnectMaxDelayMs { get; init; } = 30000;
    public int MaxReconnectAttempts { get; init; } = 12;
    public int MonitorIntervalMs { get; init; } = 5000;
    public int InitialReconnectIntervalMs { get; init; } = 3000;
    public int BackoffReconnectIntervalMs { get; init; } = 5000;
    public int MaxReconnectIntervalMs { get; init; } = 30000;
    public int CleanDisconnectDelayMs { get; init; } = 300;
}
