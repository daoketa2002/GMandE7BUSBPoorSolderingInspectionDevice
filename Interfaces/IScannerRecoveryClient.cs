using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

/// <summary>主程序调用本机扫描枪恢复 Windows 服务的最小接口。</summary>
public interface IScannerRecoveryClient
{
    Task<ScannerRecoveryResult> RestartAsync(
        string portName,
        CancellationToken cancellationToken = default);
}
