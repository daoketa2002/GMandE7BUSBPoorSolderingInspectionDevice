namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>
/// 最小检测闭环使用的内存断点快照。
/// 当前阶段只保存在进程内，用于停止后续作，不支持断电或软件崩溃恢复。
/// </summary>
public sealed class InspectionCheckpoint
{
    /// <summary>
    /// 当前正在检测的项目索引。停止发生在第 N 项未完成时，恢复后仍从第 N 项重测。
    /// </summary>
    public int CurrentItemIndex { get; set; }

    /// <summary>
    /// 当前检测步骤名称，用于日志和异常排查。
    /// </summary>
    public string CurrentStep { get; set; } = "Idle";

    /// <summary>
    /// 当前检测锁定的机种名称。
    /// </summary>
    public string MachineType { get; set; } = string.Empty;

    /// <summary>
    /// 当前检测锁定的序列号。
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// 当前检测锁定的方案名称。
    /// </summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>
    /// 当前检测锁定的作业员名称。
    /// </summary>
    public string OperatorName { get; set; } = string.Empty;

    /// <summary>
    /// 已完成项目结果。当前项只有完成判定后才加入列表。
    /// </summary>
    public List<PinResult> FinishedResults { get; set; } = new();

    /// <summary>
    /// 最后一次异常或中断原因。
    /// </summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// 是否存在可由用户再次启动后续作的断点。
    /// </summary>
    public bool HasBreakpoint { get; set; }

    /// <summary>
    /// 最近更新时间，仅用于日志排查。
    /// </summary>
    public DateTime LastUpdatedTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 清空断点和已完成结果，回到新一轮检测准备状态。
    /// </summary>
    public void ResetProgress()
    {
        CurrentItemIndex = 0;
        CurrentStep = "Idle";
        FinishedResults.Clear();
        HasBreakpoint = false;
        LastErrorMessage = null;
        LastUpdatedTime = DateTime.Now;
    }
}
