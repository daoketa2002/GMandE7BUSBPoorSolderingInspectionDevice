// ============================================================
// 文件: Services/SqliteTestRecordStorage.cs
// 描述: SQLite 测试记录存储实现 —— 实现 ITestRecordStorage 接口
//      使用 EF Core 操作 SQLite 数据库
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
    /// SQLite 测试记录存储实现
    /// 通过 EF Core 的 AppDbContext 操作 SQLite 数据库
    /// 所有写操作使用 SaveChangesAsync 保证事务原子性
    /// </summary>
    public class SqliteTestRecordStorage : ITestRecordStorage
    {
        private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
        private readonly ILogger<SqliteTestRecordStorage> _logger;

        /// <summary>
        /// 构造函数
        /// </summary>
        public SqliteTestRecordStorage(
            IDbContextFactory<AppDbContext> dbContextFactory,
            ILogger<SqliteTestRecordStorage> logger)
        {
            _dbContextFactory = dbContextFactory
                ?? throw new ArgumentNullException(nameof(dbContextFactory));
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 事务保存一条完整的检测记录到 SQLite
        /// </summary>
        public async Task SaveRecordAsync(LogRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            if (string.IsNullOrWhiteSpace(record.SerialNumber))
                throw new ArgumentException("序列号不能为空", nameof(record));
            if (record.PinResults == null || record.PinResults.Count == 0)
                throw new ArgumentException("Pin明细不能为空", nameof(record));

            try
            {
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

                if (record.Timestamp == default)
                    record.Timestamp = DateTime.Now;
                record.CreatedAt = DateTime.Now;

                dbContext.LogRecords.Add(record);
                await dbContext.SaveChangesAsync();

                _logger.LogInformation(
                    "SQLite保存成功 - SN:{Serial}, 方案:{Plan}, 结果:{Result}, Pin数:{Count}",
                    record.SerialNumber, record.PlanName, record.FinalResult,
                    record.PinResults.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SQLite保存失败 - SN:{Serial}", record.SerialNumber);
                throw;
            }
        }

        /// <summary>
        /// 分页查询检测记录
        /// </summary>
        public async Task<(List<LogRecord> Records, int TotalCount)> QueryRecordsAsync(
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

            IQueryable<LogRecord> query = dbContext.LogRecords
                .Include(r => r.PinResults)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(series))
                query = query.Where(r => r.Series.Contains(series));

            if (!string.IsNullOrWhiteSpace(serialNumber))
                query = query.Where(r => r.SerialNumber.Contains(serialNumber));

            if (!string.IsNullOrWhiteSpace(planName))
                query = query.Where(r => r.PlanName == planName);

            if (startDate.HasValue)
            {
                var start = startDate.Value.Date;
                query = query.Where(r => r.Timestamp >= start);
            }

            if (endDate.HasValue)
            {
                var end = endDate.Value.Date.AddDays(1);
                query = query.Where(r => r.Timestamp < end);
            }

            if (!string.IsNullOrWhiteSpace(finalResult))
                query = query.Where(r => r.FinalResult == finalResult);

            var totalCount = await query.CountAsync();

            if (pageIndex < 1) pageIndex = 1;

            var records = await query
                .OrderByDescending(r => r.Timestamp)
                .Skip((pageIndex - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            _logger.LogDebug(
                "SQLite查询完成 - 共{Total}条, 返回{Count}条",
                totalCount, records.Count);

            return (records, totalCount);
        }

        public async Task<bool> ExistsRecentTestRecordAsync(
            string machineType,
            string serialNumber,
            DateTime startTime,
            DateTime endTime)
        {
            if (string.IsNullOrWhiteSpace(machineType) || string.IsNullOrWhiteSpace(serialNumber))
                return false;

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var expectedMachineType = machineType.Trim();
            var expectedSerialNumber = serialNumber.Trim();

            return await dbContext.LogRecords.AsNoTracking().AnyAsync(r =>
                (r.MachineType == expectedMachineType || r.Series == expectedMachineType)
                && r.SerialNumber == expectedSerialNumber
                && r.Timestamp >= startTime
                && r.Timestamp <= endTime);
        }

        /// <summary>
        /// 获取所有机种名称
        /// </summary>
        public async Task<List<string>> GetMachineTypesAsync()
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            return await dbContext.LogRecords
                .Select(r => r.Series)
                .Distinct()
                .OrderBy(s => s)
                .ToListAsync();
        }

        /// <summary>
        /// 获取所有方案名称
        /// </summary>
        public async Task<List<string>> GetPlanNamesAsync()
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            return await dbContext.LogRecords
                .Select(r => r.PlanName)
                .Distinct()
                .OrderBy(p => p)
                .ToListAsync();
        }
    }
}
