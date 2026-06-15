using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 配置管理器 - 统一管理 appsettings.json 的读写操作
    /// 当前项目未使用
    /// </summary>
    public class ConfigManagerService : IDisposable
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly string _configFilePath;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);  // 使用 SemaphoreSlim 代替 lock
        private bool _disposed;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public ConfigManagerService(IConfiguration configuration)
        {
            _configuration = configuration;
            _logger = Log.ForContext<ConfigManagerService>();
            _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        }

        /// <summary>
        /// 保存配置到 appsettings.json
        /// </summary>
        public async Task<bool> SaveConfigurationAsync(Dictionary<string, object> updates)
        {
            await _semaphore.WaitAsync();
            try
            {
                return await SaveConfigurationInternalAsync(updates);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<bool> SaveConfigurationInternalAsync(Dictionary<string, object> updates)
        {
            const int MAX_RETRY = 3;
            for (int retry = 0; retry < MAX_RETRY; retry++)
            {
                try
                {
                    string json = File.Exists(_configFilePath)
                        ? await File.ReadAllTextAsync(_configFilePath, Encoding.UTF8)
                        : "{}";

                    using var doc = JsonDocument.Parse(json);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(doc.RootElement.GetRawText())
                        ?? new Dictionary<string, object>();

                    foreach (var (path, value) in updates)
                    {
                        SetNestedValue(dict, path.Split(':'), value);
                    }

                    var newJson = JsonSerializer.Serialize(dict, _jsonOptions);
                    var tempPath = _configFilePath + ".tmp";

                    await File.WriteAllTextAsync(tempPath, newJson, Encoding.UTF8);

                    if (File.Exists(_configFilePath))
                        File.Replace(tempPath, _configFilePath, _configFilePath + ".backup");
                    else
                        File.Move(tempPath, _configFilePath);

                    _logger.Information("配置保存成功");
                    return true;
                }
                catch (IOException) when (retry < MAX_RETRY - 1)
                {
                    _logger.Warning("保存配置重试 {Retry}", retry + 1);
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "保存配置失败");
                    return false;
                }
            }
            return false;
        }

        private void SetNestedValue(Dictionary<string, object> dict, string[] keys, object value, int index = 0)
        {
            if (index == keys.Length - 1)
            {
                dict[keys[index]] = value;
                return;
            }

            if (!dict.ContainsKey(keys[index]) || dict[keys[index]] is not Dictionary<string, object> nested)
            {
                nested = new Dictionary<string, object>();
                dict[keys[index]] = nested;
            }
            SetNestedValue(nested, keys, value, index + 1);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _semaphore?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}