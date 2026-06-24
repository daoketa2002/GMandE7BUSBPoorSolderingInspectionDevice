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
    /// 新结构：机种→方案 两级
    /// 目录结构：{方案根目录}/{机种文件夹}/{方案名}.json
    /// 一个方案一个独立文件，实现数据隔离
    /// </summary>
    public class PlanStorageService : IPlanStorageService
    {
        private readonly ILogger<PlanStorageService> _logger;
        private readonly string _planRootFolder;
        private readonly JsonSerializerOptions _jsonOptions;
        private static readonly object _fileLock = new object();

        /// <summary>
        /// 非法文件名字符（Windows文件系统不允许的字符）
        /// 用于过滤机种名和方案名中的非法字符
        /// </summary>
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// 默认机种列表（首次运行无数据时展示）
        /// </summary>
        public static readonly List<string> DefaultMachineTypes = new List<string> { "T998248391", "998245664NNHB" };

        /// <summary>
        /// 固定引脚列表（A1~A20, B1~B20）
        /// 用于方案编辑时的引脚下拉选项
        /// </summary>
        public static readonly List<string> PinList = GeneratePinList();

        /// <summary>
        /// 构造函数：从DI容器注入日志和配置
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="configuration">应用配置（读取方案存储根路径）</param>
        public PlanStorageService(ILogger<PlanStorageService> logger, IConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // 读取用户配置的方案保存根目录，默认值为"设置/方案设置"
            var configuredPath = configuration?.GetValue<string>("PlanStorage:RootPath", "设置/方案设置");
            _planRootFolder = Path.IsPathRooted(configuredPath)
                ? configuredPath!
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath!);

            // 确保根目录存在
            Directory.CreateDirectory(_planRootFolder);

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,           // 格式化JSON，便于人工阅读
                PropertyNameCaseInsensitive = true, // 反序列化时忽略属性名大小写
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // 中文不转义
            };

            _logger.LogInformation("方案存储服务初始化完成");
            _logger.LogInformation("  方案根目录: {RootPath}", _planRootFolder);
            _logger.LogInformation("  JSON编码: 中文不转义，可读性强");
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
        /// 安全化文件名：替换非法字符为下划线，去除首尾空格
        /// 确保机种名和方案名可以作为合法的文件夹名/文件名
        /// </summary>
        /// <param name="name">原始名称</param>
        /// <returns>安全化后的名称</returns>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "_";

            // 去除首尾空格
            var sanitized = name.Trim();

            // 替换非法字符为下划线
            var builder = new StringBuilder(sanitized);
            foreach (var invalidChar in InvalidFileNameChars)
            {
                builder.Replace(invalidChar, '_');
            }

            // 如果替换后为空，返回默认值
            var result = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(result) ? "_" : result;
        }

        /// <summary>
        /// 获取指定机种的文件夹路径
        /// 格式：{方案根目录}/{安全化后的机种名}/
        /// </summary>
        /// <param name="machineType">机种名称</param>
        /// <returns>机种文件夹的完整路径</returns>
        private string GetMachineTypeFolderPath(string machineType)
        {
            var safeMachineType = SanitizeFileName(machineType);
            var folderPath = Path.Combine(_planRootFolder, safeMachineType);
            return folderPath;
        }

        /// <summary>
        /// 获取指定方案的JSON文件路径
        /// 格式：{机种文件夹}/{安全化后的方案名}.json
        /// </summary>
        /// <param name="machineType">机种名称</param>
        /// <param name="planName">方案名称</param>
        /// <returns>方案JSON文件的完整路径</returns>
        private string GetPlanFilePath(string machineType, string planName)
        {
            var folderPath = GetMachineTypeFolderPath(machineType);
            var safePlanName = SanitizeFileName(planName);
            var filePath = Path.Combine(folderPath, $"{safePlanName}.json");
            return filePath;
        }

        /// <summary>
        /// 从JSON文件加载单个方案
        /// ★ 改动：加载后兼容转换旧数据中的英文 CheckMode → 中文
        /// </summary>
        /// <param name="filePath">JSON文件路径</param>
        /// <returns>方案对象，如果文件不存在或解析失败返回null</returns>
        private PlanModel? LoadPlanFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var json = File.ReadAllText(filePath, Encoding.UTF8);
                var plan = JsonSerializer.Deserialize<PlanModel>(json, _jsonOptions);

                if (plan != null)
                {
                    // 从文件路径反推机种名（兼容文件移动后的数据一致性）
                    var folderName = Path.GetFileName(Path.GetDirectoryName(filePath));
                    plan.MachineType = folderName ?? plan.MachineType;

                    // 从文件名反推方案名（去除.json扩展名）
                    var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
                    plan.PlanName = fileNameWithoutExtension ?? plan.PlanName;

                    // ★ 兼容旧数据：将英文 CheckMode 统一转为中文
                    NormalizeCheckModeInPlan(plan);
                }

                return plan;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载方案文件失败: {FilePath}", filePath);
                return null;
            }
        }

        /// <summary>
        /// 规范化方案中所有检测项目的 CheckMode 值
        /// 兼容旧数据：英文 "Continuity"/"Resistance" → 中文 "导通"/"电阻值"
        /// </summary>
        /// <param name="plan">方案对象</param>
        private static void NormalizeCheckModeInPlan(PlanModel plan)
        {
            if (plan.Items == null || plan.Items.Count == 0)
                return;

            foreach (var item in plan.Items)
            {
                item.CheckMode = NormalizeCheckMode(item.CheckMode);
            }
        }

        /// <summary>
        /// 单个 CheckMode 值规范化
        /// 英文值自动转中文，中文值原样返回
        /// </summary>
        /// <param name="rawValue">原始值</param>
        /// <returns>标准中文值</returns>
        private static string NormalizeCheckMode(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return CheckModeConstants.Continuity;

            return rawValue.Trim() switch
            {
                // 旧版英文 → 中文
                "Continuity" => CheckModeConstants.Continuity,
                "Resistance" => CheckModeConstants.Resistance,
                // 已经是中文 → 原样返回
                "导通" => CheckModeConstants.Continuity,
                "电阻值" => CheckModeConstants.Resistance,
                // 未知值 → 保留原样（避免数据丢失）
                _ => rawValue.Trim()
            };
        }

        /// <summary>
        /// 将方案对象保存为JSON文件
        /// </summary>
        /// <param name="plan">方案对象</param>
        /// <param name="filePath">目标文件路径</param>
        private void SavePlanToFile(PlanModel plan, string filePath)
        {
            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // 更新时间戳
            plan.LastModifiedTime = DateTime.Now;

            var json = JsonSerializer.Serialize(plan, _jsonOptions);
            File.WriteAllText(filePath, json, Encoding.UTF8);

            _logger.LogDebug("方案已写入文件: {FilePath}", filePath);
        }

        /// <summary>
        /// 加载指定机种文件夹下的所有方案
        /// </summary>
        /// <param name="machineType">机种名称</param>
        /// <returns>该机种下的所有方案列表</returns>
        private List<PlanModel> LoadPlansFromMachineTypeFolder(string machineType)
        {
            var folderPath = GetMachineTypeFolderPath(machineType);
            var plans = new List<PlanModel>();

            if (!Directory.Exists(folderPath))
                return plans;

            try
            {
                // 遍历所有.json文件
                var jsonFiles = Directory.GetFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly);

                foreach (var file in jsonFiles)
                {
                    var plan = LoadPlanFromFile(file);
                    if (plan != null)
                    {
                        plans.Add(plan);
                    }
                }

                _logger.LogDebug("从机种文件夹 {MachineType} 加载了 {Count} 个方案", machineType, plans.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "遍历机种文件夹失败: {Folder}", folderPath);
            }

            return plans;
        }

        #endregion

        #region IPlanStorageService 实现

        /// <inheritdoc/>
        public async Task<List<PlanModel>> LoadAllPlansAsync()
        {
            return await Task.Run(() =>
            {
                lock (_fileLock)
                {
                    var allPlans = new List<PlanModel>();

                    if (!Directory.Exists(_planRootFolder))
                    {
                        Directory.CreateDirectory(_planRootFolder);
                        _logger.LogInformation("方案根目录已创建（无数据）: {RootPath}", _planRootFolder);
                        return allPlans;
                    }

                    // 遍历所有机种子文件夹
                    var machineTypeDirs = Directory.GetDirectories(_planRootFolder);

                    foreach (var dir in machineTypeDirs)
                    {
                        var machineType = Path.GetFileName(dir);
                        var plans = LoadPlansFromMachineTypeFolder(machineType);
                        allPlans.AddRange(plans);
                    }

                    _logger.LogInformation("成功加载全部方案: 共 {PlanCount} 个方案，{MachineTypeCount} 个机种",
                        allPlans.Count, machineTypeDirs.Length);

                    return allPlans;
                }
            });
        }

        /// <inheritdoc/>
        public async Task SavePlanAsync(PlanModel plan, string? originalMachineType = null, string? originalPlanName = null)
        {
            // 参数校验
            if (string.IsNullOrWhiteSpace(plan.MachineType))
                throw new ArgumentException("机种名称不能为空", nameof(plan.MachineType));
            if (string.IsNullOrWhiteSpace(plan.PlanName))
                throw new ArgumentException("方案名称不能为空", nameof(plan.PlanName));

            await Task.Run(() =>
            {
                lock (_fileLock)
                {
                    // ================================================================
                    // 重新整理检测项目序号（从1开始连续）
                    // ================================================================
                    for (int i = 0; i < plan.Items.Count; i++)
                    {
                        plan.Items[i].Index = i + 1;

                        // 确保每个项目有唯一标识（首次创建时生成）
                        if (string.IsNullOrWhiteSpace(plan.Items[i].Id))
                        {
                            plan.Items[i].Id = Guid.NewGuid().ToString();
                        }
                    }

                    // 设置创建时间（新增方案时）
                    if (plan.CreatedTime == default)
                    {
                        plan.CreatedTime = DateTime.Now;
                    }

                    // ================================================================
                    // ★ 核心修复：判断是否需要删除旧文件
                    // 判定条件：机种名称变更 OR 方案名称变更（任意一个变更都视为路径变化）
                    // 覆盖场景：
                    //   1. 新增方案 → originalMachineType/originalPlanName 都为 null，跳过删除
                    //   2. 仅改内容   → 两者都未变，跳过删除，直接覆盖保存
                    //   3. 仅改方案名 → planNameChanged=true，删除旧文件，保存新文件（重命名）
                    //   4. 仅改机种   → machineTypeChanged=true，删除旧文件，保存新文件（移动）
                    //   5. 同时改两者 → 两者都变更，删除旧文件，保存新文件
                    // ================================================================
                    bool machineTypeChanged = !string.IsNullOrWhiteSpace(originalMachineType)
                        && !string.Equals(originalMachineType, plan.MachineType, StringComparison.OrdinalIgnoreCase);

                    bool planNameChanged = !string.IsNullOrWhiteSpace(originalPlanName)
                        && !string.Equals(originalPlanName, plan.PlanName, StringComparison.OrdinalIgnoreCase);

                    if (machineTypeChanged || planNameChanged)
                    {
                        // 使用旧机种名+旧方案名定位原文件
                        var oldMachineType = originalMachineType!;
                        var oldPlanName = originalPlanName!;
                        var oldFilePath = GetPlanFilePath(oldMachineType, oldPlanName);

                        if (File.Exists(oldFilePath))
                        {
                            File.Delete(oldFilePath);
                            _logger.LogInformation("路径变更：已删除旧方案文件 {OldFile}", oldFilePath);
                        }

                        // 清理旧机种文件夹（如果为空）
                        CleanupEmptyMachineTypeFolder(oldMachineType);
                    }

                    // ================================================================
                    // 保存到新路径（覆盖或新建）
                    // ================================================================
                    var newFilePath = GetPlanFilePath(plan.MachineType, plan.PlanName);
                    SavePlanToFile(plan, newFilePath);

                    _logger.LogInformation("方案保存成功: {MachineType}/{PlanName}.json",
                        SanitizeFileName(plan.MachineType), SanitizeFileName(plan.PlanName));
                }
            });
        }

        /// <summary>
        /// 清理空的机种文件夹
        /// 当文件夹下没有任何文件时，自动删除该空文件夹
        /// </summary>
        /// <param name="machineType">机种名称</param>
        private void CleanupEmptyMachineTypeFolder(string machineType)
        {
            var folderPath = GetMachineTypeFolderPath(machineType);

            if (!Directory.Exists(folderPath))
                return;

            // 检查文件夹是否为空（无文件且无子目录）
            var hasFiles = Directory.GetFiles(folderPath).Length > 0;
            var hasDirectories = Directory.GetDirectories(folderPath).Length > 0;

            if (!hasFiles && !hasDirectories)
            {
                Directory.Delete(folderPath);
                _logger.LogInformation("已删除空机种文件夹: {FolderPath}", folderPath);
            }
        }

        /// <inheritdoc/>
        public async Task DeletePlanAsync(string machineType, string planName)
        {
            // 参数校验
            if (string.IsNullOrWhiteSpace(machineType))
                throw new ArgumentException("机种名称不能为空", nameof(machineType));
            if (string.IsNullOrWhiteSpace(planName))
                throw new ArgumentException("方案名称不能为空", nameof(planName));

            await Task.Run(() =>
            {
                lock (_fileLock)
                {
                    var filePath = GetPlanFilePath(machineType, planName);

                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        _logger.LogInformation("方案已删除: {FilePath}", filePath);

                        // 如果机种文件夹为空，删除空文件夹
                        var folderPath = GetMachineTypeFolderPath(machineType);
                        if (Directory.Exists(folderPath) &&
                            Directory.GetFiles(folderPath).Length == 0 &&
                            Directory.GetDirectories(folderPath).Length == 0)
                        {
                            Directory.Delete(folderPath);
                            _logger.LogInformation("已删除空机种文件夹: {FolderPath}", folderPath);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("要删除的方案文件不存在: {FilePath}", filePath);
                    }
                }
            });
        }

        /// <inheritdoc/>
        public async Task<List<string>> GetAllMachineTypesAsync()
        {
            return await Task.Run(() =>
            {
                lock (_fileLock)
                {
                    if (!Directory.Exists(_planRootFolder))
                        return new List<string>(DefaultMachineTypes);

                    // 获取所有子文件夹名作为机种名称
                    var machineTypes = Directory.GetDirectories(_planRootFolder)
                        .Select(dir => Path.GetFileName(dir))
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .ToList()!;

                    // 合并默认机种（去重）
                    foreach (var defaultType in DefaultMachineTypes)
                    {
                        if (!machineTypes.Any(mt => mt.Equals(defaultType, StringComparison.OrdinalIgnoreCase)))
                        {
                            machineTypes.Add(defaultType);
                        }
                    }

                    var result = machineTypes.OrderBy(mt => mt).ToList();
                    _logger.LogDebug("获取所有机种: 共 {Count} 个", result.Count);
                    return result;
                }
            });
        }

        /// <inheritdoc/>
        public async Task<List<string>> GetPlanNamesByMachineTypeAsync(string machineType)
        {
            return await Task.Run(() =>
            {
                lock (_fileLock)
                {
                    if (string.IsNullOrWhiteSpace(machineType))
                        return new List<string>();

                    var folderPath = GetMachineTypeFolderPath(machineType);

                    if (!Directory.Exists(folderPath))
                        return new List<string>();

                    // 获取所有.json文件名（不含扩展名）作为方案名称
                    var planNames = Directory.GetFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly)
                        .Select(file => Path.GetFileNameWithoutExtension(file))
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .OrderBy(name => name)
                        .ToList()!;

                    _logger.LogDebug("获取机种 {MachineType} 的方案列表: 共 {Count} 个", machineType, planNames.Count);
                    return planNames;
                }
            });
        }

        #endregion
    }
}