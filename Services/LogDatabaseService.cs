// ============================================================
// 文件: Services/LogDatabaseService.cs
// 描述: 日志数据库服务实现 —— 使用 EF Core 操作 SQLite
//      实现 ILogDatabaseService 接口
// 关键特性:
//   - 事务写入（SaveChangesAsync 默认事务）
//   - LINQ 分页查询（Skip/Take）
//   - 自动 Include 导航属性（PinResults）
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Data;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 日志数据库服务实现
    /// 通过 EF Core 的 AppDbContext 操作 SQLite 数据库
    /// 所有写操作使用 SaveChangesAsync 保证事务原子性
    /// </summary>
    public class LogDatabaseService : ILogDatabaseService
    {
        private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
        private readonly ILogger<LogDatabaseService> _logger;

        /// <summary>
        /// 构造函数 —— 使用 DbContextFactory 确保每次操作使用独立的 DbContext 实例
        /// 避免跨线程操作同一 DbContext 导致的并发问题
        /// </summary>
        public LogDatabaseService(
            IDbContextFactory<AppDbContext> dbContextFactory,
            ILogger<LogDatabaseService> logger)
        {
            _dbContextFactory = dbContextFactory
                ?? throw new ArgumentNullException(nameof(dbContextFactory));
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        // ============================================================
        // 事务保存
        // ============================================================

        /// <inheritdoc/>
        /// <remarks>
        /// 使用 EF Core 的 SaveChangesAsync，默认在一个事务中完成所有变更。
        /// 主记录和所有 PinResults 明细要么全部写入，要么全部回滚，
        /// 有效防止断电等异常情况产生孤儿数据。
        /// </remarks>
        public async Task SaveLogRecordAsync(LogRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            // 验证必填字段
            if (string.IsNullOrWhiteSpace(record.SerialNumber))
                throw new ArgumentException("序列号不能为空", nameof(record));
            if (record.PinResults == null || record.PinResults.Count == 0)
                throw new ArgumentException("Pin明细不能为空", nameof(record));

            try
            {
                // 每次操作创建独立的 DbContext，避免跨线程问题
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

                // 设置创建时间戳
                if (record.Timestamp == default)
                    record.Timestamp = DateTime.Now;
                record.CreatedAt = DateTime.Now;

                // 添加主记录（EF Core 会自动跟踪 PinResults 导航属性）
                dbContext.LogRecords.Add(record);

                // 事务保存：主记录 + 所有 PinResults 同时写入
                await dbContext.SaveChangesAsync();

                _logger.LogInformation(
                    "日志保存成功 - SN:{Serial}, 方案:{Plan}, 结果:{Result}, Pin数:{Count}",
                    record.SerialNumber, record.PlanName, record.FinalResult,
                    record.PinResults.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "日志保存失败 - SN:{Serial}", record.SerialNumber);
                throw; // 重新抛出，让调用方处理
            }
        }

        // ============================================================
        // 分页查询
        // ============================================================

        /// <inheritdoc/>
        /// <remarks>
        /// 查询逻辑：
        /// 1. 构建 IQueryable 基础查询（Include PinResults 导航属性）
        /// 2. 逐条件应用筛选（AND 关系）
        /// 3. 先获取 TotalCount（分页前）
        /// 4. 应用排序 + Skip/Take 分页
        /// 5. 返回 (本页数据, 总条数)
        /// </remarks>
        public async Task<(List<LogRecord> Records, int TotalCount)> QueryLogsAsync(
            string? series = null,
            string? serialNumber = null,
            string? planName = null,
            DateTime? startDate = null,
            DateTime? endDate = null,
            string? finalResult = null,
            int pageIndex = 1,
            int pageSize = 20)
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            // 基础查询：包含 PinResults 导航属性，按时间倒序
            IQueryable<LogRecord> query = dbContext.LogRecords
                .Include(r => r.PinResults)
                .AsNoTracking(); // 只读查询，无需跟踪

            // ===== 逐条件应用筛选（AND 关系，null/空白=忽略） =====

            if (!string.IsNullOrWhiteSpace(series))
            {
                query = query.Where(r => r.Series.Contains(series));
            }

            if (!string.IsNullOrWhiteSpace(serialNumber))
            {
                query = query.Where(r => r.SerialNumber.Contains(serialNumber));
            }

            if (!string.IsNullOrWhiteSpace(planName))
            {
                // 方案名称精确匹配（因为是从下拉框选择的）
                query = query.Where(r => r.PlanName == planName);
            }

            if (startDate.HasValue)
            {
                var start = startDate.Value.Date; // 当天 00:00:00
                query = query.Where(r => r.Timestamp >= start);
            }

            if (endDate.HasValue)
            {
                var end = endDate.Value.Date.AddDays(1); // 次日 00:00:00（含当天全部数据）
                query = query.Where(r => r.Timestamp < end);
            }

            if (!string.IsNullOrWhiteSpace(finalResult))
            {
                query = query.Where(r => r.FinalResult == finalResult);
            }

            // ===== 获取总条数（分页前） =====
            var totalCount = await query.CountAsync();

            // 参数验证：页码不能小于1
            if (pageIndex < 1) pageIndex = 1;

            // ===== 排序 + 分页 =====
            var records = await query
                .OrderByDescending(r => r.Timestamp) // 最新记录在前
                .Skip((pageIndex - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            _logger.LogDebug(
                "查询完成 - 条件(Series:{Series}, SN:{SN}, Plan:{Plan}, " +
                "Date:{Start}~{End}, Result:{Result}) → 共{Total}条, 返回{Count}条",
                series ?? "*", serialNumber ?? "*", planName ?? "*",
                startDate?.ToString("yyyy-MM-dd") ?? "*",
                endDate?.ToString("yyyy-MM-dd") ?? "*",
                finalResult ?? "*", totalCount, records.Count);

            return (records, totalCount);
        }

        // ============================================================
        // 辅助查询
        // ============================================================

        /// <inheritdoc/>
        public async Task<List<string>> GetMachineTypesAsync()
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            var types = await dbContext.LogRecords
                .Select(r => r.Series)
                .Distinct()
                .OrderBy(s => s)
                .ToListAsync();

            return types;
        }

        /// <inheritdoc/>
        public async Task<List<string>> GetPlanNamesAsync()
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            var plans = await dbContext.LogRecords
                .Select(r => r.PlanName)
                .Distinct()
                .OrderBy(p => p)
                .ToListAsync();

            return plans;
        }
    }
}