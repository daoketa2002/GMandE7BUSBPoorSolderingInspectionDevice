using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    public partial class PlanSettingViewModel : ObservableObject, INavigationAware
    {
        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IPlanStorageService _planStorageService;
        private readonly Serilog.ILogger _logger;

        // 全部方案列表（原始数据）
        private List<PlanModel> _allPlans = new List<PlanModel>();

        // === 扫描枪相关 ===
        private readonly HoneywellH1900Scanner? _scanner;
        private bool _scannerSubscribed = false;

        /// <summary>
        /// 扫描枪是否已连接
        /// </summary>
        [ObservableProperty]
        private bool _isScannerConnected = false;

        /// <summary>
        /// 扫描枪状态文本
        /// </summary>
        [ObservableProperty]
        private string _scannerStatusText = "扫描枪未连接";

        /// <summary>
        /// 最近扫描的原始条码
        /// </summary>
        [ObservableProperty]
        private string _scannedBarcode = string.Empty;

        /// <summary>
        /// 扫描枪端口名（配置文件读取）
        /// </summary>
        [ObservableProperty]
        private string _scannerPortName = "COM9";

        public PlanSettingViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IPlanStorageService planStorageService,
            HoneywellH1900Scanner? scanner = null)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _logger = Log.ForContext<PlanSettingViewModel>();
            _scanner = scanner;  // ← 允许为null

            // 初始化下拉选项
            SeriesOptions = new ObservableCollection<string>(PlanStorageService.DefaultSeries);
            ModelOptions = new ObservableCollection<string>(PlanStorageService.DefaultModels);

            // 订阅扫描枪事件
            SubscribeScannerEvents();

            _logger.Debug("PlanSettingViewModel 构造完成，扫描枪: {HasScanner}", _scanner != null);
        }

        #region 检索条件

        [ObservableProperty]
        private string _filterSeries = string.Empty;

        [ObservableProperty]
        private string _filterModel = string.Empty;

        [ObservableProperty]
        private string _filterPlanName = string.Empty;

        /// <summary>
        /// 系列下拉选项
        /// </summary>
        public ObservableCollection<string> SeriesOptions { get; }

        /// <summary>
        /// 型号下拉选项
        /// </summary>
        public ObservableCollection<string> ModelOptions { get; }

        #endregion

        #region 方案列表

        /// <summary>
        /// 显示的方案列表（过滤后）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<PlanModel> _plans = new ObservableCollection<PlanModel>();

        /// <summary>
        /// 表格选中的方案
        /// </summary>
        [ObservableProperty]
        private PlanModel? _selectedPlan;

        /// <summary>
        /// 总方案数显示
        /// </summary>
        [ObservableProperty]
        private string _totalCountText = "共 0 个方案";

        /// <summary>
        /// 是否有选中方案（用于按钮启用/禁用）
        /// </summary>
        [ObservableProperty]
        private bool _hasSelection;

        /// <summary>
        /// 方案总数（内部用）
        /// </summary>
        private int _totalPlanCount;

        partial void OnSelectedPlanChanged(PlanModel? value)
        {
            HasSelection = value != null;
        }

        #endregion

        #region 检索命令

        /// <summary>
        /// 执行检索
        /// </summary>
        [RelayCommand]
        private void Search()
        {
            ApplyFilter();
        }

        /// <summary>
        /// 清空检索条件
        /// </summary>
        [RelayCommand]
        private void ClearFilter()
        {
            FilterSeries = string.Empty;
            FilterModel = string.Empty;
            FilterPlanName = string.Empty;
            ApplyFilter();
        }

        /// <summary>
        /// 应用过滤条件，初版
        /// </summary>
        //private void ApplyFilter()
        //{
        //    var filtered = _allPlans.AsEnumerable();

        //    if (!string.IsNullOrWhiteSpace(FilterSeries))
        //    {
        //        var series = FilterSeries.Trim();
        //        filtered = filtered.Where(p => p.Series.Contains(series, StringComparison.OrdinalIgnoreCase));
        //    }

        //    if (!string.IsNullOrWhiteSpace(FilterModel))
        //    {
        //        var model = FilterModel.Trim();
        //        filtered = filtered.Where(p => p.Model.Contains(model, StringComparison.OrdinalIgnoreCase));
        //    }

        //    if (!string.IsNullOrWhiteSpace(FilterPlanName))
        //    {
        //        var planName = FilterPlanName.Trim();
        //        filtered = filtered.Where(p => p.PlanName.Contains(planName, StringComparison.OrdinalIgnoreCase));
        //    }

        //    Plans = new ObservableCollection<PlanModel>(filtered.OrderBy(p => p.Series).ThenBy(p => p.Model).ThenBy(p => p.PlanName));
        //    TotalCountText = $"共 {Plans.Count} 个方案";
        //    _totalPlanCount = Plans.Count;
        //}

        /// <summary>
        /// 应用过滤条件（AND组合，空条件跳过，全空返回全部方案）
        /// </summary>
        private void ApplyFilter()
        {
            var filtered = _allPlans.AsEnumerable();

            var series = FilterSeries?.Trim();
            var model = FilterModel?.Trim();
            var planName = FilterPlanName?.Trim();

            // AND组合：有值的条件参与筛选，空的条件跳过
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
            _totalPlanCount = Plans.Count;
        }

        #endregion

        #region 方案操作命令

        /// <summary>
        /// 新增方案 → 导航到编辑页面
        /// </summary>
        [RelayCommand]
        private async Task AddPlanAsync()
        {
            try
            {
                _logger.Information("用户点击新增方案");
                await _navigationService.NavigateToAsync<PlanEditView>("Main", null);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到方案编辑页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 编辑方案 → 导航到编辑页面，传入选中方案
        /// </summary>
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

                _logger.Information("用户点击编辑方案: {Plan}", SelectedPlan.PlanName);
                // 传入选中方案的深拷贝
                var clone = System.Text.Json.JsonSerializer.Deserialize<PlanModel>(
                    System.Text.Json.JsonSerializer.Serialize(SelectedPlan));
                await _navigationService.NavigateToAsync<PlanEditView>("Main", clone);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到方案编辑页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 删除选中方案
        /// </summary>
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
                _logger.Information("方案已删除: {Plan}", plan.PlanName);
                await _notificationService.ShowInfoAsync($"方案 \"{plan.PlanName}\" 已删除", "删除成功");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "删除方案失败");
                await _notificationService.ShowErrorAsync($"删除失败：{ex.Message}");
            }
        }

        #endregion

        #region 扫描枪集成

        /// <summary>
        /// 订阅扫描枪事件
        /// </summary>
        private void SubscribeScannerEvents()
        {
            if (_scanner == null || _scannerSubscribed) return;

            _scanner.BarcodeReceived += OnScannerBarcodeReceived;
            _scanner.ConnectionStateChanged += OnScannerConnectionChanged;
            _scannerSubscribed = true;

            // 初始状态同步
            IsScannerConnected = _scanner.IsConnected;
            ScannerStatusText = _scanner.IsConnected ? $"扫描枪已连接 ({_scanner.PortName})" : "扫描枪未连接";

            _logger.Debug("已订阅扫描枪事件");
        }

        /// <summary>
        /// 取消订阅扫描枪事件
        /// </summary>
        private void UnsubscribeScannerEvents()
        {
            if (_scanner == null || !_scannerSubscribed) return;

            _scanner.BarcodeReceived -= OnScannerBarcodeReceived;
            _scanner.ConnectionStateChanged -= OnScannerConnectionChanged;
            _scannerSubscribed = false;

            _logger.Debug("已取消订阅扫描枪事件");
        }

        /// <summary>
        /// 扫描枪连接状态变更处理
        /// </summary>
        private void OnScannerConnectionChanged(object? sender, bool isConnected)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsScannerConnected = isConnected;
                ScannerStatusText = isConnected ? $"扫描枪已连接 ({_scanner?.PortName})" : "扫描枪已断开";
                _logger.Information("扫描枪连接状态: {Status}", ScannerStatusText);
            });
        }

        /// <summary>
        /// 扫描枪条码接收处理
        /// 解析格式："T998248391,250919,00004Z"
        ///   第1段 → 型号（机种名称）
        ///   第2段 → 日期（忽略）
        ///   第3段 → 序列号（忽略）
        /// </summary>
        private void OnScannerBarcodeReceived(object? sender, BarcodeReceivedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ScannedBarcode = e.Barcode;
                _logger.Information("扫描枪收到条码: {Barcode}", e.Barcode);

                ParseAndApplyBarcode(e.Barcode);
            });
        }

        /// <summary>
        /// 解析条码并应用到检索条件
        /// 
        /// 条码格式示例："T998248391,250919,00004Z"
        ///   第1段 → 机种名称（型号）
        ///   第2段 → 日期（忽略）
        ///   第3段 → 序列号（忽略）
        /// 
        /// 系列推断策略（按优先级）：
        ///   1. 前缀推断：T998开头→GM5，998开头→E78
        ///   2. 反向查找：从已加载方案库中查找该机种所属系列
        ///   3. 兜底：找不到系列则仅用机种名称检索
        /// </summary>
        private void ParseAndApplyBarcode(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode))
                return;

            try
            {
                // 按逗号分割
                var parts = barcode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 1)
                {
                    var modelName = parts[0].Trim();

                    if (!string.IsNullOrWhiteSpace(modelName))
                    {
                        // 填充机种名称
                        FilterModel = modelName;

                        // 如果系列为空，尝试推断系列
                        if (string.IsNullOrWhiteSpace(FilterSeries))
                        {
                            // 策略1：前缀推断（硬编码规则，速度快）
                            if (modelName.StartsWith("T998", StringComparison.OrdinalIgnoreCase))
                            {
                                FilterSeries = "GM5";
                                _logger.Information("扫描枪：前缀推断系列=GM5（型号以T998开头）");
                            }
                            else if (modelName.StartsWith("998", StringComparison.OrdinalIgnoreCase))
                            {
                                FilterSeries = "E78";
                                _logger.Information("扫描枪：前缀推断系列=E78（型号以998开头）");
                            }
                            // 策略2：反向查找方案库（兜底，适用手动添加的非标机种）
                            else
                            {
                                var matchedPlan = _allPlans.FirstOrDefault(p =>
                                    p.Model.Equals(modelName, StringComparison.OrdinalIgnoreCase));

                                if (matchedPlan != null)
                                {
                                    FilterSeries = matchedPlan.Series;
                                    _logger.Information("扫描枪：方案库反向查找到系列={Series}（机种={Model}）",
                                        matchedPlan.Series, modelName);
                                }
                                else
                                {
                                    // 策略3：找不到系列，仅用机种名称检索
                                    _logger.Information("扫描枪：无法推断系列，仅用机种名称检索（机种={Model}）", modelName);
                                }
                            }
                        }

                        _logger.Information("扫描枪解析完成: 系列={Series}, 机种={Model}", FilterSeries, FilterModel);

                        // 自动触发检索
                        Search();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "解析扫描枪条码失败: {Barcode}", barcode);
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
                if (_scanner == null)
                {
                    _logger.Warning("扫描枪服务未注册");
                    _ = _notificationService.ShowWarningAsync("扫描枪服务未初始化", "提示");
                    return;
                }

                var connected = _scanner.Connect(ScannerPortName);
                if (connected)
                {
                    IsScannerConnected = true;
                    ScannerStatusText = $"扫描枪已连接 ({ScannerPortName})";
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
                _logger.Error(ex, "连接扫描枪失败");
                _ = _notificationService.ShowErrorAsync($"扫描枪连接失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 断开扫描枪命令
        /// </summary>
        [RelayCommand]
        private void DisconnectScanner()
        {
            try
            {
                _scanner?.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "断开扫描枪失败");
            }
        }

        #endregion

        #region 返回命令

        [RelayCommand]
        private async Task GoBackAsync()
        {
            try
            {
                _logger.Information("用户点击返回主菜单");
                await _navigationService.NavigateToAsync<MainMenuView>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "返回主菜单失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 刷新方案列表
        /// </summary>
        private async Task RefreshPlansAsync()
        {
            _allPlans = await _planStorageService.LoadAllPlansAsync();
            ApplyFilter();

            // 刷新下拉选项
            var allSeries = await _planStorageService.GetAllSeriesAsync();
            SeriesOptions.Clear();
            foreach (var s in allSeries)
                SeriesOptions.Add(s);

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

        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.Debug("进入方案设定页面");

            // 刷新列表
            await RefreshPlansAsync();

            // 订阅扫描枪事件
            SubscribeScannerEvents();

            // 添加原始数据监听（调试用）
            if (_scanner != null)
            {
                _scanner.RawDataReceived += OnScannerRawDataReceived;
            }

            // 自动连接扫描枪
            if (_scanner != null && !_scanner.IsConnected)
            {
                await Task.Run(() => _scanner.Connect(ScannerPortName));
            }
        }

        /// <summary>
        /// 扫描枪原始数据接收（调试用，排查扫描问题）
        /// </summary>
        private void OnScannerRawDataReceived(object? sender, string rawData)
        {
            _logger.Information("🔍 扫描枪原始数据: {Raw}", rawData.Replace("\r", "\\r").Replace("\n", "\\n"));
        }

        public Task OnNavigatedFromAsync()
        {
            _logger.Debug("离开方案设定页面");

            // 取消原始数据监听
            if (_scanner != null)
            {
                _scanner.RawDataReceived -= OnScannerRawDataReceived;
            }

            UnsubscribeScannerEvents();  // 页面离开时取消订阅扫描枪事件
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion
    }
}