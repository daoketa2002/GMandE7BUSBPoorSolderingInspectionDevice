using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 全局当前方案服务实现
    /// 持久化到JSON文件，重启后保留选择
    /// </summary>
    public class CurrentPlanService : ICurrentPlanService
    {
        private readonly ILogger<CurrentPlanService> _logger;
        private readonly string _stateFilePath;
        private readonly JsonSerializerOptions _jsonOptions;
        private PlanModel? _currentPlan;

        public CurrentPlanService(ILogger<CurrentPlanService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var settingsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "设置");
            Directory.CreateDirectory(settingsFolder);
            _stateFilePath = Path.Combine(settingsFolder, "currentPlan.json");

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };

            LoadCurrentPlan();
            _logger.LogInformation("CurrentPlanService 初始化完成，当前方案: {Plan}", CurrentPlanName);
        }

        public PlanModel? CurrentPlan
        {
            get => _currentPlan;
            set
            {
                if (_currentPlan?.PlanName != value?.PlanName ||
                    _currentPlan?.Series != value?.Series ||
                    _currentPlan?.Model != value?.Model)
                {
                    _currentPlan = value;
                    SaveCurrentPlan();
                    CurrentPlanChanged?.Invoke(this, value);
                    _logger.LogInformation("当前方案已切换为: {Plan}", CurrentPlanName);
                }
            }
        }

        public string CurrentPlanName
        {
            get
            {
                if (_currentPlan == null) return "未选择方案";
                return $"{_currentPlan.Series} - {_currentPlan.Model} - {_currentPlan.PlanName}";
            }
        }

        public bool HasPlan => _currentPlan != null;

        public event EventHandler<PlanModel?>? CurrentPlanChanged;

        private void LoadCurrentPlan()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    var json = File.ReadAllText(_stateFilePath, System.Text.Encoding.UTF8);
                    _currentPlan = JsonSerializer.Deserialize<PlanModel>(json, _jsonOptions);
                    _logger.LogDebug("从文件加载当前方案: {Plan}", CurrentPlanName);
                }
                else
                {
                    _currentPlan = null;
                    _logger.LogDebug("未找到当前方案记录");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载当前方案状态文件失败");
                _currentPlan = null;
            }
        }

        private void SaveCurrentPlan()
        {
            try
            {
                if (_currentPlan != null)
                {
                    var json = JsonSerializer.Serialize(_currentPlan, _jsonOptions);
                    File.WriteAllText(_stateFilePath, json, System.Text.Encoding.UTF8);
                }
                else if (File.Exists(_stateFilePath))
                {
                    File.Delete(_stateFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存当前方案状态文件失败");
            }
        }
    }
}