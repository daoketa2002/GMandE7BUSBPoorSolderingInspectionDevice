using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
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
    /// 基于 JSON 文件的作业员存储服务
    /// </summary>
    public class OperatorStorageService : IOperatorStorageService
    {
        private readonly ILogger<OperatorStorageService> _logger;
        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly object _lock = new();

        public OperatorStorageService(ILogger<OperatorStorageService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var settingsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "设置");
            Directory.CreateDirectory(settingsFolder);

            _filePath = Path.Combine(settingsFolder, "operators.json");
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };

            _logger.LogInformation("作业员存储服务初始化完成，文件路径: {FilePath}", _filePath);
        }

        /// <inheritdoc/>
        public async Task<List<OperatorModel>> LoadOperatorsAsync()
        {
            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    if (!File.Exists(_filePath))
                    {
                        _logger.LogInformation("作业员文件不存在，返回空列表");
                        return new List<OperatorModel>();
                    }

                    try
                    {
                        var json = File.ReadAllText(_filePath, System.Text.Encoding.UTF8);
                        var operators = JsonSerializer.Deserialize<List<OperatorModel>>(json, _jsonOptions)
                                       ?? new List<OperatorModel>();

                        _logger.LogInformation("从文件加载了 {Count} 名作业员", operators.Count);
                        return operators;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "加载作业员文件失败，返回空列表");
                        return new List<OperatorModel>();
                    }
                }
            });
        }

        /// <inheritdoc/>
        public async Task SaveOperatorsAsync(List<OperatorModel> operators)
        {
            await Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(operators, _jsonOptions);
                        File.WriteAllText(_filePath, json, System.Text.Encoding.UTF8);
                        _logger.LogInformation("已保存 {Count} 名作业员到文件", operators.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "保存作业员文件失败");
                        throw;
                    }
                }
            });
        }

        /// <inheritdoc/>
        public async Task<int> GetNextIdAsync()
        {
            var operators = await LoadOperatorsAsync();
            return operators.Any() ? operators.Max(o => o.Id) + 1 : 1;
        }
    }
}