// ============================================================
// 文件: Interfaces/ILogDatabaseService.cs
// 描述: 日志数据库服务接口 —— 定义SQLite检测结果的存取契约
//      遵循接口隔离原则，所有数据库操作通过此接口解耦
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 日志数据库服务接口
    /// 封装 SQLite 中检测结果（LogRecords + PinResults）的所有存取操作
    /// 实现类使用 EF Core 进行事务写入和 LINQ 查询
    /// </summary>
    public interface ILogDatabaseService
    {
        /// <summary>
        /// 事务保存一条完整的检测记录
        /// 包括主记录（LogRecord）和所有Pin明细（PinResults）
        /// 使用 EF Core 事务确保原子性写入，防止断电产生孤儿数据
        /// </summary>
        /// <param name="record">待保存的主记录（含PinResults列表）</param>
        Task SaveLogRecordAsync(LogRecord record);

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
        Task<(List<LogRecord> Records, int TotalCount)> QueryLogsAsync(
            string? series = null,
            string? serialNumber = null,
            string? planName = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            string? finalResult = null,
            int pageIndex = 1,
            int pageSize = 20);

        /// <summary>
        /// 获取所有机种名称（去重）
        /// 从 LogRecords 表的 Series 字段提取
        /// </summary>
        Task<List<string>> GetMachineTypesAsync();

        /// <summary>
        /// 获取所有方案名称（去重）
        /// 从 LogRecords 表的 PlanName 字段提取
        /// </summary>
        Task<List<string>> GetPlanNamesAsync();
    }
}