namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>
/// 运行控制动作仲裁规则。这里只放纯规则，避免 Start/Stop/Reset 的优先级判断散落在界面流程里。
/// </summary>
public static class InspectionControlActionPolicy
{
    public static bool ShouldReplacePending(
        PendingInspectionControlAction currentPending,
        PendingInspectionControlAction newAction)
    {
        return newAction != PendingInspectionControlAction.None && newAction > currentPending;
    }

    public static PendingInspectionControlAction AbsorbLowerPriorityAfterReset(PendingInspectionControlAction pendingAction)
    {
        return pendingAction == PendingInspectionControlAction.EmergencyStop
            ? pendingAction
            : PendingInspectionControlAction.None;
    }

    public static bool ShouldAbsorbNewAction(
        InspectionControlAction currentAction,
        PendingInspectionControlAction newAction)
    {
        return currentAction == InspectionControlAction.Resetting
               && newAction is PendingInspectionControlAction.Start
                   or PendingInspectionControlAction.Stop
                   or PendingInspectionControlAction.Reset;
    }

    public static bool ShouldRejectNewAction(
        InspectionControlAction currentAction,
        PendingInspectionControlAction newAction)
    {
        return currentAction == InspectionControlAction.EmergencyStopping
               && newAction is PendingInspectionControlAction.Start
                   or PendingInspectionControlAction.Reset;
    }
}
