namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>设备主动健康检查的结果状态。</summary>
public enum DeviceHealthCheckStatus
{
    Healthy,
    Unhealthy,
    SkippedBusy,
    Disabled
}

/// <summary>设备健康检查结果；连接服务据此决定是否确认断线。</summary>
public sealed record DeviceHealthCheckResult(
    DeviceHealthCheckStatus Status,
    string Message,
    Exception? Exception = null)
{
    public bool IsHealthy => Status == DeviceHealthCheckStatus.Healthy;

    public static DeviceHealthCheckResult Healthy(string message = "OK")
        => new(DeviceHealthCheckStatus.Healthy, message);

    public static DeviceHealthCheckResult Unhealthy(string message, Exception? exception = null)
        => new(DeviceHealthCheckStatus.Unhealthy, message, exception);

    public static DeviceHealthCheckResult SkippedBusy(string message)
        => new(DeviceHealthCheckStatus.SkippedBusy, message);

    public static DeviceHealthCheckResult Disabled(string message)
        => new(DeviceHealthCheckStatus.Disabled, message);
}
