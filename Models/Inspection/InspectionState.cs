namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>检测引擎运行状态</summary>
public enum InspectionState
{
    Idle,
    Initializing,
    WaitingForProductInfo,
    WaitingForValidPlan,
    WaitingForDevices,
    WaitingForPlcStart,
    StartingValidation,
    Testing,
    PausedByStop,
    PausedByEmergencyStop,
    /// <summary>单项 NG 后按系统设置停止本轮，等待操作员复位或终了</summary>
    StoppedBySingleItemNg,
    ResetRequested,
    CompletedPendingSave,
    CompletedPass,
    CompletedFail,
    Aborted,
    Error
}
