namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>
/// 复位收口后的最终控制信号验证结果。只有 Success 才允许运行页回到 Ready/CanStart。
/// </summary>
public enum ResetCompletionValidationResult
{
    Success,
    StartSignalStillActive,
    ResetSignalStillActive,
    StopSignalStillActive,
    EmergencyStopStillActive,
    PlcReadFailed
}
