namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>设备连接管理器执行扫描枪深度恢复后的结果。</summary>
public sealed class ScannerDeepRecoveryResult
{
    public bool Succeeded { get; init; }
    public string ResultCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string PortName { get; init; } = string.Empty;
    public ScannerRecoveryDrainResult? DrainResult { get; init; }

    public static ScannerDeepRecoveryResult Failure(
        string code,
        string message,
        string portName = "",
        ScannerRecoveryDrainResult? drainResult = null)
        => new()
        {
            Succeeded = false,
            ResultCode = code,
            Message = message,
            PortName = portName,
            DrainResult = drainResult
        };
}
