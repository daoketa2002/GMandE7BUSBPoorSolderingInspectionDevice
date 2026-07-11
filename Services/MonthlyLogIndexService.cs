// ============================================================
// 文件: Services/MonthlyLogIndexService.cs
// 描述: 月度日志索引服务，负责 record-index.csv 追加、读取与重建
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 月度日志索引服务。索引是加速层，正式 CSV 才是事实源。
    /// </summary>
    public class MonthlyLogIndexService
    {
        public const string IndexFileName = "record-index.csv";
        public const string Header = "Timestamp,MachineType,SerialNumber,PlanName,PlanVersion,FinalResult,FileName,RowNumber";

        private readonly ILogger<MonthlyLogIndexService> _logger;

        public MonthlyLogIndexService(ILogger<MonthlyLogIndexService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task AppendAsync(string monthFolder, LogRecord record, string fileName, int rowNumber)
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(monthFolder);
                var indexPath = Path.Combine(monthFolder, IndexFileName);
                var exists = File.Exists(indexPath);

                using var writer = new StreamWriter(indexPath, append: true, new UTF8Encoding(true));
                if (!exists)
                {
                    writer.WriteLine(Header);
                }

                writer.WriteLine(string.Join(",",
                    Escape(record.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                    Escape(!string.IsNullOrWhiteSpace(record.MachineType) ? record.MachineType : record.Series),
                    Escape(record.SerialNumber),
                    Escape(record.PlanName),
                    Math.Max(1, record.PlanVersion).ToString(CultureInfo.InvariantCulture),
                    Escape(record.FinalResult),
                    Escape(fileName),
                    rowNumber.ToString(CultureInfo.InvariantCulture)));

                _logger.LogInformation("[日志索引] 追加成功: {FileName} 行{RowNumber}", fileName, rowNumber);
            });
        }

        public async Task<List<LogIndexEntry>> ReadMonthIndexAsync(string monthFolder)
        {
            return await Task.Run(() =>
            {
                var indexPath = Path.Combine(monthFolder, IndexFileName);
                if (!File.Exists(indexPath))
                {
                    _logger.LogWarning("[日志索引] 索引缺失: {IndexPath}", indexPath);
                    return new List<LogIndexEntry>();
                }

                return ReadIndexFile(indexPath);
            });
        }

        public async Task RebuildMonthIndexAsync(string monthFolder)
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(monthFolder);
                _logger.LogWarning("[日志索引] 开始重建月份索引: {MonthFolder}", monthFolder);

                var indexPath = Path.Combine(monthFolder, IndexFileName);
                var tempPath = indexPath + ".tmp";

                using (var writer = new StreamWriter(tempPath, append: false, new UTF8Encoding(true)))
                {
                    writer.WriteLine(Header);

                    foreach (var csvFile in Directory.GetFiles(monthFolder, "*.csv")
                                 .Where(path => !string.Equals(Path.GetFileName(path), IndexFileName, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (var entry in BuildEntriesFromCsv(csvFile))
                        {
                            writer.WriteLine(string.Join(",",
                                Escape(entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                                Escape(entry.MachineType),
                                Escape(entry.SerialNumber),
                                Escape(entry.PlanName),
                                Math.Max(1, entry.PlanVersion).ToString(CultureInfo.InvariantCulture),
                                Escape(entry.FinalResult),
                                Escape(entry.FileName),
                                entry.RowNumber.ToString(CultureInfo.InvariantCulture)));
                        }
                    }
                }

                if (File.Exists(indexPath))
                {
                    File.Delete(indexPath);
                }

                File.Move(tempPath, indexPath);
                _logger.LogWarning("[日志索引] 重建完成: {IndexPath}", indexPath);
            });
        }

        private static List<LogIndexEntry> BuildEntriesFromCsv(string csvFile)
        {
            var entries = new List<LogIndexEntry>();
            var lines = File.ReadAllLines(csvFile, DetectFileEncoding(csvFile));
            if (lines.Length < 2)
                return entries;

            var headers = ParseCsvLine(lines[0]);
            var headerMap = BuildHeaderMap(headers);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var columns = ParseCsvLine(lines[i]);
                entries.Add(new LogIndexEntry
                {
                    Timestamp = ParseDateTime(
                        GetColumnValue(headerMap, columns, "日期"),
                        GetColumnValue(headerMap, columns, "时间")),
                    MachineType = GetColumnValue(headerMap, columns, "机种名称"),
                    SerialNumber = GetColumnValue(headerMap, columns, "序列号"),
                    PlanName = GetColumnValue(headerMap, columns, "方案名称"),
                    PlanVersion = ParsePlanVersion(GetColumnValue(headerMap, columns, "方案版本")),
                    FinalResult = GetColumnValue(headerMap, columns, "综合判定"),
                    FileName = Path.GetFileName(csvFile),
                    RowNumber = i
                });
            }

            return entries;
        }

        private static List<LogIndexEntry> ReadIndexFile(string indexPath)
        {
            var entries = new List<LogIndexEntry>();
            var lines = File.ReadAllLines(indexPath, DetectFileEncoding(indexPath)); 
            if (lines.Length < 2)
                return entries;

            var headers = ParseCsvLine(lines[0]);
            var headerMap = BuildHeaderMap(headers);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var columns = ParseCsvLine(lines[i]);
                entries.Add(new LogIndexEntry
                {
                    Timestamp = DateTime.TryParse(GetColumnValue(headerMap, columns, "Timestamp"), null, DateTimeStyles.RoundtripKind, out var timestamp)
                        ? timestamp
                        : DateTime.MinValue,
                    MachineType = GetColumnValue(headerMap, columns, "MachineType"),
                    SerialNumber = GetColumnValue(headerMap, columns, "SerialNumber"),
                    PlanName = GetColumnValue(headerMap, columns, "PlanName"),
                    PlanVersion = ParsePlanVersion(GetColumnValue(headerMap, columns, "PlanVersion")),
                    FinalResult = GetColumnValue(headerMap, columns, "FinalResult"),
                    FileName = GetColumnValue(headerMap, columns, "FileName"),
                    RowNumber = int.TryParse(GetColumnValue(headerMap, columns, "RowNumber"), out var rowNumber) ? rowNumber : 0
                });
            }

            return entries;
        }

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
            var combined = $"{dateStr} {timeStr}";
            if (DateTime.TryParseExact(combined, "yyyy年MM月dd日 HH时mm分ss秒",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
                return result;

            if (DateTime.TryParseExact(combined, "yyyy/MM/dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                return result;

            return DateTime.TryParse(combined, out result) ? result : DateTime.Now;
        }

        private static string Escape(string field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            return field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
                ? $"\"{field.Replace("\"", "\"\"")}\""
                : field;
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
            return processed.Replace("\"\"", "\"");
        }
    }
}
