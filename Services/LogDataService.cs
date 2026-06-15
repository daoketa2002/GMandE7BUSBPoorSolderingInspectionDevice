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
    /// <summary>
    /// 日志数据服务 — 负责从「设置/日志数据」文件夹中读取CSV文件，
    /// 解析为 LogDataModel 列表，并提供基于文件名的检索功能。
    /// </summary>
    /// <remarks>
    /// <para><b>文件名规范：</b>CSV 文件必须以 机种名_序列号_方案名称.csv 格式命名。
    /// 例如：E78_SN20261001_SchemeV1.csv、GM5_SN20261001_SchemeV1.csv</para>
    /// <para><b>检索逻辑：</b>解析文件名提取三要素，与检索条件做 Contains 模糊匹配（忽略大小写），
    /// 三个条件为 AND 关系，空白条件视为忽略。只加载文件名匹配的CSV文件，不读取不匹配的文件。</para>
    /// <para><b>CSV编码策略：</b></para>
    /// <para>1. 读取文件头部字节直接匹配 BOM（EF BB BF → UTF-8 等）。</para>
    /// <para>2. 无 BOM → 回退 GB2312（中文 Windows 系统本地 ANSI 编码）。</para>
    /// <para><b>固定列识别：</b></para>
    /// <para>序号、机种名称、序列号、方案名称、检查者、综合判定、日期、时间 — 其余列均视为动态检测项。</para>
    /// </remarks>
    public class LogDataService : ILogDataService
    {
        private readonly ILogger<LogDataService> _logger;
        private readonly string _logDataFolder;

        /// <summary>
        /// 固定列名集合 — 这些列在CSV中有明确语义，不作为动态列处理。
        /// 大小写不敏感匹配。
        /// </summary>
        private static readonly HashSet<string> FixedColumnNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "序号", "机种名称", "序列号", "方案名称", "检查者", "综合判定", "日期", "时间"
        };

        /// <summary>
        /// 文件名解析结果 — 从「机种名_序列号_方案名称.csv」中提取的三要素。
        /// </summary>
        private struct FileNameInfo
        {
            public string MachineType;
            public string SerialNumber;
            public string PlanName;
        }

        /// <summary>
        /// 初始化日志数据服务。
        /// </summary>
        /// <param name="logger">日志记录器</param>
        public LogDataService(ILogger<LogDataService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logDataFolder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                 "日志数据");
            _logger.LogInformation("日志数据服务初始化，数据路径: {Path}", _logDataFolder);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 遍历所有 CSV 文件名（格式：机种名_序列号_方案名称.csv），
        /// 提取第一个下划线之前的部分作为机种名称，去重后排序返回。
        /// </remarks>
        public async Task<List<string>> GetMachineTypesAsync()
        {
            return await Task.Run(() =>
            {
                var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!Directory.Exists(_logDataFolder)) return types.ToList();

                foreach (var filePath in Directory.GetFiles(_logDataFolder, "*.csv"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    var info = ParseFileName(fileName);
                    if (!string.IsNullOrWhiteSpace(info.MachineType))
                        types.Add(info.MachineType);
                }

                return types.OrderBy(t => t).ToList();
            });
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 遍历所有 CSV 文件，解析文件名（机种名_序列号_方案名称.csv），
        /// 与检索条件做 Contains 模糊匹配（忽略大小写）。
        /// 三个条件为 AND 关系，空白条件视为忽略。
        /// 所有条件均为 null/空白时 → 加载全部文件。
        /// </remarks>
        public async Task<List<LogDataModel>> LoadFilteredLogDataAsync(
            string? machineType = null,
            string? serialNumber = null,
            string? planName = null)
        {
            return await Task.Run(() =>
            {
                var result = new List<LogDataModel>();
                if (!Directory.Exists(_logDataFolder))
                {
                    _logger.LogWarning("日志数据文件夹不存在: {Path}", _logDataFolder);
                    return result;
                }

                // 规范化检索条件（null → 忽略该维度）
                var filterMachine = string.IsNullOrWhiteSpace(machineType) ? null : machineType.Trim();
                var filterSerial  = string.IsNullOrWhiteSpace(serialNumber) ? null : serialNumber.Trim();
                var filterPlan    = string.IsNullOrWhiteSpace(planName) ? null : planName.Trim();

                var allFiles = Directory.GetFiles(_logDataFolder, "*.csv");

                foreach (var filePath in allFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);

                    // 解析文件名，提取三要素
                    var info = ParseFileName(fileName);
                    if (info.MachineType == null) continue; // 无法解析的跳过

                    // 文件名三要素与检索条件做 AND 模糊匹配
                    if (!MatchFilter(info.MachineType, filterMachine)) continue;
                    if (!MatchFilter(info.SerialNumber, filterSerial)) continue;
                    if (!MatchFilter(info.PlanName,    filterPlan))   continue;

                    // 文件名匹配 → 加载该文件内容
                    try
                    {
                        result.AddRange(ParseCsvFile(filePath));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "解析CSV文件失败: {File}", Path.GetFileName(filePath));
                    }
                }

                _logger.LogInformation("检索完成 — 条件(机种:{Machine}, 序列号:{Serial}, 方案:{Plan}) → 匹配 {Count} 条",
                    filterMachine ?? "*", filterSerial ?? "*", filterPlan ?? "*", result.Count);
                return result;
            });
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 遍历所有数据的 DynamicItems 字典，收集所有出现的键，去重后保持首次出现的顺序（即CSV文件中的原始列顺序）。
        /// </remarks>
        public List<string> GetAllDynamicHeaders(List<LogDataModel> data)
        {
            return data
                .SelectMany(m => m.DynamicItems.Keys)
                .Distinct()
                .ToList();
        }

        #region 私有方法 — 文件名解析与匹配

        /// <summary>
        /// 解析 CSV 文件名，提取机种名称、序列号、方案名称。
        /// </summary>
        /// <remarks>
        /// <para>文件名格式：机种名_序列号_方案名称.csv</para>
        /// <para>用第一个下划线分割出机种名和剩余部分，再用最后一个下划线分割出方案名称，中间部分为序列号。</para>
        /// <para>例如 "E78_SN20261001_SchemeV1" → (E78, SN20261001, SchemeV1)</para>
        /// <para>解析失败时 MachineType 为 null。</para>
        /// </remarks>
        private static FileNameInfo ParseFileName(string fileNameWithoutExtension)
        {
            var info = new FileNameInfo();

            // 找到第一个 '_' → 左边是机种名
            int firstUnderscore = fileNameWithoutExtension.IndexOf('_');
            if (firstUnderscore <= 0) return info; // 解析失败
            info.MachineType = fileNameWithoutExtension.Substring(0, firstUnderscore);

            // 找到最后一个 '_' → 右边是方案名称
            int lastUnderscore = fileNameWithoutExtension.LastIndexOf('_');
            if (lastUnderscore <= firstUnderscore) return info; // 解析失败
            info.PlanName = fileNameWithoutExtension.Substring(lastUnderscore + 1);

            // 中间部分 → 序列号
            info.SerialNumber = fileNameWithoutExtension.Substring(
                firstUnderscore + 1,
                lastUnderscore - firstUnderscore - 1);

            return info;
        }

        /// <summary>
        /// 单字段模糊匹配（忽略大小写）。
        /// filter 为 null 时表示忽略该维度，始终返回 true。
        /// </summary>
        private static bool MatchFilter(string? value, string? filter)
        {
            if (filter == null) return true;                        // 空白条件 → 忽略
            if (value == null) return false;                        // 文件名无此字段 → 不匹配
            return value.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region 私有方法 — 编码检测与CSV解析

        /// <summary>
        /// 检测CSV文件的文本编码 — 通过直接读取文件头部原始字节判断 BOM。
        /// </summary>
        /// <remarks>
        /// <para><b>策略（按优先级）：</b></para>
        /// <para>1. 读取文件前4个字节，直接匹配 BOM（字节顺序标记）：
        ///    EF BB BF → UTF-8 | FF FE → UTF-16 LE | FE FF → UTF-16 BE | FF FE 00 00 → UTF-32 LE</para>
        /// <para>2. 无任何 BOM 匹配 → 回退到 GB2312（中文 Windows 系统本地 ANSI 代码页 20936）。</para>
        /// </remarks>
        /// <param name="filePath">CSV文件路径</param>
        /// <returns>文件的编码对象</returns>
        private static Encoding DetectFileEncoding(string filePath)
        {
            var bom = new byte[4];
            int bytesRead;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bytesRead = fs.Read(bom, 0, bom.Length);
            }

            // ① UTF-8 BOM: EF BB BF
            if (bytesRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return Encoding.UTF8;

            // ② UTF-32 LE BOM: FF FE 00 00（必须在 UTF-16 LE 之前检测）
            if (bytesRead >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00)
                return Encoding.UTF32;

            // ③ UTF-16 LE BOM: FF FE
            if (bytesRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                return Encoding.Unicode;

            // ④ UTF-16 BE BOM: FE FF
            if (bytesRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                return Encoding.BigEndianUnicode;

            // ⑤ 无任何 BOM → 回退到中文 ANSI 编码（GB2312 / GBK）
            try { return Encoding.GetEncoding("GB2312"); } catch { }
            try { return Encoding.GetEncoding("GBK"); }     catch { }
            try { return Encoding.GetEncoding(936); }       catch { }
            return Encoding.UTF8;
        }

        /// <summary>
        /// 解析单个CSV文件，返回其中所有数据行的 LogDataModel 列表。
        /// </summary>
        /// <param name="filePath">CSV文件完整路径</param>
        /// <returns>解析后的日志数据模型列表</returns>
        private List<LogDataModel> ParseCsvFile(string filePath)
        {
            var models = new List<LogDataModel>();

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
                    Index        = GetIntValue(headerMap, columns, "序号"),
                    MachineType  = GetStringValue(headerMap, columns, "机种名称"),
                    SerialNumber = GetStringValue(headerMap, columns, "序列号"),
                    PlanName     = GetStringValue(headerMap, columns, "方案名称"),
                    Inspector    = GetStringValue(headerMap, columns, "检查者"),
                    Judgment     = GetStringValue(headerMap, columns, "综合判定"),
                    Date         = GetStringValue(headerMap, columns, "日期"),
                    Time         = GetStringValue(headerMap, columns, "时间")
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

        /// <summary>
        /// 解析CSV文件中的一行文本，返回列值数组（RFC 4180 标准）。
        /// </summary>
        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new System.Text.StringBuilder();

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