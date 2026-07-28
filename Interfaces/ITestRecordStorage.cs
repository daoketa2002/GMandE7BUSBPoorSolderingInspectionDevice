using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

/// <summary>
/// 正式检测记录的最小持久化契约。
/// 当前业务使用 CSV 保存记录，并通过正式 CSV 完成近期重复测试检查。
/// </summary>
public interface ITestRecordStorage
{
    /// <summary>保存一条完整的正式检测记录。</summary>
    Task SaveRecordAsync(LogRecord record);

    /// <summary>判断指定机种和序列号在时间范围内是否已有检测记录。</summary>
    Task<bool> ExistsRecentTestRecordAsync(
        string machineType,
        string serialNumber,
        DateTime startTime,
        DateTime endTime);
}
