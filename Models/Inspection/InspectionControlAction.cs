namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>
/// 运行页当前正在执行的控制动作。它不是界面状态，只用于 Start/Stop/Reset/EmergencyStop/Finish 串行门禁。
/// </summary>
public enum InspectionControlAction
{
    None,
    Starting,
    Stopping,
    Resetting,
    EmergencyStopping,
    Finishing
}
