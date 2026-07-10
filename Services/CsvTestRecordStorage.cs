// ============================================================
// 文件: Services/CsvTestRecordStorage.cs
// 描述: CSV 测试记录存储实现 —— 实现 ITestRecordStorage 接口
//      按年月分层存储，支持分卷策略、线程安全写入、编码自动检测
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// CSV 测试记录存储实现
    /// 
    /// 目录结构：
    /// {根目录}/数据/TestLog/{yyyy-MM}/{机种名称}_{方案名称}.csv
    /// 
    /// 特性：
    /// - 按年月自动分层
    /// - 分卷策略：单文件超 MaxRowsPerFile 行自动创建 (1).csv 分卷
    /// - 线程安全：按文件路径独立加锁
    /// - 编码：UTF-8 with BOM
    /// - 解析：自动检测 BOM / GB2312 回退
    /// </summary>
    public class CsvTestRecordStorage : ITestRecordStorage
    {
        private readonly CsvStoragePathManager _pathManager;
        private readonly ILogger<CsvTestRecordStorage> _logger;
        private readonly IPlanStorageService _planStorageService;

        private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _fileLocks = new();

        private static readonly HashSet<string> FixedColumnNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "序号", "机种名称", "序列号", "方案名称", "检查者", "综合判定", "日期", "时间"
        };

        public CsvTestRecordStorage(
            CsvStoragePathManager pathManager,
            IPlanStorageService planStorageService,
            ILogger<CsvTestRecordStorage> logger)
        {
            _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _logger.LogInformation("CsvTestRecordStorage 初始化完成");
        }

        // ============================================================
        // 保存
        // ============================================================

        /// <summary>
        /// 保存一条检测记录到 CSV 文件
        /// 自动写入当前月份文件夹，按机种_方案命名
        ///
        /// 改动说明（方案需求变动）：
        /// 优先使用 MachineType 字段（新版机种名），Series 作为兼容回退
        /// 动态列写入实际测量值（由调用方在 PinResult.Result 中填充）
        /// </summary>
        public async Task SaveRecordAsync(LogRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            // ★ 优先使用 MachineType，兼容旧数据回退到 Series
            string effectiveMachineType = !string.IsNullOrWhiteSpace(record.MachineType)
                ? record.MachineType
                : record.Series;

            if (string.IsNullOrWhiteSpace(effectiveMachineType))
                throw new ArgumentException("机种名称不能为空", nameof(record));
            if (string.IsNullOrWhiteSpace(record.SerialNumber))
                throw new ArgumentException("序列号不能为空", nameof(record));
            if (record.PinResults == null || record.PinResults.Count == 0)
                throw new ArgumentException("Pin明细不能为空", nameof(record));

            try
            {
                // 使用记录的时间戳确定月份（而非当前时间）
                var recordDate = record.Timestamp == default ? DateTime.Now : record.Timestamp;
                string filePath = _pathManager.GetAvailableCsvFilePath(effectiveMachineType, record.PlanName, recordDate);
                var fileLock = _fileLocks.GetOrAdd(filePath, _ => new ReaderWriterLockSlim());

                await Task.Run(() =>
                {
                    if (!fileLock.TryEnterWriteLock(TimeSpan.FromSeconds(30)))
                    {
                        throw new TimeoutException($"获取文件写锁超时: {filePath}");
                    }

                    try
                    {
                        bool fileExists = File.Exists(filePath);
                        int rowIndex = _pathManager.CountDataRows(filePath) + 1;

                        using (var writer = new StreamWriter(
                            filePath, true, new UTF8Encoding(true)))
                        {
                            if (!fileExists)
                            {
                                string header = BuildCsvHeader(record);
                                writer.WriteLine(header);
                                _logger.LogDebug("已写入表头到新文件: {FilePath}", filePath);
                            }

                            string dataRow = BuildCsvDataRow(record, rowIndex);
                            writer.WriteLine(dataRow);
                        }

                        _logger.LogInformation(
                            "CSV保存成功 - 机种:{MachineType}, 方案:{Plan}, SN:{Serial}, 结果:{Result}",
                            effectiveMachineType, record.PlanName, record.SerialNumber,
                            record.FinalResult);
                    }
                    finally
                    {
                        fileLock.ExitWriteLock();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CSV保存失败 - SN:{Serial}", record.SerialNumber);
                throw;
            }
        }

        // ============================================================
        // 查询
        // ============================================================

        /// <summary>
        /// 分页查询检测记录
        /// 根据筛选条件确定需要扫描的月份文件夹范围
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
            return await Task.Run(() =>
            {
                var allRecords = LoadAllFilteredRecords(
                    series, serialNumber, planName,
                    startDate, endDate, finalResult);

                var sortedRecords = allRecords
                    .OrderByDescending(r => r.Timestamp)
                    .ToList();

                int totalCount = sortedRecords.Count;

                if (pageIndex < 1) pageIndex = 1;
                var pagedRecords = sortedRecords
                    .Skip((pageIndex - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                _logger.LogDebug(
                    "CSV查询完成 - 条件(Series:{Series}, SN:{SN}, Plan:{Plan}) → 共{Total}条, 返回{Count}条",
                    series ?? "*", serialNumber ?? "*", planName ?? "*",
                    totalCount, pagedRecords.Count);

                return (pagedRecords, totalCount);
            });
        }

        /// <summary>
        /// 短路查询指定机种和序列号在时间范围内是否已有记录。
        /// 只扫描涉及月份下当前机种的 CSV 文件，避免运行页输入时全量扫盘。
        /// </summary>
        public async Task<bool> ExistsRecentTestRecordAsync(
            string machineType,
            string serialNumber,
            DateTime startTime,
            DateTime endTime)
        {
            if (string.IsNullOrWhiteSpace(machineType) || string.IsNullOrWhiteSpace(serialNumber))
                return false;

            if (endTime < startTime)
                return false;

            return await Task.Run(() =>
            {
                var testLogRoot = _pathManager.GetTestLogRootPath();
                if (!Directory.Exists(testLogRoot))
                    return false;

                var safeMachineType = _pathManager.SanitizeFileName(machineType.Trim());
                var searchPattern = $"{safeMachineType}_*.csv";
                var expectedMachineType = machineType.Trim();
                var expectedSerialNumber = serialNumber.Trim();

                foreach (var monthFolder in GetMonthFoldersInRange(startTime, endTime))
                {
                    string[] csvFiles;
                    try
                    {
                        csvFiles = Directory.GetFiles(monthFolder, searchPattern);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "重复测试检查扫描月份文件夹失败: {Folder}", monthFolder);
                        continue;
                    }

                    foreach (var csvFile in csvFiles)
                    {
                        if (CsvFileContainsRecord(csvFile, expectedMachineType, expectedSerialNumber, startTime, endTime))
                        {
                            _logger.LogWarning(
                                "[重复测试] 最近记录命中 - 机种:{MachineType}, SN:{SerialNumber}, 文件:{FileName}",
                                machineType, serialNumber, Path.GetFileName(csvFile));
                            return true;
                        }
                    }
                }

                return false;
            });
        }

        /// <summary>
        /// 获取所有机种名称
        /// </summary>
        public async Task<List<string>> GetMachineTypesAsync()
        {
            return await Task.Run(() => _pathManager.GetMachineTypes());
        }

        /// <summary>
        /// 获取所有方案名称
        /// </summary>
        public async Task<List<string>> GetPlanNamesAsync()
        {
            return await Task.Run(() => _pathManager.GetPlanNames());
        }

        #region CSV 写入

        /// <summary>
        /// 构建 CSV 表头行
        /// 格式：序号,机种名称,序列号,方案名称,检查者,综合判定,{动态Pin列...},日期,时间
        /// </summary>
        private string BuildCsvHeader(LogRecord record)
        {
            var headerBuilder = new StringBuilder();

            // 固定列（日期和时间在最后）
            headerBuilder.Append("序号,机种名称,序列号,方案名称,检查者,综合判定");

            // 动态 Pin 列（紧跟综合判定之后）
            if (record.PinResults != null)
            {
                foreach (var pinResult in record.PinResults)
                {
                    headerBuilder.Append(',');
                    headerBuilder.Append(EscapeCsvField(pinResult.PinName));
                }
            }

            // 日期和时间放在最后
            headerBuilder.Append(",日期,时间");

            return headerBuilder.ToString();
        }

        /// <summary>
        /// 构建 CSV 数据行
        /// 格式：序号,机种名称,序列号,方案名称,检查者,综合判定,{动态值...},日期,时间
        /// </summary>
        private string BuildCsvDataRow(LogRecord record, int rowIndex)
        {
            var dataBuilder = new StringBuilder();

            // 固定列
            dataBuilder.Append(rowIndex);
            dataBuilder.Append(',');
            // ★ 机种名称优先使用 MachineType，兼容旧数据回退 Series
            dataBuilder.Append(EscapeCsvField(
                !string.IsNullOrWhiteSpace(record.MachineType) ? record.MachineType : record.Series));
            dataBuilder.Append(',');
            dataBuilder.Append(EscapeCsvField(record.SerialNumber));
            dataBuilder.Append(',');
            dataBuilder.Append(EscapeCsvField(record.PlanName));
            dataBuilder.Append(',');
            dataBuilder.Append(EscapeCsvField(record.Operator));
            dataBuilder.Append(',');
            dataBuilder.Append(EscapeCsvField(record.FinalResult));

            // 动态 Pin 结果列
            if (record.PinResults != null)
            {
                foreach (var pinResult in record.PinResults)
                {
                    dataBuilder.Append(',');
                    dataBuilder.Append(EscapeCsvField(pinResult.Result));
                }
            }

            // 日期和时间放在最后（中文格式）
            dataBuilder.Append(',');
            dataBuilder.Append(record.Timestamp.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture));
            dataBuilder.Append(',');
            dataBuilder.Append(record.Timestamp.ToString("HH时mm分ss秒", CultureInfo.InvariantCulture));

            return dataBuilder.ToString();
        }

        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return field;
        }

        #endregion

        #region CSV 读取与解析

        /// <summary>
        /// 加载并筛选所有匹配的 CSV 记录
        /// 
        /// 优化策略：
        /// 1. 如果指定了日期范围，只扫描对应月份文件夹
        /// 2. 如果指定了机种名称，只加载文件名匹配的 CSV
        /// 3. 其他条件在内存中筛选
        /// </summary>
        private List<LogRecord> LoadAllFilteredRecords(
            string? series,
            string? serialNumber,
            string? planName,
            DateTime? startDate,
            DateTime? endDate,
            string? finalResult)
        {
            var allRecords = new List<LogRecord>();
            var testLogRoot = _pathManager.GetTestLogRootPath();

            if (!Directory.Exists(testLogRoot))
                return allRecords;

            // 确定需要扫描的月份文件夹
            List<string> monthFolders;
            if (startDate.HasValue || endDate.HasValue)
            {
                // 有日期范围 → 只扫描相关月份
                monthFolders = GetMonthFoldersInRange(startDate, endDate);
            }
            else
            {
                // 无日期范围 → 扫描所有月份
                try
                {
                    monthFolders = Directory.GetDirectories(testLogRoot)
                        .OrderByDescending(d => d)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "扫描 TestLog 子文件夹失败");
                    return allRecords;
                }
            }

            // 遍历月份文件夹
            foreach (var monthFolder in monthFolders)
            {
                try
                {
                    // 确定要加载的 CSV 文件
                    string searchPattern;
                    if (!string.IsNullOrWhiteSpace(series))
                    {
                        var safeSeries = _pathManager.SanitizeFileName(series.Trim());
                        if (!string.IsNullOrWhiteSpace(planName))
                        {
                            // 同时指定机种和方案 → 精确匹配
                            var safePlan = _pathManager.SanitizeFileName(planName.Trim());
                            searchPattern = $"{safeSeries}_{safePlan}*.csv";
                        }
                        else
                        {
                            // 只指定机种 → 匹配该机种所有方案
                            searchPattern = $"{safeSeries}_*.csv";
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(planName))
                    {
                        // 只指定方案 → 匹配所有机种的该方案
                        var safePlan = _pathManager.SanitizeFileName(planName.Trim());
                        searchPattern = $"*_{safePlan}*.csv";
                    }
                    else
                    {
                        searchPattern = "*.csv";
                    }

                    var csvFiles = Directory.GetFiles(monthFolder, searchPattern);

                    foreach (var csvFile in csvFiles)
                    {
                        try
                        {
                            var records = ParseCsvFile(csvFile);
                            allRecords.AddRange(records);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "解析CSV文件失败: {File}", Path.GetFileName(csvFile));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "扫描月份文件夹失败: {Folder}", monthFolder);
                }
            }

            // 内存筛选
            var filtered = allRecords.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(serialNumber))
            {
                var sn = serialNumber.Trim();
                filtered = filtered.Where(r =>
                    r.SerialNumber.Contains(sn, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(finalResult) && finalResult != "全部")
            {
                filtered = filtered.Where(r =>
                    string.Equals(r.FinalResult, finalResult, StringComparison.OrdinalIgnoreCase));
            }

            if (startDate.HasValue)
            {
                var start = startDate.Value.Date;
                filtered = filtered.Where(r => r.Timestamp.Date >= start);
            }

            if (endDate.HasValue)
            {
                var end = endDate.Value.Date;
                filtered = filtered.Where(r => r.Timestamp.Date <= end);
            }

            return filtered.ToList();
        }

        /// <summary>
        /// 获取日期范围内的月份文件夹列表
        /// </summary>
        private List<string> GetMonthFoldersInRange(DateTime? startDate, DateTime? endDate)
        {
            var testLogRoot = _pathManager.GetTestLogRootPath();
            var result = new List<string>();

            if (!Directory.Exists(testLogRoot))
                return result;

            // 确定起始和结束月份
            DateTime startMonth = startDate.HasValue
                ? new DateTime(startDate.Value.Year, startDate.Value.Month, 1)
                : DateTime.Now.AddMonths(-12); // 默认往前12个月

            DateTime endMonth = endDate.HasValue
                ? new DateTime(endDate.Value.Year, endDate.Value.Month, 1)
                : new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);

            // 遍历所有月份文件夹
            foreach (var dir in Directory.GetDirectories(testLogRoot))
            {
                var folderName = Path.GetFileName(dir);
                if (DateTime.TryParseExact(folderName, "yyyy-MM",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime folderDate))
                {
                    var folderMonth = new DateTime(folderDate.Year, folderDate.Month, 1);
                    if (folderMonth >= startMonth && folderMonth <= endMonth)
                    {
                        result.Add(dir);
                    }
                }
            }

            return result.OrderBy(d => d).ToList();
        }

        private bool CsvFileContainsRecord(
            string filePath,
            string machineType,
            string serialNumber,
            DateTime startTime,
            DateTime endTime)
        {
            try
            {
                var encoding = DetectFileEncoding(filePath);
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

                var headerLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(headerLine))
                    return false;

                var headers = ParseCsvLine(headerLine);
                var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Length; i++)
                {
                    var headerName = headers[i].Trim();
                    if (!headerMap.ContainsKey(headerName))
                    {
                        headerMap[headerName] = i;
                    }
                }

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var columns = ParseCsvLine(line);
                    var rowMachineType = GetColumnValue(headerMap, columns, "机种名称");
                    var rowSerialNumber = GetColumnValue(headerMap, columns, "序列号");

                    if (!string.Equals(rowMachineType, machineType, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(rowSerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var timestamp = ParseDateTime(
                        GetColumnValue(headerMap, columns, "日期"),
                        GetColumnValue(headerMap, columns, "时间"));

                    if (timestamp >= startTime && timestamp <= endTime)
                        return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "重复测试检查解析 CSV 失败: {File}", Path.GetFileName(filePath));
            }

            return false;
        }

        /// <summary>
        /// 解析单个 CSV 文件为 LogRecord 列表
        /// </summary>
        private List<LogRecord> ParseCsvFile(string filePath)
        {
            var records = new List<LogRecord>();

            var encoding = DetectFileEncoding(filePath);
            var lines = File.ReadAllLines(filePath, encoding);

            if (lines.Length < 2) return records;

            var headers = ParseCsvLine(lines[0]);
            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                var headerName = headers[i].Trim();
                if (!headerMap.ContainsKey(headerName))
                {
                    headerMap[headerName] = i;
                }
            }

            var dynamicHeaders = headers
                .Select(h => h.Trim())
                .Where(h => !FixedColumnNames.Contains(h) && !string.IsNullOrWhiteSpace(h))
                .ToList();

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                var columns = ParseCsvLine(lines[i]);
                if (columns.Length == 0) continue;

                string dateStr = GetColumnValue(headerMap, columns, "日期");
                string timeStr = GetColumnValue(headerMap, columns, "时间");
                DateTime timestamp = ParseDateTime(dateStr, timeStr);

                var record = new LogRecord
                {
                    Timestamp = timestamp,
                    Series = GetColumnValue(headerMap, columns, "机种名称"),
                    SerialNumber = GetColumnValue(headerMap, columns, "序列号"),
                    PlanName = GetColumnValue(headerMap, columns, "方案名称"),
                    Operator = GetColumnValue(headerMap, columns, "检查者"),
                    FinalResult = GetColumnValue(headerMap, columns, "综合判定"),
                    PinResults = new List<PinResult>()
                };

                foreach (var dh in dynamicHeaders)
                {
                    record.PinResults.Add(new PinResult
                    {
                        PinName = dh,
                        Result = GetColumnValue(headerMap, columns, dh)
                    });
                }

                records.Add(record);
            }

            return records;
        }

        private static string GetColumnValue(Dictionary<string, int> headerMap, string[] columns, string columnName)
        {
            if (headerMap.TryGetValue(columnName, out int index) && index < columns.Length)
                return columns[index].Trim();
            return string.Empty;
        }

        private static DateTime ParseDateTime(string dateStr, string timeStr)
        {
            // 先尝试最常用的格式
            string combined = $"{dateStr} {timeStr}";

            // 格式1：yyyy年MM月dd日 HH时mm分ss秒（新格式）
            if (DateTime.TryParseExact(combined,
                "yyyy年MM月dd日 HH时mm分ss秒",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result))
                return result;

            // 格式2：yyyy/MM/dd HH:mm:ss（兼容旧格式）
            if (DateTime.TryParseExact(combined,
                "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                return result;

            // 格式3：通用尝试
            if (DateTime.TryParse(combined, out result))
                return result;

            // 解析失败输出调试信息
            System.Diagnostics.Debug.WriteLine(
                $"CSV日期解析失败 - 日期:'{dateStr}', 时间:'{timeStr}'，使用当前时间兜底");

            return DateTime.Now;
        }

        private static Encoding DetectFileEncoding(string filePath)
        {
            var bom = new byte[4];
            int bytesRead;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bytesRead = fs.Read(bom, 0, bom.Length);
            }

            if (bytesRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return Encoding.UTF8;

            if (bytesRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                return Encoding.Unicode;

            try { return Encoding.GetEncoding("GB2312"); } catch { }
            try { return Encoding.GetEncoding("GBK"); } catch { }
            try { return Encoding.GetEncoding(936); } catch { }

            return Encoding.UTF8;
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(ProcessCsvField(current.ToString()));
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            result.Add(ProcessCsvField(current.ToString()));
            return result.ToArray();
        }

        private static string ProcessCsvField(string field)
        {
            var processed = field.Trim();
            if (processed.StartsWith("\"") && processed.EndsWith("\""))
                processed = processed.Substring(1, processed.Length - 2);
            processed = processed.Replace("\"\"", "\"");
            return processed;
        }

        #endregion
    }
}
