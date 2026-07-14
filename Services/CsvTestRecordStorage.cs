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
    /// {根目录}/TestLog/{yyyy-MM}/{机种名称}_{方案名称}.csv
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
        private readonly MonthlyLogIndexService _monthlyLogIndexService;

        private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _fileLocks = new();

        private static readonly HashSet<string> FixedColumnNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "序号", "机种名称", "序列号", "方案名称", "检查者", "综合判定", "日期", "时间", "方案版本"
        };

        public CsvTestRecordStorage(
            CsvStoragePathManager pathManager,
            MonthlyLogIndexService monthlyLogIndexService,
            ILogger<CsvTestRecordStorage> logger)
        {
            _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
            _monthlyLogIndexService = monthlyLogIndexService ?? throw new ArgumentNullException(nameof(monthlyLogIndexService));
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
                string monthFolder = _pathManager.GetMonthFolderPath(recordDate);
                Directory.CreateDirectory(monthFolder);

                string baseFileName = _pathManager.GetBaseFileName(effectiveMachineType, record.PlanName);
                string expectedHeader = BuildCsvHeader(record);
                string logicalLockKey = Path.Combine(monthFolder, baseFileName);
                var fileLock = _fileLocks.GetOrAdd(logicalLockKey, _ => new ReaderWriterLockSlim());
                string writtenFileName = string.Empty;
                int writtenRowNumber = 0;

                await Task.Run(() =>
                {
                    if (!fileLock.TryEnterWriteLock(TimeSpan.FromSeconds(30)))
                    {
                        throw new TimeoutException($"获取文件写锁超时: {logicalLockKey}");
                    }

                    try
                    {
                        string filePath = SelectWritableCsvFile(monthFolder, baseFileName, expectedHeader, record.PlanVersion);
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

                        writtenFileName = Path.GetFileName(filePath);
                        writtenRowNumber = rowIndex;

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

                if (!string.IsNullOrWhiteSpace(writtenFileName) && writtenRowNumber > 0)
                {
                    try
                    {
                        await _monthlyLogIndexService.AppendAsync(monthFolder, record, writtenFileName, writtenRowNumber);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[日志索引] 追加失败，正式 CSV 已保留: {FileName}", writtenFileName);
                    }
                }
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
                    var entries = ReadOrRebuildMonthIndex(monthFolder);
                    var hit = entries.FirstOrDefault(entry =>
                        string.Equals(entry.MachineType, expectedMachineType, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(entry.SerialNumber, expectedSerialNumber, StringComparison.OrdinalIgnoreCase)
                        && entry.Timestamp >= startTime
                        && entry.Timestamp <= endTime);

                    if (hit != null)
                    {
                        _logger.LogWarning(
                            "[重复测试] 最近记录命中 - 机种:{MachineType}, SN:{SerialNumber}, 文件:{FileName}",
                            machineType, serialNumber, hit.FileName);
                        return true;
                    }
                }

                return false;
            });
        }

        private string BuildCsvHeader(LogRecord record)
        {
            return CsvRecordFormatter.BuildHeader(record);
        }

        /// <summary>
        /// 构建 CSV 数据行
        /// 格式：序号,机种名称,序列号,方案名称,检查者,综合判定,{动态值...},日期,时间,方案版本
        /// </summary>
        private string BuildCsvDataRow(LogRecord record, int rowIndex)
        {
            return CsvRecordFormatter.BuildDataRow(record, rowIndex);
        }

        private static string EscapeCsvField(string field)
        {
            return CsvRecordFormatter.EscapeField(field);
        }

        private string SelectWritableCsvFile(string monthFolder, string baseFileName, string expectedHeader, int planVersion)
        {
            var existingFiles = GetExistingPlanFiles(monthFolder, baseFileName);
            if (existingFiles.Count == 0)
            {
                return Path.Combine(monthFolder, $"{baseFileName}.csv");
            }

            var lastFile = existingFiles.OrderByDescending(f => f.Number).First();
            var rowCount = _pathManager.CountDataRows(lastFile.FullPath);
            var headerCompatible = IsHeaderCompatible(lastFile.FullPath, expectedHeader);
            var lastVersion = GetLastRecordPlanVersion(lastFile.FullPath);
            var samePlanVersion = lastVersion == Math.Max(1, planVersion);

            if (headerCompatible && samePlanVersion && rowCount < _pathManager.MaxRowsPerFile)
            {
                _logger.LogInformation("[CSV分卷] 当前文件兼容，继续追加: {FileName}", Path.GetFileName(lastFile.FullPath));
                return lastFile.FullPath;
            }

            int newFileNumber = lastFile.Number + 1;
            var newFilePath = Path.Combine(monthFolder, $"{baseFileName}({newFileNumber}).csv");

            if (!headerCompatible)
            {
                _logger.LogWarning("[CSV分卷] 表头变化，创建新分卷: {FileName}", Path.GetFileName(newFilePath));
            }
            else if (!samePlanVersion)
            {
                _logger.LogWarning("[CSV分卷] 方案版本变化 V{OldVersion} -> V{NewVersion}，创建新分卷: {FileName}",
                    lastVersion, Math.Max(1, planVersion), Path.GetFileName(newFilePath));
            }
            else
            {
                _logger.LogWarning("[CSV分卷] 文件达到 {MaxRows} 行，创建新分卷: {FileName}",
                    _pathManager.MaxRowsPerFile, Path.GetFileName(newFilePath));
            }

            return newFilePath;
        }

        private static List<CsvPlanFileInfo> GetExistingPlanFiles(string monthFolder, string baseFileName)
        {
            var result = new List<CsvPlanFileInfo>();
            if (!Directory.Exists(monthFolder))
                return result;

            foreach (var filePath in Directory.GetFiles(monthFolder, $"{baseFileName}*.csv"))
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                if (string.Equals(fileName, baseFileName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new CsvPlanFileInfo(filePath, 0));
                    continue;
                }

                if (fileName.StartsWith(baseFileName + "(", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(")", StringComparison.Ordinal))
                {
                    var numberText = fileName.Substring(baseFileName.Length + 1, fileName.Length - baseFileName.Length - 2);
                    if (int.TryParse(numberText, out var number) && number > 0)
                    {
                        result.Add(new CsvPlanFileInfo(filePath, number));
                    }
                }
            }

            return result;
        }

        private static bool IsHeaderCompatible(string filePath, string expectedHeader)
        {
            try
            {
                if (!File.Exists(filePath))
                    return true;

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, DetectFileEncoding(filePath), detectEncodingFromByteOrderMarks: true);
                var existingHeader = reader.ReadLine()?.TrimStart('\uFEFF') ?? string.Empty;
                return string.Equals(existingHeader, expectedHeader, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static int GetLastRecordPlanVersion(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return 1;

                var lines = File.ReadAllLines(filePath, DetectFileEncoding(filePath));
                if (lines.Length < 2)
                    return 1;

                var headers = ParseCsvLine(lines[0]);
                var headerMap = BuildHeaderMap(headers);
                var lastDataLine = lines.LastOrDefault(line => !string.IsNullOrWhiteSpace(line) && line != lines[0]);
                if (string.IsNullOrWhiteSpace(lastDataLine))
                    return 1;

                var columns = ParseCsvLine(lastDataLine);
                return ParsePlanVersion(GetColumnValue(headerMap, columns, "方案版本"));
            }
            catch
            {
                return 1;
            }
        }

        private sealed record CsvPlanFileInfo(string FullPath, int Number);

        #region CSV 读取与解析

        /// <summary>
        /// 加载并筛选所有匹配的 CSV 记录
        /// 
        /// 优化策略：
        /// 1. 如果指定了日期范围，只扫描对应月份文件夹
        /// 2. 如果指定了机种名称，只加载文件名匹配的 CSV
        /// 3. 其他条件在内存中筛选
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

        private List<LogIndexEntry> ReadOrRebuildMonthIndex(string monthFolder)
        {
            try
            {
                var indexPath = Path.Combine(monthFolder, MonthlyLogIndexService.IndexFileName);
                if (!File.Exists(indexPath))
                {
                    _monthlyLogIndexService.RebuildMonthIndexAsync(monthFolder).GetAwaiter().GetResult();
                }

                return _monthlyLogIndexService.ReadMonthIndexAsync(monthFolder).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[日志索引] 读取或重建索引失败: {MonthFolder}", monthFolder);
                return new List<LogIndexEntry>();
            }
        }

        /// <summary>
        /// 解析单个 CSV 文件为 LogRecord 列表
        /// </summary>
        private static Dictionary<string, int> BuildHeaderMap(string[] headers)
        {
            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                var headerName = headers[i].Trim().TrimStart('\uFEFF');
                if (!headerMap.ContainsKey(headerName))
                {
                    headerMap[headerName] = i;
                }
            }

            return headerMap;
        }

        private static string GetColumnValue(Dictionary<string, int> headerMap, string[] columns, string columnName)
        {
            if (headerMap.TryGetValue(columnName, out int index) && index < columns.Length)
                return columns[index].Trim();
            return string.Empty;
        }

        private static int ParsePlanVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 1;

            value = value.Trim();
            if (value.StartsWith("V", StringComparison.OrdinalIgnoreCase))
                value = value[1..];

            return int.TryParse(value, out var version) && version > 0 ? version : 1;
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
