namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>
/// 检测引擎停止等待结果。区分 AlreadyStopped 和 Stopped，便于现场日志判断调用时机。
/// </summary>
public enum InspectionStopWaitResult
{
    AlreadyStopped,
    Stopped,
    Timeout
}
