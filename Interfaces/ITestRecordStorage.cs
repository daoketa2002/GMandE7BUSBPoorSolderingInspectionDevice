// ============================================================
// 文件: Interfaces/ITestRecordStorage.cs
// 描述: 测试记录持久化接口 —— 定义检测结果的存取契约
//      遵循接口隔离原则，所有持久化操作通过此接口解耦
//      替代原有的 ILogDatabaseService（标记为 Obsolete）
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 测试记录存储接口
    /// 封装检测结果（LogRecord + PinResults）的持久化存取操作
    /// 实现类可以是 SQLite、CSV 文件、JSON 文件等任意存储介质
    /// </summary>
    public interface ITestRecordStorage
    {
        /// <summary>
        /// 事务保存一条完整的检测记录
        /// 包括主记录（LogRecord）和所有 Pin 明细（PinResults）
        /// </summary>
        /// <param name="record">待保存的主记录（含 PinResults 列表）</param>
        Task SaveRecordAsync(LogRecord record);

        /// <summary>
        /// 分页查询检测记录，支持多条件组合筛选
        /// 所有筛选条件为 AND 关系，null 或空白视为忽略该条件
        /// </summary>
        /// <param name="series">系列筛选（模糊匹配）</param>
        /// <param name="serialNumber">序列号筛选（模糊匹配）</param>
        /// <param name="planName">方案名称筛选（精确匹配）</param>
        /// <param name="startDate">开始日期（含）</param>
        /// <param name="endDate">结束日期（含）</param>
        /// <param name="finalResult">判定结果筛选："OK" / "NG" / null=全部</param>
        /// <param name="pageIndex">页码（从1开始）</param>
        /// <param name="pageSize">每页条数</param>
        /// <returns>(本页记录列表, 符合条件的总记录数)</returns>
        Task<(List<LogRecord> Records, int TotalCount)> QueryRecordsAsync(
            string? series = null,
            string? serialNumber = null,
            string? planName = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            string? finalResult = null,
            int pageIndex = 1,
            int pageSize = 20);

        /// <summary>
        /// 查询当前筛选范围内全部检测记录，用于导出等不分页场景。
        /// CSV 实现优先走月度索引定位行号，避免沿用页面查询的数量上限。
        /// </summary>
        Task<List<LogRecord>> QueryAllRecordsAsync(
            string? series = null,
            string? serialNumber = null,
            string? planName = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            string? finalResult = null);

        /// <summary>
        /// 获取当前筛选范围内所有命中记录结构的动态 Pin 列并集。
        /// CSV 实现可通过索引命中的文件表头计算，避免加载全部完整记录。
        /// </summary>
        Task<List<string>> GetDynamicHeadersAsync(
            string? series = null,
            string? serialNumber = null,
            string? planName = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            string? finalResult = null);

        /// <summary>
        /// 判断指定机种和序列号在时间范围内是否已有检测记录。
        /// 用于运行页重复测试提醒，找到第一条即可返回。
        /// </summary>
        Task<bool> ExistsRecentTestRecordAsync(
            string machineType,
            string serialNumber,
            DateTime startTime,
            DateTime endTime);

        /// <summary>
        /// 获取所有机种名称（去重）
        /// </summary>
        Task<List<string>> GetMachineTypesAsync();

        /// <summary>
        /// 获取所有方案名称（去重）
        /// </summary>
        Task<List<string>> GetPlanNamesAsync();
    }
}
