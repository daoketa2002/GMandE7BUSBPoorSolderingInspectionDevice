using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    public class LogDataService : ILogDataService
    {
        private readonly ILogger<LogDataService> _logger;
        private readonly string _logDataFolder;

        private static readonly HashSet<string> FixedColumnNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "序号", "机种名称", "序列号", "方案名称", "检查者", "综合判定", "日期", "时间"
        };

        public LogDataService(ILogger<LogDataService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logDataFolder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "设置", "日志数据");
            _logger.LogInformation("日志数据服务初始化，数据路径: {Path}", _logDataFolder);
        }

        public async Task<List<string>> GetMachineTypesAsync()
        {
            return await Task.Run(() =>
            {
                var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!Directory.Exists(_logDataFolder)) return types.ToList();

                foreach (var file in Directory.GetFiles(_logDataFolder, "*.csv"))
                {
                    try
                    {
                        using var reader = new StreamReader(file, System.Text.Encoding.UTF8);
                        reader.ReadLine();
                        var line = reader.ReadLine();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var columns = ParseCsvLine(line);
                            if (columns.Length > 1 && !string.IsNullOrWhiteSpace(columns[1]))
                                types.Add(columns[1].Trim());
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "读取文件 {File} 时出错，已跳过", Path.GetFileName(file));
                    }
                }
                return types.OrderBy(t => t).ToList();
            });
        }

        public async Task<List<LogDataModel>> LoadAllLogDataAsync()
        {
            return await Task.Run(() =>
            {
                var result = new List<LogDataModel>();
                if (!Directory.Exists(_logDataFolder))
                {
                    _logger.LogWarning("日志数据文件夹不存在: {Path}", _logDataFolder);
                    return result;
                }

                var csvFiles = Directory.GetFiles(_logDataFolder, "*.csv");
                _logger.LogInformation("找到 {Count} 个CSV文件", csvFiles.Length);

                foreach (var file in csvFiles)
                {
                    try
                    {
                        result.AddRange(ParseCsvFile(file));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "解析CSV文件失败: {File}", Path.GetFileName(file));
                    }
                }

                _logger.LogInformation("共加载 {Count} 条日志数据", result.Count);
                return result;
            });
        }

        public List<LogDataModel> Search(IEnumerable<LogDataModel> source,
            string? machineType, string? serialNumber, string? planName)
        {
            var query = source.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(machineType))
                query = query.Where(m =>
                    m.MachineType.Contains(machineType, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(serialNumber))
                query = query.Where(m =>
                    m.SerialNumber.Contains(serialNumber, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(planName))
                query = query.Where(m =>
                    m.PlanName.Contains(planName, StringComparison.OrdinalIgnoreCase));

            return query.ToList();
        }

        public List<string> GetAllDynamicHeaders(List<LogDataModel> data)
        {
            return data
                .SelectMany(m => m.DynamicItems.Keys)
                .Distinct()
                .OrderBy(h => h)
                .ToList();
        }

        #region 私有方法

        // 检测文件编码
        private static Encoding DetectFileEncoding(string filePath)
        {
            using var reader = new StreamReader(filePath, Encoding.Default, true);
            // 读取BOM判断编码
            reader.Peek();
            return reader.CurrentEncoding;
        }

        private List<LogDataModel> ParseCsvFile(string filePath)
        {
            var models = new List<LogDataModel>();
            // 自动检测编码
            var encoding = DetectFileEncoding(filePath);
            var lines = File.ReadAllLines(filePath, encoding);
            if (lines.Length < 2) return models;

            var headers = ParseCsvLine(lines[0]);
            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
                headerMap[headers[i].Trim()] = i;

            var dynamicHeaders = headers
                .Select(h => h.Trim())
                .Where(h => !FixedColumnNames.Contains(h))
                .ToList();

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                var columns = ParseCsvLine(lines[i]);
                if (columns.Length == 0) continue;

                var model = new LogDataModel
                {
                    Index = GetIntValue(headerMap, columns, "序号"),
                    MachineType = GetStringValue(headerMap, columns, "机种名称"),
                    SerialNumber = GetStringValue(headerMap, columns, "序列号"),
                    PlanName = GetStringValue(headerMap, columns, "方案名称"),
                    Inspector = GetStringValue(headerMap, columns, "检查者"),
                    Judgment = GetStringValue(headerMap, columns, "综合判定"),
                    Date = GetStringValue(headerMap, columns, "日期"),
                    Time = GetStringValue(headerMap, columns, "时间")
                };

                foreach (var dh in dynamicHeaders)
                {
                    if (headerMap.TryGetValue(dh, out var colIndex) && colIndex < columns.Length)
                        model.DynamicItems[dh] = columns[colIndex].Trim();
                }

                models.Add(model);
            }

            return models;
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new System.Text.StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"' && (i == 0 || line[i - 1] != '\\')) // 处理转义引号
                {
                    inQuotes = !inQuotes;
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
            // 去除首尾空格，处理引号
            var processed = field.Trim();
            if (processed.StartsWith("\"") && processed.EndsWith("\""))
            {
                processed = processed.Substring(1, processed.Length - 2);
            }
            // 处理转义字符
            processed = processed.Replace("\"\"", "\"");
            return processed;
        }

        private static string GetStringValue(Dictionary<string, int> headerMap, string[] columns, string columnName)
        {
            if (headerMap.TryGetValue(columnName, out var index) && index < columns.Length)
                return columns[index].Trim();
            return string.Empty;
        }

        private static int GetIntValue(Dictionary<string, int> headerMap, string[] columns, string columnName)
        {
            var str = GetStringValue(headerMap, columns, columnName);
            return int.TryParse(str, out var val) ? val : 0;
        }

        #endregion
    }
}