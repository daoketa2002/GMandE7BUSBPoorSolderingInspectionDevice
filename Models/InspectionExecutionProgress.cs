namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>
/// 轻量执行进度快照，仅保留诊断所需字段。
/// 不再支持断点续测，每次 RunInspectionAsync 都从第 0 项开始。
/// </summary>
public sealed class InspectionExecutionProgress
{
    /// <summary>当前正在检测的项目索引</summary>
    public int CurrentItemIndex { get; set; }

    /// <summary>当前检测步骤名称，用于日志和异常排查</summary>
    public string CurrentStep { get; set; } = "Idle";

    /// <summary>最后一次异常或中断原因</summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>最近更新时间</summary>
    public DateTime LastUpdatedTime { get; set; } = DateTime.Now;

    /// <summary>重置到初始状态</summary>
    public void Reset()
    {
        CurrentItemIndex = 0;
        CurrentStep = "Idle";
        LastErrorMessage = null;
        LastUpdatedTime = DateTime.Now;
    }
}
