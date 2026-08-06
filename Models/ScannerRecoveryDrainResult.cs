namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>扫描枪深度恢复后的历史数据排空结果。</summary>
public sealed class ScannerRecoveryDrainResult
{
    public int DrainedBytes { get; init; }
    public int ElapsedMs { get; init; }
    public bool TimedOut { get; init; }
}
