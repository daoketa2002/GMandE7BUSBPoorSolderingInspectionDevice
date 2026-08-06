namespace GMScannerRecoveryService.Models;

/// <summary>命名管道恢复请求。服务只接受固定命令和 COM 口。</summary>
public sealed class ScannerRecoveryRequest
{
    public string Command { get; init; } = string.Empty;
    public string PortName { get; init; } = string.Empty;
}
