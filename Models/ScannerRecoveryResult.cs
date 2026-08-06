namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>扫描枪恢复服务的明确结果，不把服务异常直接抛给 UI。</summary>
public sealed class ScannerRecoveryResult
{
    public bool Succeeded { get; init; }
    public string ResultCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string PortName { get; init; } = string.Empty;
    public long ElapsedMs { get; init; }

    public static ScannerRecoveryResult Failure(string code, string message, string portName = "")
        => new()
        {
            Succeeded = false,
            ResultCode = code,
            Message = message,
            PortName = portName
        };
}
