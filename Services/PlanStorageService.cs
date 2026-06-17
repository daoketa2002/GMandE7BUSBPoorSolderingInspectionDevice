using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 基于JSON文件的方案存储服务
    /// 按系列分文件夹，单文件最多MaxSchemesPerFile个方案，超出自动分片
    /// 目录结构：设置\方案设置\{系列名}\scheme_001.json, scheme_002.json...
    /// </summary>
    public class PlanStorageService : IPlanStorageService
    {
        private readonly ILogger<PlanStorageService> _logger;
        private readonly string _planFolderPath;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly int _maxSchemesPerFile;
        private static readonly object _lock = new();

        /// <summary>
        /// 默认系列列表
        /// </summary>
        public static readonly List<string> DefaultSeries = new List<string> { "GM5", "E78" };

        /// <summary>
        /// 默认型号列表
        /// </summary>
        public static readonly List<string> DefaultModels = new List<string> { "T998248391", "998245664NNHB" };

        /// <summary>
        /// 固定引脚列表（A1~A20, B1~B20）
        /// </summary>
        public static readonly List<string> PinList = GeneratePinList();

        public PlanStorageService(ILogger<PlanStorageService> logger, IConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _planFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "设置", "方案设置");
            Directory.CreateDirectory(_planFolderPath);

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };

            _maxSchemesPerFile = configuration?.GetValue<int>("PlanStorage:MaxSchemesPerFile", 50) ?? 50;
            if (_maxSchemesPerFile < 1) _maxSchemesPerFile = 50;

            _logger.LogInformation("方案存储服务初始化完成，路径: {Path}，单文件上限: {Max}", _planFolderPath, _maxSchemesPerFile);
        }

        #region 辅助方法

        /// <summary>
        /// 生成固定引脚列表 A1~A20, B1~B20
        /// </summary>
        private static List<string> GeneratePinList()
        {
            var pins = new List<string>();
            for (int i = 1; i <= 20; i++)
            {
                pins.Add($"A{i}");
                pins.Add($"B{i}");
            }
            return pins;
        }

        /// <summary>
        /// 安全的文件名
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new StringBuilder(name);
            foreach (var c in invalid)
                safe.Replace(c, '_');
            return safe.ToString();
        }

        /// <summary>
        /// 获取系列文件夹路径：设置\方案设置\GM5\
        /// </summary>
        private string GetSeriesFolderPath(string series)
        {
            var safeName = SanitizeFileName(series);
            var folder = Path.Combine(_planFolderPath, safeName);
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// 获取分片文件路径：设置\方案设置\GM5\scheme_001.json
        /// </summary>
        private string GetSchemeFilePath(string series, int fileIndex)
        {
            return Path.Combine(GetSeriesFolderPath(series), $"scheme_{fileIndex:D3}.json");
        }

        /// <summary>
        /// 加载指定系列的所有方案（合并所有分片文件）
        /// </summary>
        private List<PlanModel> LoadSchemesFromSeriesFolder(string series)
        {
            var folder = GetSeriesFolderPath(series);
            var allSchemes = new List<PlanModel>();

            if (!Directory.Exists(folder))
                return allSchemes;

            try
            {
                var files = Directory.GetFiles(folder, "scheme_*.json")
                                     .OrderBy(f => f)
                                     .ToList();

                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file, Encoding.UTF8);
                        var schemes = JsonSerializer.Deserialize<List<PlanModel>>(json, _jsonOptions);
                        if (schemes != null)
                            allSchemes.AddRange(schemes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "解析方案分片文件失败: {File}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "遍历系列文件夹失败: {Folder}", folder);
            }

            return allSchemes;
        }

        /// <summary>
        /// 将方案列表分片写入系列文件夹
        /// </summary>
        private void WriteSchemesToSeriesFolder(string series, List<PlanModel> schemes)
        {
            var folder = GetSeriesFolderPath(series);

            // 计算分片数
            int totalFiles = (schemes.Count + _maxSchemesPerFile - 1) / _maxSchemesPerFile;
            if (totalFiles == 0) totalFiles = 0;

            // 写入各分片
            for (int i = 0; i < totalFiles; i++)
            {
                var chunk = schemes.Skip(i * _maxSchemesPerFile).Take(_maxSchemesPerFile).ToList();
                var filePath = GetSchemeFilePath(series, i + 1);
                var json = JsonSerializer.Serialize(chunk, _jsonOptions);
                File.WriteAllText(filePath, json, Encoding.UTF8);
                _logger.LogDebug("分片写入: {File}, 方案数: {Count}", filePath, chunk.Count);
            }

            // 删除多余的空文件（方案减少后残留的scheme_xxx.json）
            if (Directory.Exists(folder))
            {
                var existingFiles = Directory.GetFiles(folder, "scheme_*.json");
                foreach (var file in existingFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var numStr = fileName.Replace("scheme_", "");
                    if (int.TryParse(numStr, out int index) && index > totalFiles)
                    {
                        File.Delete(file);
                        _logger.LogInformation("已删除多余分片文件: {File}", file);
                    }
                }

                // 如果文件夹为空，删除文件夹
                if (Directory.GetFiles(folder).Length == 0)
                {
                    Directory.Delete(folder);
                    _logger.LogInformation("系列文件夹已删除（无方案）: {Folder}", folder);
                }
            }
        }

        #endregion

        #region IPlanStorageService 实现

        /// <inheritdoc/>
        public async Task<List<PlanModel>> LoadAllPlansAsync()
        {
            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    var allPlans = new List<PlanModel>();

                    if (!Directory.Exists(_planFolderPath))
                    {
                        Directory.CreateDirectory(_planFolderPath);
                    }

                    // 遍历系列子文件夹
                    var seriesDirs = Directory.GetDirectories(_planFolderPath);

                    // 首次运行：无任何系列文件夹，自动导入默认方案
                    if (seriesDirs.Length == 0)
                    {
                        _logger.LogInformation("未检测到任何方案文件，方案列表为空");
                        return allPlans; // 空列表
                    }

                    // 遍历所有系列文件夹，加载方案
                    foreach (var dir in seriesDirs)
                    {
                        var seriesName = Path.GetFileName(dir);
                        var schemes = LoadSchemesFromSeriesFolder(seriesName);
                        allPlans.AddRange(schemes);
                    }

                    _logger.LogInformation("成功加载 {Count} 个方案（来自 {SeriesCount} 个系列）",
                        allPlans.Count, seriesDirs.Length);
                    return allPlans;
                }
            });
        }

        /// <inheritdoc/>
        public async Task SavePlanAsync(PlanModel plan, string? originalSeries = null)
        {
            if (string.IsNullOrWhiteSpace(plan.Series))
                throw new ArgumentException("系列不能为空");
            if (string.IsNullOrWhiteSpace(plan.Model))
                throw new ArgumentException("型号不能为空");
            if (string.IsNullOrWhiteSpace(plan.PlanName))
                throw new ArgumentException("方案名称不能为空");

            await Task.Run(() =>
            {
                lock (_lock)
                {
                    // 重新整理序号
                    for (int i = 0; i < plan.Items.Count; i++)
                    {
                        plan.Items[i].Index = i + 1;
                    }

                    // 系列变更处理：从旧系列中删除
                    if (!string.IsNullOrWhiteSpace(originalSeries) &&
                        !string.Equals(originalSeries, plan.Series, StringComparison.OrdinalIgnoreCase))
                    {
                        var oldSchemes = LoadSchemesFromSeriesFolder(originalSeries);
                        var removed = oldSchemes.RemoveAll(s =>
                            string.Equals(s.Model, plan.Model, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(s.PlanName, plan.PlanName, StringComparison.OrdinalIgnoreCase));

                        if (removed > 0)
                        {
                            WriteSchemesToSeriesFolder(originalSeries, oldSchemes);
                            _logger.LogInformation("系列变更：已从旧系列 {OldSeries} 中移除方案 {Plan}",
                                originalSeries, plan.PlanName);
                        }
                    }

                    // 保存到当前系列
                    var currentSchemes = LoadSchemesFromSeriesFolder(plan.Series);

                    var existingIndex = currentSchemes.FindIndex(s =>
                        string.Equals(s.Model, plan.Model, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.PlanName, plan.PlanName, StringComparison.OrdinalIgnoreCase));

                    if (existingIndex >= 0)
                    {
                        currentSchemes[existingIndex] = plan;
                    }
                    else
                    {
                        currentSchemes.Add(plan);
                    }

                    WriteSchemesToSeriesFolder(plan.Series, currentSchemes);
                    _logger.LogInformation("方案已保存: {Series} - {Model} - {Name}, 该系列共 {Count} 个方案",
                        plan.Series, plan.Model, plan.PlanName, currentSchemes.Count);
                }
            });
        }

        /// <inheritdoc/>
        public async Task DeletePlanAsync(string series, string model, string planName)
        {
            await Task.Run(() =>
            {
                lock (_lock)
                {
                    var schemes = LoadSchemesFromSeriesFolder(series);
                    var removed = schemes.RemoveAll(s =>
                        string.Equals(s.Model, model, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.PlanName, planName, StringComparison.OrdinalIgnoreCase));

                    if (removed > 0)
                    {
                        WriteSchemesToSeriesFolder(series, schemes);
                        _logger.LogInformation("方案已删除: {Series} - {Model} - {Name}", series, model, planName);
                    }
                    else
                    {
                        _logger.LogWarning("未找到要删除的方案: {Series} - {Model} - {Name}", series, model, planName);
                    }
                }
            });
        }

        /// <inheritdoc/>
        public async Task<List<string>> GetAllSeriesAsync()
        {
            var plans = await LoadAllPlansAsync();
            var series = plans.Select(p => p.Series)
                              .Where(s => !string.IsNullOrWhiteSpace(s))
                              .Distinct()
                              .ToList();

            foreach (var ds in DefaultSeries)
            {
                if (!series.Contains(ds))
                    series.Add(ds);
            }

            return series.OrderBy(s => s).ToList();
        }

        /// <inheritdoc/>
        public async Task<List<string>> GetModelsBySeriesAsync(string series)
        {
            var schemes = await Task.Run(() => LoadSchemesFromSeriesFolder(series));
            var models = schemes.Select(s => s.Model)
                                .Where(m => !string.IsNullOrWhiteSpace(m))
                                .Distinct()
                                .ToList();

            foreach (var dm in DefaultModels)
            {
                if (!models.Contains(dm))
                    models.Add(dm);
            }

            return models.OrderBy(m => m).ToList();
        }

        #endregion
    }
}