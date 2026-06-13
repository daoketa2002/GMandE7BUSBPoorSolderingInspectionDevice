using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 基于JSON文件的方案存储服务
    /// 按系列保存到同一个JSON文件（如 GM5_Scheme.json）
    /// </summary>
    public class PlanStorageService : IPlanStorageService
    {
        private readonly ILogger<PlanStorageService> _logger;
        private readonly string _planFolderPath;
        private readonly JsonSerializerOptions _jsonOptions;
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

        public PlanStorageService(ILogger<PlanStorageService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _planFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "设置", "方案设置");
            Directory.CreateDirectory(_planFolderPath);

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };

            _logger.LogInformation("方案存储服务初始化完成，路径: {Path}", _planFolderPath);
        }

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
        /// 获取系列对应的JSON文件路径
        /// </summary>
        private string GetSeriesFilePath(string series)
        {
            var safeName = series
                .Replace("\\", "_")
                .Replace("/", "_")
                .Replace(":", "_")
                .Replace("*", "_")
                .Replace("?", "_")
                .Replace("\"", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("|", "_");
            return Path.Combine(_planFolderPath, $"{safeName}_Scheme.json");
        }

        /// <summary>
        /// 加载指定系列的所有方案
        /// </summary>
        private SeriesSchemeCollection LoadSeriesSchemes(string series)
        {
            var filePath = GetSeriesFilePath(series);
            if (!File.Exists(filePath))
            {
                return new SeriesSchemeCollection();
            }

            try
            {
                var json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                var collection = JsonSerializer.Deserialize<SeriesSchemeCollection>(json, _jsonOptions);
                return collection ?? new SeriesSchemeCollection();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载系列方案文件失败: {FilePath}", filePath);
                return new SeriesSchemeCollection();
            }
        }

        /// <summary>
        /// 保存指定系列的所有方案
        /// </summary>
        private void SaveSeriesSchemes(string series, SeriesSchemeCollection collection)
        {
            var filePath = GetSeriesFilePath(series);

            // 如果方案列表为空，删除文件
            if (collection.Schemes.Count == 0)
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogInformation("系列方案文件已删除（无方案）: {FilePath}", filePath);
                }
                return;
            }

            var json = JsonSerializer.Serialize(collection, _jsonOptions);
            File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
            _logger.LogInformation("系列方案文件已保存: {FilePath}", filePath);
        }

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
                        _logger.LogInformation("方案文件夹不存在，返回空列表");
                        return allPlans;
                    }

                    try
                    {
                        var files = Directory.GetFiles(_planFolderPath, "*_Scheme.json");
                        foreach (var file in files)
                        {
                            try
                            {
                                var json = File.ReadAllText(file, System.Text.Encoding.UTF8);
                                var collection = JsonSerializer.Deserialize<SeriesSchemeCollection>(json, _jsonOptions);
                                if (collection?.Schemes != null)
                                {
                                    allPlans.AddRange(collection.Schemes);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "解析方案文件失败: {File}", Path.GetFileName(file));
                            }
                        }

                        _logger.LogInformation("成功加载 {Count} 个方案", allPlans.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "加载方案文件失败");
                    }

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

                    // 如果系列发生了变更，需要从旧系列文件中删除
                    if (!string.IsNullOrWhiteSpace(originalSeries) &&
                        !string.Equals(originalSeries, plan.Series, StringComparison.OrdinalIgnoreCase))
                    {
                        var oldCollection = LoadSeriesSchemes(originalSeries);
                        var removed = oldCollection.Schemes.RemoveAll(s =>
                            string.Equals(s.Model, plan.Model, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(s.PlanName, plan.PlanName, StringComparison.OrdinalIgnoreCase));
                        if (removed > 0)
                        {
                            SaveSeriesSchemes(originalSeries, oldCollection);
                            _logger.LogInformation("从旧系列 {Series} 中移除了方案", originalSeries);
                        }
                    }

                    // 保存到当前系列文件
                    var collection = LoadSeriesSchemes(plan.Series);

                    // 查找是否已存在同型号+同方案名
                    var existingIndex = collection.Schemes.FindIndex(s =>
                        string.Equals(s.Model, plan.Model, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.PlanName, plan.PlanName, StringComparison.OrdinalIgnoreCase));

                    if (existingIndex >= 0)
                    {
                        // 更新已有方案
                        collection.Schemes[existingIndex] = plan;
                    }
                    else
                    {
                        // 新增方案
                        collection.Schemes.Add(plan);
                    }

                    SaveSeriesSchemes(plan.Series, collection);
                    _logger.LogInformation("方案已保存: {Series} - {Model} - {Name}", plan.Series, plan.Model, plan.PlanName);
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
                    var collection = LoadSeriesSchemes(series);
                    var removed = collection.Schemes.RemoveAll(s =>
                        string.Equals(s.Model, model, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.PlanName, planName, StringComparison.OrdinalIgnoreCase));

                    if (removed > 0)
                    {
                        SaveSeriesSchemes(series, collection);
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

            // 合并默认系列（去重）
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
            // 优先从文件中加载
            var collection = await Task.Run(() => LoadSeriesSchemes(series));
            var models = collection.Schemes
                                   .Select(s => s.Model)
                                   .Where(m => !string.IsNullOrWhiteSpace(m))
                                   .Distinct()
                                   .ToList();

            // 合并默认型号（去重）
            foreach (var dm in DefaultModels)
            {
                if (!models.Contains(dm))
                    models.Add(dm);
            }

            return models.OrderBy(m => m).ToList();
        }
    }
}