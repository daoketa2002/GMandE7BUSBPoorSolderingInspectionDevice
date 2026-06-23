// ============================================================
// 文件: ViewModels/PlanSettingViewModel.cs
// 描述: 方案设定页面 ViewModel
// 改动:
//   - 统一使用 ILogger<PlanSettingViewModel>（替代 Serilog.ILogger）
//   - 使用 ScannerIntegrationHelper 替代内联扫描枪代码，降低耦合
// ============================================================

using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Common;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 方案设定页面 ViewModel
    /// 管理检测方案的检索、新增、编辑、删除
    /// 集成扫描枪：扫码自动填充机种名称并推断系列
    /// </summary>
    public partial class PlanSettingViewModel : ObservableObject, INavigationAware
    {
        #region 服务注入

        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IPlanStorageService _planStorageService;
        private readonly ILogger<PlanSettingViewModel> _logger;

        // ⭐ 扫描枪集成帮助类（替代原有内联代码）
        private readonly ScannerIntegrationHelper _scannerHelper;

        #endregion

        #region 字段

        /// <summary>全部方案列表（原始数据，用于本地过滤）</summary>
        private List<PlanModel> _allPlans = new List<PlanModel>();

        #endregion

        #region 扫描枪相关属性（通过 Helper 暴露）

        /// <summary>扫描枪是否已连接</summary>
        [ObservableProperty]
        private bool _isScannerConnected;

        /// <summary>扫描枪状态文本</summary>
        [ObservableProperty]
        private string _scannerStatusText = "扫描枪未连接";

        /// <summary>最近扫描的原始条码</summary>
        [ObservableProperty]
        private string _scannedBarcode = string.Empty;

        /// <summary>扫描枪端口名</summary>
        [ObservableProperty]
        private string _scannerPortName = "COM9";

        #endregion

        #region 构造函数

        public PlanSettingViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IPlanStorageService planStorageService,
            IScannerBarcodeService? scannerService,
            ILogger<PlanSettingViewModel> logger)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ⭐ 初始化扫描枪帮助类
            _scannerHelper = new ScannerIntegrationHelper(scannerService, _logger);

            // ⭐ 同步初始状态
            IsScannerConnected = _scannerHelper.IsScannerConnected;
            ScannerStatusText = _scannerHelper.ScannerStatusText;
            ScannerPortName = _scannerHelper.PortName;

            // ⭐ 订阅扫描枪事件（通过 Helper 转发）
            _scannerHelper.ScannerConnected += OnScannerConnected;
            _scannerHelper.ScannerDisconnected += OnScannerDisconnected;
            _scannerHelper.BarcodeScanned += OnScannerBarcodeScanned;

            // 初始化下拉选项
            SeriesOptions = new ObservableCollection<string>(PlanStorageService.DefaultSeries);
            ModelOptions = new ObservableCollection<string>(PlanStorageService.DefaultModels);

            _logger.LogDebug("PlanSettingViewModel 构造完成，扫描枪: {HasScanner}", scannerService != null);
        }

        #endregion

        #region 检索条件

        [ObservableProperty]
        private string _filterSeries = string.Empty;

        [ObservableProperty]
        private string _filterModel = string.Empty;

        [ObservableProperty]
        private string _filterPlanName = string.Empty;

        /// <summary>系列下拉选项</summary>
        public ObservableCollection<string> SeriesOptions { get; }

        /// <summary>型号下拉选项</summary>
        public ObservableCollection<string> ModelOptions { get; }

        #endregion

        #region 方案列表

        [ObservableProperty]
        private ObservableCollection<PlanModel> _plans = new ObservableCollection<PlanModel>();

        [ObservableProperty]
        private PlanModel? _selectedPlan;

        [ObservableProperty]
        private string _totalCountText = "共 0 个方案";

        [ObservableProperty]
        private bool _hasSelection;

        partial void OnSelectedPlanChanged(PlanModel? value)
        {
            HasSelection = value != null;
        }

        #endregion

        #region 检索命令

        [RelayCommand]
        private void Search()
        {
            ApplyFilter();
        }

        [RelayCommand]
        private void ClearFilter()
        {
            FilterSeries = string.Empty;
            FilterModel = string.Empty;
            FilterPlanName = string.Empty;
            ApplyFilter();
        }

        /// <summary>
        /// 应用过滤条件（AND组合，空条件跳过，全空返回全部方案）
        /// </summary>
        private void ApplyFilter()
        {
            var filtered = _allPlans.AsEnumerable();

            var series = FilterSeries?.Trim();
            var model = FilterModel?.Trim();
            var planName = FilterPlanName?.Trim();

            if (!string.IsNullOrWhiteSpace(series))
                filtered = filtered.Where(p => p.Series.Contains(series, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(model))
                filtered = filtered.Where(p => p.Model.Contains(model, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(planName))
                filtered = filtered.Where(p => p.PlanName.Contains(planName, StringComparison.OrdinalIgnoreCase));

            var result = filtered.OrderBy(p => p.Series)
                                 .ThenBy(p => p.Model)
                                 .ThenBy(p => p.PlanName)
                                 .ToList();

            Plans.Clear();
            foreach (var p in result)
            {
                Plans.Add(p);
            }
            TotalCountText = $"共 {Plans.Count} 个方案";
        }

        #endregion

        #region 方案操作命令

        [RelayCommand]
        private async Task AddPlanAsync()
        {
            try
            {
                _logger.LogInformation("用户点击新增方案");
                await _navigationService.NavigateToAsync<PlanEditView>("Main", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导航到方案编辑页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        [RelayCommand]
        private async Task EditPlanAsync()
        {
            try
            {
                if (SelectedPlan == null)
                {
                    await _notificationService.ShowWarningAsync("请先选择要编辑的方案！", "未选择");
                    return;
                }

                _logger.LogInformation("用户点击编辑方案: {Plan}", SelectedPlan.PlanName);
                var clone = System.Text.Json.JsonSerializer.Deserialize<PlanModel>(
                    System.Text.Json.JsonSerializer.Serialize(SelectedPlan));
                await _navigationService.NavigateToAsync<PlanEditView>("Main", clone);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导航到方案编辑页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        [RelayCommand]
        private async Task DeletePlanAsync()
        {
            try
            {
                if (SelectedPlan == null)
                {
                    await _notificationService.ShowWarningAsync("请先选择要删除的方案！", "未选择");
                    return;
                }

                var plan = SelectedPlan;
                var confirmed = await _notificationService.ConfirmAsync(
                    $"确定要删除方案 \"{plan.PlanName}\"（{plan.Series} - {plan.Model}）吗？\n\n此操作不可恢复！",
                    "确认删除");

                if (!confirmed) return;

                await _planStorageService.DeletePlanAsync(plan.Series, plan.Model, plan.PlanName);
                await RefreshPlansAsync();

                _logger.LogInformation("方案已删除: {Plan}", plan.PlanName);
                await _notificationService.ShowInfoAsync($"方案 \"{plan.PlanName}\" 已删除", "删除成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除方案失败");
                await _notificationService.ShowErrorAsync($"删除失败：{ex.Message}");
            }
        }

        #endregion

        #region 扫描枪事件处理

        /// <summary>
        /// 扫描枪连接成功回调
        /// 更新连接状态并同步 UI 属性
        /// </summary>
        private void OnScannerConnected()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsScannerConnected = true;
                ScannerStatusText = _scannerHelper.ScannerStatusText;
                _logger.LogInformation("扫描枪已连接: {Port}", ScannerPortName);
            });
        }

        /// <summary>
        /// 扫描枪断开连接回调
        /// 更新连接状态并同步 UI 属性
        /// </summary>
        private void OnScannerDisconnected()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsScannerConnected = false;
                ScannerStatusText = _scannerHelper.ScannerStatusText;
                _logger.LogInformation("扫描枪已断开");
            });
        }

        /// <summary>
        /// 扫描枪条码接收回调
        /// 自动填充机种名称（FilterModel）并推断系列（FilterSeries），然后触发检索
        /// </summary>
        private void OnScannerBarcodeScanned(BarcodeParsedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ScannedBarcode = e.RawBarcode;
                _logger.LogInformation("扫描枪收到条码: {Barcode}, 机种={Model}", e.RawBarcode, e.ModelName);

                // 自动填充机种名称
                FilterModel = e.ModelName;

                // 自动推断系列
                if (string.IsNullOrWhiteSpace(FilterSeries))
                {
                    InferSeriesFromModel(e.ModelName);
                }

                // 触发检索
                Search();
            });
        }

        /// <summary>
        /// 从机种名称推断系列
        /// 规则：T998开头→GM5，998开头→E78，否则从已加载方案中查找
        /// </summary>
        private void InferSeriesFromModel(string modelName)
        {
            if (modelName.StartsWith("T998", StringComparison.OrdinalIgnoreCase))
            {
                FilterSeries = "GM5";
            }
            else if (modelName.StartsWith("998", StringComparison.OrdinalIgnoreCase))
            {
                FilterSeries = "E78";
            }
            else
            {
                var matchedPlan = _allPlans.FirstOrDefault(p =>
                    p.Model.Equals(modelName, StringComparison.OrdinalIgnoreCase));
                if (matchedPlan != null)
                {
                    FilterSeries = matchedPlan.Series;
                }
            }
        }

        /// <summary>
        /// 手动连接扫描枪命令
        /// </summary>
        [RelayCommand]
        private void ConnectScanner()
        {
            try
            {
                if (_scannerHelper == null)
                {
                    _logger.LogWarning("扫描枪帮助类未初始化");
                    _ = _notificationService.ShowWarningAsync("扫描枪服务未初始化", "提示");
                    return;
                }

                var connected = _scannerHelper.Connect(ScannerPortName);
                if (connected)
                {
                    IsScannerConnected = true;
                    ScannerStatusText = _scannerHelper.ScannerStatusText;
                    _ = _notificationService.ShowInfoAsync($"扫描枪连接成功: {ScannerPortName}", "连接成功");
                }
                else
                {
                    _ = _notificationService.ShowWarningAsync(
                        $"扫描枪连接失败，请检查:\n1. 端口 {ScannerPortName} 是否存在\n2. 扫描枪是否已切换为USB串口模式",
                        "连接失败");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "连接扫描枪失败");
                _ = _notificationService.ShowErrorAsync($"扫描枪连接失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 断开扫描枪连接命令
        /// </summary>
        [RelayCommand]
        private void DisconnectScanner()
        {
            try
            {
                _scannerHelper?.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "断开扫描枪失败");
            }
        }

        #endregion

        #region 返回命令

        [RelayCommand]
        private async Task GoBackAsync()
        {
            try
            {
                _logger.LogInformation("用户点击返回主菜单");
                await _navigationService.NavigateToAsync<MainMenuView>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "返回主菜单失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 刷新方案列表及下拉选项
        /// </summary>
        private async Task RefreshPlansAsync()
        {
            _allPlans = await _planStorageService.LoadAllPlansAsync();
            ApplyFilter();

            // 刷新系列下拉选项
            var allSeries = await _planStorageService.GetAllSeriesAsync();
            SeriesOptions.Clear();
            foreach (var s in allSeries)
                SeriesOptions.Add(s);

            // 刷新型号下拉选项
            var allModels = await _planStorageService.GetModelsBySeriesAsync(FilterSeries);
            if (!string.IsNullOrWhiteSpace(FilterSeries))
            {
                ModelOptions.Clear();
                foreach (var m in allModels)
                    ModelOptions.Add(m);
            }
            else
            {
                ModelOptions.Clear();
                foreach (var m in PlanStorageService.DefaultModels)
                    ModelOptions.Add(m);
            }
        }

        #endregion

        #region INavigationAware 实现

        /// <summary>
        /// 页面导航进入时触发
        /// 刷新方案列表，订阅并自动连接扫描枪
        /// </summary>
        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogDebug("进入方案设定页面");

            // 刷新列表
            await RefreshPlansAsync();

            // ⭐ 订阅扫描枪事件
            _scannerHelper.Subscribe();

            // 同步连接状态
            IsScannerConnected = _scannerHelper.IsScannerConnected;
            ScannerStatusText = _scannerHelper.ScannerStatusText;

            // ⭐ 自动连接扫描枪（如果尚未连接）
            if (_scannerHelper != null && !_scannerHelper.IsScannerConnected)
            {
                await Task.Run(() => _scannerHelper.Connect(ScannerPortName));
            }
        }

        /// <summary>
        /// 页面导航离开时触发
        /// ⭐ 必须取消订阅，防止在其他页面扫码时误触发本页检索逻辑
        /// </summary>
        public Task OnNavigatedFromAsync()
        {
            _logger.LogDebug("离开方案设定页面");

            _scannerHelper.Unsubscribe();

            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion
    }
}