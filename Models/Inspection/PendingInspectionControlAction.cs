namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>
/// 运行控制挂起动作。枚举值只表达固定优先级：急停 > 复位 > 停止 > 启动。
/// </summary>
public enum PendingInspectionControlAction
{
    None = 0,
    Start = 1,
    Stop = 2,
    Reset = 3,
    EmergencyStop = 4
}
