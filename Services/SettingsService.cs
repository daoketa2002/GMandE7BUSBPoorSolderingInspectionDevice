
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WPFStandardFramework.AppConfig;
using WPFStandardFramework.Interfaces;

namespace WPFStandardFramework.Services
{
    /// <summary>
    /// 应用程序设置服务 - 负责设置文件的读写操作
    /// 提供同步和异步两种方式，支持文件损坏自动恢复、重试机制和备份清理
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private readonly string _settingsFilePath;
        private readonly ILogger<SettingsService> _logger;

        /// <summary>
        /// JSON序列化配置选项
        /// 配置为：格式化输出、不区分大小写、驼峰命名、允许注释和末尾逗号
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,               // JSON格式化输出，便于阅读
            PropertyNameCaseInsensitive = true,  // 反序列化时属性名不区分大小写
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,  // 使用驼峰命名
            ReadCommentHandling = JsonCommentHandling.Skip,     // 允许JSON文件中包含注释
            AllowTrailingCommas = true           // 允许JSON对象末尾有多余的逗号
        };

        /// <summary>
        /// 文件操作最大重试次数
        /// </summary>
        private const int MAX_RETRY_COUNT = 3;

        /// <summary>
        /// 重试间隔延迟（毫秒）
        /// </summary>
        private const int RETRY_DELAY_MS = 100;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="configuration">配置对象（虽然当前未使用，但保留以支持未来扩展）</param>
        /// <param name="logger">日志记录器</param>
        public SettingsService(IConfiguration configuration, ILogger<SettingsService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // 获取程序运行基目录
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var settingsFolderPath = Path.Combine(baseDirectory, "Settings");

            try
            {
                // 确保Settings文件夹存在，如果不存在则创建
                Directory.CreateDirectory(settingsFolderPath);
                _logger.LogDebug("Settings文件夹已确认: {FolderPath}", settingsFolderPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建Settings文件夹失败: {FolderPath}", settingsFolderPath);
                throw; // 文件夹创建失败是严重错误，让程序启动失败
            }

            _settingsFilePath = Path.Combine(settingsFolderPath, "appsettings.json");
            _logger.LogDebug("设置文件路径: {FilePath}", _settingsFilePath);
        }

        /// <summary>
        /// 同步加载设置
        /// 从本地JSON文件读取并反序列化为ApplicationSettings对象
        /// </summary>
        /// <returns>应用程序设置对象，如果文件不存在或损坏则返回默认设置</returns>
        public ApplicationSettings LoadSettings()
        {
            _logger.LogDebug("尝试加载设置文件: {FilePath}", _settingsFilePath);

            try
            {
                // 检查文件是否存在
                if (!File.Exists(_settingsFilePath))
                {
                    _logger.LogInformation("设置文件不存在，返回默认设置");
                    return CreateDefaultSettings();
                }

                // 读取文件内容
                var json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);

                // 检查文件是否为空
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogWarning("设置文件为空，返回默认设置");
                    return CreateDefaultSettings();
                }

                // 反序列化JSON
                var settings = JsonSerializer.Deserialize<ApplicationSettings>(json, _jsonOptions);

                // 检查反序列化结果
                if (settings == null)
                {
                    _logger.LogWarning("反序列化结果为null，返回默认设置");
                    return CreateDefaultSettings();
                }

                // 验证设置的有效性
                ValidateSettings(settings);

                _logger.LogInformation("成功加载设置文件: {FilePath}", _settingsFilePath);
                return settings;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON解析错误，设置文件可能已损坏");
                BackupCorruptedFile(); // 备份损坏的文件
                return CreateDefaultSettings();
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "无权限访问设置文件: {FilePath}", _settingsFilePath);
                throw; // 权限问题应该让上层知道
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载设置时发生未预期的错误");
                return CreateDefaultSettings();
            }
        }

        /// <summary>
        /// 异步加载设置
        /// 非阻塞方式从本地JSON文件读取并反序列化
        /// </summary>
        /// <returns>应用程序设置对象，如果文件不存在或损坏则返回默认设置</returns>
        public async Task<ApplicationSettings> LoadSettingsAsync()
        {
            _logger.LogDebug("尝试异步加载设置文件: {FilePath}", _settingsFilePath);

            try
            {
                // 检查文件是否存在
                if (!File.Exists(_settingsFilePath))
                {
                    _logger.LogInformation("设置文件不存在，返回默认设置");
                    return CreateDefaultSettings();
                }

                // 异步读取文件内容
                var json = await File.ReadAllTextAsync(_settingsFilePath, Encoding.UTF8).ConfigureAwait(false);

                // 检查文件是否为空
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogWarning("设置文件为空，返回默认设置");
                    return CreateDefaultSettings();
                }

                // 反序列化JSON
                var settings = JsonSerializer.Deserialize<ApplicationSettings>(json, _jsonOptions);

                // 检查反序列化结果
                if (settings == null)
                {
                    _logger.LogWarning("反序列化结果为null，返回默认设置");
                    return CreateDefaultSettings();
                }

                // 验证设置的有效性
                ValidateSettings(settings);

                _logger.LogInformation("成功异步加载设置文件");
                return settings;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON解析错误，设置文件可能已损坏");
                await BackupCorruptedFileAsync().ConfigureAwait(false); // 异步备份损坏的文件
                return CreateDefaultSettings();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "异步加载设置时发生未预期的错误");
                return CreateDefaultSettings();
            }
        }

        /// <summary>
        /// 同步保存设置
        /// 将设置对象序列化并写入本地JSON文件，支持重试机制和原子操作
        /// </summary>
        /// <param name="settings">要保存的设置对象</param>
        public void SaveSettings(ApplicationSettings settings)
        {
            // 参数验证
            if (settings == null)
                throw new ArgumentNullException(nameof(settings), "设置对象不能为null");

            _logger.LogDebug("尝试保存设置到文件: {FilePath}", _settingsFilePath);

            // 使用重试机制执行保存操作
            ExecuteWithRetry(() =>
            {
                try
                {
                    // 验证设置的有效性
                    ValidateSettings(settings);

                    // 序列化为JSON
                    var json = JsonSerializer.Serialize(settings, _jsonOptions);

                    // 使用临时文件确保写入的原子性
                    var tempFilePath = _settingsFilePath + ".tmp";
                    File.WriteAllText(tempFilePath, json, Encoding.UTF8);

                    // 原子替换操作
                    if (File.Exists(_settingsFilePath))
                    {
                        // 如果原文件存在，使用Replace方法原子替换并自动备份
                        File.Replace(tempFilePath, _settingsFilePath, _settingsFilePath + ".backup");
                    }
                    else
                    {
                        // 如果原文件不存在，直接移动临时文件
                        File.Move(tempFilePath, _settingsFilePath);
                    }

                    _logger.LogInformation("成功保存设置文件");

                    // 清理旧的备份文件，防止磁盘空间被占满
                    CleanupOldBackups();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "保存设置失败");
                    throw; // 重新抛出异常，让上层处理
                }
            }, "SaveSettings"); // 传入操作名称用于日志
        }

        /// <summary>
        /// 异步保存设置
        /// 非阻塞方式将设置对象序列化并写入本地JSON文件
        /// </summary>
        /// <param name="settings">要保存的设置对象</param>
        public async Task SaveSettingsAsync(ApplicationSettings settings)
        {
            // 参数验证
            if (settings == null)
                throw new ArgumentNullException(nameof(settings), "设置对象不能为null");

            _logger.LogDebug("尝试异步保存设置文件");

            // 使用异步重试机制执行保存操作
            await ExecuteWithRetryAsync(async () =>
            {
                try
                {
                    // 验证设置的有效性
                    ValidateSettings(settings);

                    // 序列化为JSON
                    var json = JsonSerializer.Serialize(settings, _jsonOptions);

                    // 创建临时文件
                    var tempFilePath = _settingsFilePath + ".tmp";
                    await File.WriteAllTextAsync(tempFilePath, json, Encoding.UTF8).ConfigureAwait(false);

                    // 原子替换操作
                    if (File.Exists(_settingsFilePath))
                    {
                        // 如果原文件存在，使用Replace方法原子替换并自动备份
                        File.Replace(tempFilePath, _settingsFilePath, _settingsFilePath + ".backup");
                    }
                    else
                    {
                        // 如果原文件不存在，直接移动临时文件
                        File.Move(tempFilePath, _settingsFilePath);
                    }

                    _logger.LogInformation("成功异步保存设置文件");

                    // 异步清理旧的备份文件
                    await Task.Run(() => CleanupOldBackups()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "异步保存设置失败");
                    throw; // 重新抛出异常，让上层处理
                }
            }, "SaveSettingsAsync"); // 传入操作名称用于日志
        }

        /// <summary>
        /// 创建默认设置
        /// 当设置文件不存在或损坏时，返回一个包含合理默认值的设置对象
        /// </summary>
        /// <returns>默认设置对象</returns>
        private static ApplicationSettings CreateDefaultSettings()
        {
            return new ApplicationSettings();
           
        }

        /// <summary>
        /// 验证设置的有效性
        /// 检查设置对象是否符合业务规则，包括版本兼容性检查
        /// </summary>
        /// <param name="settings">要验证的设置对象</param>
        private void ValidateSettings(ApplicationSettings settings)
        {
            // 参数验证
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            // 版本兼容性检查
            if (!string.IsNullOrEmpty(settings.Version))
            {
                var currentVersion = new Version("1.0.0");
                var fileVersion = new Version(settings.Version);

                // 如果文件版本比当前版本新，记录警告
                if (fileVersion > currentVersion)
                {
                    _logger.LogWarning("设置文件版本 {FileVersion} 高于当前版本 {CurrentVersion}，可能存在兼容性问题",
                        fileVersion, currentVersion);
                }
            }

            _logger.LogDebug("设置验证通过");
        }

        /// <summary>
        /// 备份损坏的文件
        /// 当设置文件损坏时，将其重命名为带时间戳的备份文件
        /// </summary>
        private void BackupCorruptedFile()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    var corruptedPath = $"{_settingsFilePath}.corrupted.{timestamp}";
                    File.Move(_settingsFilePath, corruptedPath);
                    _logger.LogInformation("已备份损坏的设置文件到: {CorruptedPath}", corruptedPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "备份损坏的设置文件失败");
            }
        }

        /// <summary>
        /// 异步备份损坏的文件
        /// 当设置文件损坏时，异步将其重命名为带时间戳的备份文件
        /// </summary>
        private async Task BackupCorruptedFileAsync()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    var corruptedPath = $"{_settingsFilePath}.corrupted.{timestamp}";
                    await Task.Run(() => File.Move(_settingsFilePath, corruptedPath)).ConfigureAwait(false);
                    _logger.LogInformation("已异步备份损坏的设置文件到: {CorruptedPath}", corruptedPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "异步备份损坏的设置文件失败");
            }
        }

        /// <summary>
        /// 带重试机制的同步方法执行器
        /// 在IO操作失败时自动重试指定次数
        /// </summary>
        /// <param name="action">要执行的操作</param>
        /// <param name="operationName">操作名称（用于日志）</param>
        private void ExecuteWithRetry(Action action, string operationName)
        {
            int retryCount = 0;
            while (retryCount < MAX_RETRY_COUNT)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException) when (retryCount < MAX_RETRY_COUNT - 1)
                {
                    // 只有IO异常才重试，且不超过最大重试次数
                    retryCount++;
                    _logger.LogWarning("操作 {OperationName} 第 {RetryCount}/{MaxRetries} 次重试",
                        operationName, retryCount, MAX_RETRY_COUNT);
                    Thread.Sleep(RETRY_DELAY_MS); // 重试前等待
                }
            }
        }

        /// <summary>
        /// 带重试机制的异步方法执行器
        /// 在IO操作失败时自动重试指定次数
        /// </summary>
        /// <param name="action">要执行的异步操作</param>
        /// <param name="operationName">操作名称（用于日志）</param>
        private async Task ExecuteWithRetryAsync(Func<Task> action, string operationName)
        {
            int retryCount = 0;
            while (retryCount < MAX_RETRY_COUNT)
            {
                try
                {
                    await action().ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (retryCount < MAX_RETRY_COUNT - 1)
                {
                    // 只有IO异常才重试，且不超过最大重试次数
                    retryCount++;
                    _logger.LogWarning("异步操作 {OperationName} 第 {RetryCount}/{MaxRetries} 次重试",
                        operationName, retryCount, MAX_RETRY_COUNT);
                    await Task.Delay(RETRY_DELAY_MS).ConfigureAwait(false); // 异步等待
                }
            }
        }


        /// <summary>
        /// 清理旧备份文件
        /// 删除超过7天的备份文件和损坏文件，防止磁盘空间被占满
        /// </summary>
        private void CleanupOldBackups()
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsFilePath);
                if (directory == null) return;

                // 获取所有备份文件和损坏文件
                var backupFiles = Directory.GetFiles(directory, "*.backup");
                var corruptedFiles = Directory.GetFiles(directory, "*.corrupted.*");
                var allOldFiles = backupFiles.Concat(corruptedFiles);

                foreach (var file in allOldFiles)
                {
                    var fileInfo = new FileInfo(file);
                    // 保留最近7天的备份
                    if (fileInfo.CreationTime < DateTime.Now.AddDays(-7))
                    {
                        File.Delete(file);
                        _logger.LogDebug("已清理旧备份文件: {FilePath}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理旧备份文件失败");
            }
        }
    }

}
