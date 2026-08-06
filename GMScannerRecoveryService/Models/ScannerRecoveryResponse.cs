namespace GMScannerRecoveryService.Models;

/// <summary>命名管道恢复响应，不包含扫码内容。</summary>
public sealed class ScannerRecoveryResponse
{
    public bool Succeeded { get; init; }
    public string ResultCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string PortName { get; init; } = string.Empty;
    public long ElapsedMs { get; init; }

    public static ScannerRecoveryResponse Failure(string code, string message, string portName = "")
        => new()
        {
            Succeeded = false,
            ResultCode = code,
            Message = message,
            PortName = portName
        };
}
