namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>
/// 复位收口后的最终控制信号验证结果。
/// DT121 的清除结果由复位写入响应确认，不通过快照读回判定复位失败。
/// </summary>
public enum ResetCompletionValidationResult
{
    Success,
    StartSignalStillActive,
    StopSignalStillActive,
    EmergencyStopStillActive,
    PlcReadFailed
}
