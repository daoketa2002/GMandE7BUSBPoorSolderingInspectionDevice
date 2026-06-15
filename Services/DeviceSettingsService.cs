using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceConfigs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 设备设置服务 - 负责设备配置文件的读写操作
    /// 支持文件损坏自动恢复、重试机制和备份清理
    /// </summary>
    public class DeviceSettingsService : IDeviceSettingsService
    {
        private readonly string _settingsFilePath;
        private readonly ILogger<DeviceSettingsService> _logger;

        /// <summary>
        /// JSON序列化配置选项
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// 文件操作最大重试次数
        /// </summary>
        private const int MAX_RETRY_COUNT = 3;

        /// <summary>
        /// 重试间隔延迟（毫秒）
        /// </summary>
        private const int RETRY_DELAY_MS = 100;

        public DeviceSettingsService(IConfiguration configuration, ILogger<DeviceSettingsService>? logger = null)
        {
            _logger = logger;

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var settingsFolderPath = Path.Combine(baseDirectory, "设置", "设备设置");

            try
            {
                Directory.CreateDirectory(settingsFolderPath);
                _logger?.LogDebug("设备设置文件夹已确认: {FolderPath}", settingsFolderPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "创建设备设置文件夹失败: {FolderPath}", settingsFolderPath);
                throw;
            }

            _settingsFilePath = Path.Combine(settingsFolderPath, "DeviceSettings.json");
            _logger?.LogDebug("设备设置文件路径: {FilePath}", _settingsFilePath);
        }

        /// <summary>
        /// 同步加载设备设置
        /// </summary>
        public DeviceSettings LoadSettings()
        {
            _logger?.LogDebug("尝试加载设备设置文件: {FilePath}", _settingsFilePath);

            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    _logger?.LogInformation("设备设置文件不存在，返回默认设置");
                    return CreateDefaultSettings();
                }

                var json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);

                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger?.LogWarning("设备设置文件为空，返回默认设置");
                    return CreateDefaultSettings();
                }

                var settings = JsonSerializer.Deserialize<DeviceSettings>(json, _jsonOptions);

                if (settings == null)
                {
                    _logger?.LogWarning("反序列化结果为null，返回默认设置");
                    return CreateDefaultSettings();
                }

                _logger?.LogInformation("成功加载设备设置文件");
                return settings;
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "JSON解析错误，设备设置文件可能已损坏");
                BackupCorruptedFile();
                return CreateDefaultSettings();
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger?.LogError(ex, "无权限访问设备设置文件: {FilePath}", _settingsFilePath);
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "加载设备设置时发生未预期的错误");
                return CreateDefaultSettings();
            }
        }

        /// <summary>
        /// 同步保存设备设置
        /// 支持重试机制和原子操作
        /// </summary>
        public void SaveSettings(DeviceSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings), "设备设置对象不能为null");

            _logger?.LogDebug("尝试保存设备设置到文件: {FilePath}", _settingsFilePath);

            ExecuteWithRetry(() =>
            {
                try
                {
                    var json = JsonSerializer.Serialize(settings, _jsonOptions);

                    // 使用临时文件确保写入的原子性
                    var tempFilePath = _settingsFilePath + ".tmp";
                    File.WriteAllText(tempFilePath, json, Encoding.UTF8);

                    // 原子替换操作
                    if (File.Exists(_settingsFilePath))
                    {
                        File.Replace(tempFilePath, _settingsFilePath, _settingsFilePath + ".backup");
                    }
                    else
                    {
                        File.Move(tempFilePath, _settingsFilePath);
                    }

                    _logger?.LogInformation("成功保存设备设置文件");

                    // 清理旧的备份文件
                    CleanupOldBackups();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "保存设备设置失败");
                    throw;
                }
            }, "SaveDeviceSettings");
        }

        /// <summary>
        /// 创建默认设备设置
        /// </summary>
        private static DeviceSettings CreateDefaultSettings()
        {
            return new DeviceSettings();
        }

        /// <summary>
        /// 备份损坏的文件
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
                    _logger?.LogInformation("已备份损坏的设备设置文件到: {CorruptedPath}", corruptedPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "备份损坏的设备设置文件失败");
            }
        }

        /// <summary>
        /// 带重试机制的同步方法执行器
        /// </summary>
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
                    retryCount++;
                    _logger?.LogWarning("操作 {OperationName} 第 {RetryCount}/{MaxRetries} 次重试",
                        operationName, retryCount, MAX_RETRY_COUNT);
                    System.Threading.Thread.Sleep(RETRY_DELAY_MS);
                }
            }
        }

        /// <summary>
        /// 清理旧备份文件（超过7天的备份）
        /// </summary>
        private void CleanupOldBackups()
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsFilePath);
                if (directory == null) return;

                var backupFiles = Directory.GetFiles(directory, "*.backup");
                var corruptedFiles = Directory.GetFiles(directory, "*.corrupted.*");
                var allOldFiles = backupFiles.Concat(corruptedFiles);

                foreach (var file in allOldFiles)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.CreationTime < DateTime.Now.AddDays(-7))
                    {
                        File.Delete(file);
                        _logger?.LogDebug("已清理旧备份文件: {FilePath}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "清理旧备份文件失败");
            }
        }
    }
}