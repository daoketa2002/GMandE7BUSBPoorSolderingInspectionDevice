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
    /// 解析为 LogDataModel 列表，并提供检索和动态列提取功能。
    /// </summary>
    /// <remarks>
    /// <para><b>CSV编码策略：</b></para>
    /// <para>1. 通过 BOM（字节顺序标记）检测是否是 UTF-8/UTF-16/UTF-32。</para>
    /// <para>2. 若无 BOM，回退到 GB2312（中文Windows系统本地编码），以兼容历史数据。</para>
    /// <para>3. 依赖 Program.cs 中注册的 CodePagesEncodingProvider 来启用 GB2312。</para>
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
        /// 初始化日志数据服务。
        /// </summary>
        /// <param name="logger">日志记录器</param>
        public LogDataService(ILogger<LogDataService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            // 日志数据统一存放在程序运行目录的「设置/日志数据」子文件夹
            _logDataFolder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "设置", "日志数据");
            _logger.LogInformation("日志数据服务初始化，数据路径: {Path}", _logDataFolder);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 遍历所有CSV文件，读取第2列（机种名称）的值，去重后返回排序列表。
        /// 结果用于页面的机种名称下拉框。
        /// </remarks>
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
                        // 自动检测文件编码（BOM优先 → 无BOM回退GB2312）
                        var encoding = DetectFileEncoding(file);
                        using var reader = new StreamReader(file, encoding);
                        // 跳过表头行
                        reader.ReadLine();
                        // 读取第一行数据，提取机种名称（第2列）
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

        /// <inheritdoc/>
        /// <remarks>
        /// 加载日志数据文件夹下的所有CSV文件，每个文件调用 ParseCsvFile 解析。
        /// 解析失败的单个文件会被跳过（记录错误日志），不影响其他文件的加载。
        /// </remarks>
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
                        // 单个文件解析失败不中断整体流程
                        _logger.LogError(ex, "解析CSV文件失败: {File}", Path.GetFileName(file));
                    }
                }

                _logger.LogInformation("共加载 {Count} 条日志数据", result.Count);
                return result;
            });
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 多个条件之间为 AND 关系（同时满足）。匹配方式为 Contains 模糊匹配（忽略大小写）。
        /// 空白或 null 的条件会被忽略。
        /// </remarks>
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

        #region 私有方法

        /// <summary>
        /// 检测CSV文件的文本编码 — 通过直接读取文件头部原始字节判断 BOM。
        /// </summary>
        /// <remarks>
        /// <para><b>策略（按优先级）：</b></para>
        /// <para>1. 读取文件前4个字节，直接匹配 BOM（字节顺序标记）：
        ///    EF BB BF → UTF-8 | FF FE → UTF-16 LE | FE FF → UTF-16 BE | FF FE 00 00 → UTF-32 LE</para>
        /// <para>2. 无任何 BOM 匹配 → 回退到 GB2312（中文 Windows 系统本地 ANSI 代码页 20936）。</para>
        /// <para><b>为什么不依赖 Encoding.Default：</b>.NET Core 中 Encoding.Default 始终为 UTF-8，
        /// 不再像 .NET Framework 那样等于系统的 ANSI 代码页。</para>
        /// <para><b>为什么不依赖 StreamReader.CurrentEncoding：</b>该方法在不同 .NET 版本中行为不一致，
        /// 直接读取原始字节判断 BOM 是最可靠的方式。</para>
        /// </remarks>
        /// <param name="filePath">CSV文件路径</param>
        /// <returns>文件的编码对象</returns>
        private static Encoding DetectFileEncoding(string filePath)
        {
            // 直接读取文件头部原始字节，不依赖 StreamReader 的 BOM 检测行为
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
            //     外部工具 / 设备生成的 CSV 通常使用系统默认 ANSI 编码
            //     按优先级尝试：GB2312(20936) → GBK(936) → UTF-8
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

            // 自动检测编码
            var encoding = DetectFileEncoding(filePath);
            var lines = File.ReadAllLines(filePath, encoding);

            // CSV至少需要两行：表头 + 一行数据
            if (lines.Length < 2) return models;

            // 解析表头行，建立「列名 → 列索引」映射
            var headers = ParseCsvLine(lines[0]);
            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
                headerMap[headers[i].Trim()] = i;

            // 提取不属于固定列的列名作为动态列
            var dynamicHeaders = headers
                .Select(h => h.Trim())
                .Where(h => !FixedColumnNames.Contains(h))
                .ToList();

            // 从第2行开始解析数据行
            for (int i = 1; i < lines.Length; i++)
            {
                // 跳过空行
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                var columns = ParseCsvLine(lines[i]);
                if (columns.Length == 0) continue;

                // 填充固定字段
                var model = new LogDataModel
                {
                    Index       = GetIntValue(headerMap, columns, "序号"),
                    MachineType = GetStringValue(headerMap, columns, "机种名称"),
                    SerialNumber = GetStringValue(headerMap, columns, "序列号"),
                    PlanName    = GetStringValue(headerMap, columns, "方案名称"),
                    Inspector   = GetStringValue(headerMap, columns, "检查者"),
                    Judgment    = GetStringValue(headerMap, columns, "综合判定"),
                    Date        = GetStringValue(headerMap, columns, "日期"),
                    Time        = GetStringValue(headerMap, columns, "时间")
                };

                // 填充动态检测项
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
        /// 解析CSV文件中的一行文本，返回列值数组。
        /// 遵循 RFC 4180 标准：逗号分隔、双引号包裹含特殊字符的字段、"" 表示转义引号。
        /// </summary>
        /// <param name="line">CSV行文本</param>
        /// <returns>列值字符串数组</returns>
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
                    // 标准 RFC 4180："" 表示转义的引号字符
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++; // 跳过下一个引号
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    // 逗号作为字段分隔符（仅在引号外生效）
                    result.Add(ProcessCsvField(current.ToString()));
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            // 追加最后一个字段
            result.Add(ProcessCsvField(current.ToString()));
            return result.ToArray();
        }

        /// <summary>
        /// 处理解析后的单个CSV字段值：去除首尾空格，移除包裹的双引号。
        /// </summary>
        /// <param name="field">原始字段字符串</param>
        /// <returns>处理后的干净字符串</returns>
        private static string ProcessCsvField(string field)
        {
            var processed = field.Trim();
            if (processed.StartsWith("\"") && processed.EndsWith("\""))
            {
                processed = processed.Substring(1, processed.Length - 2);
            }
            // 处理转义字符
            processed = processed.Replace("\"\"", "\"");
            return processed;
        }

        /// <summary>
        /// 从解析后的列数组中按列名获取字符串值。
        /// </summary>
        /// <param name="headerMap">列名 → 列索引 映射</param>
        /// <param name="columns">当前行的列值数组</param>
        /// <param name="columnName">要获取的列名</param>
        /// <returns>列值（不存在则返回空字符串）</returns>
        private static string GetStringValue(Dictionary<string, int> headerMap, string[] columns, string columnName)
        {
            if (headerMap.TryGetValue(columnName, out var index) && index < columns.Length)
                return columns[index].Trim();
            return string.Empty;
        }

        /// <summary>
        /// 从解析后的列数组中按列名获取整数值。
        /// </summary>
        /// <param name="headerMap">列名 → 列索引 映射</param>
        /// <param name="columns">当前行的列值数组</param>
        /// <param name="columnName">要获取的列名</param>
        /// <returns>列值（解析失败则返回0）</returns>
        private static int GetIntValue(Dictionary<string, int> headerMap, string[] columns, string columnName)
        {
            var str = GetStringValue(headerMap, columns, columnName);
            return int.TryParse(str, out var val) ? val : 0;
        }

        #endregion
    }
}