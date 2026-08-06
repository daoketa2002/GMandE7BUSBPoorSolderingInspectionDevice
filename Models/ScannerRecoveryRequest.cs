namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>主程序发送给本机恢复服务的固定请求。</summary>
public sealed class ScannerRecoveryRequest
{
    public string Command { get; init; } = "RestartScanner";
    public string PortName { get; init; } = string.Empty;
}
