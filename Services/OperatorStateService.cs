using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 全局作业员状态服务实现
    /// 当前选中的作业员持久化到 JSON 文件，重启后保留
    /// </summary>
    public class OperatorStateService : IOperatorStateService
    {
        private readonly ILogger<OperatorStateService> _logger;
        private readonly string _stateFilePath;
        private readonly JsonSerializerOptions _jsonOptions;
        private OperatorModel? _currentOperator;

        private const string DEFAULT_OPERATOR_NAME = "默认作业员";

        public OperatorStateService(ILogger<OperatorStateService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var settingsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "设置");
            Directory.CreateDirectory(settingsFolder);
            _stateFilePath = Path.Combine(settingsFolder, "currentOperator.json");

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };

            // 启动时加载上次选择的作业员
            LoadCurrentOperator();
            _logger.LogInformation("OperatorStateService 初始化完成，当前作业员: {Operator}", CurrentOperatorName);
        }

        /// <inheritdoc/>
        public OperatorModel? CurrentOperator
        {
            get => _currentOperator;
            set
            {
                if (_currentOperator?.Id != value?.Id || _currentOperator?.Name != value?.Name)
                {
                    _currentOperator = value;
                    SaveCurrentOperator();
                    OperatorChanged?.Invoke(this, value);
                    _logger.LogInformation("当前作业员已切换为: {Operator}", CurrentOperatorName);
                }
            }
        }

        /// <inheritdoc/>
        public string CurrentOperatorName => _currentOperator?.Name ?? DEFAULT_OPERATOR_NAME;

        /// <inheritdoc/>
        public bool HasOperator => _currentOperator != null;

        /// <inheritdoc/>
        public string DefaultOperatorName => DEFAULT_OPERATOR_NAME;

        /// <inheritdoc/>
        public event EventHandler<OperatorModel?>? OperatorChanged;

        #region 持久化

        private void LoadCurrentOperator()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    var json = File.ReadAllText(_stateFilePath, System.Text.Encoding.UTF8);
                    _currentOperator = JsonSerializer.Deserialize<OperatorModel>(json, _jsonOptions);
                    _logger.LogDebug("从文件加载上次作业员: {Operator}", _currentOperator?.Name ?? "null");
                }
                else
                {
                    _currentOperator = null;
                    _logger.LogDebug("未找到上次作业员记录，使用默认作业员");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载作业员状态文件失败");
                _currentOperator = null;
            }
        }

        private void SaveCurrentOperator()
        {
            try
            {
                if (_currentOperator != null)
                {
                    var json = JsonSerializer.Serialize(_currentOperator, _jsonOptions);
                    File.WriteAllText(_stateFilePath, json, System.Text.Encoding.UTF8);
                }
                else if (File.Exists(_stateFilePath))
                {
                    File.Delete(_stateFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存作业员状态文件失败");
            }
        }

        #endregion
    }
}