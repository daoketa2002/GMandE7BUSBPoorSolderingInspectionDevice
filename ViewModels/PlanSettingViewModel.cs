// ============================================================
// 文件: ViewModels/PlanSettingViewModel.cs（修改部分）
// 改动说明:
//   - 移除 ScannerIntegrationHelper 字段和相关代码
//   - 注入 IDeviceConnectionManager
//   - 移除手动连接/断开扫描枪的命令和代码
//   - 移除 ConnectScanner / DisconnectScanner 命令
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
    /// 机种下拉框与方案下拉框联动：切换机种自动刷新方案列表
    /// 集成扫描枪：扫码自动填充机种名称
    /// </summary>
    public partial class PlanSettingViewModel : ObservableObject, INavigationAware
    {
        #region 服务注入

        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IPlanStorageService _planStorageService;
        private readonly ILogger<PlanSettingViewModel> _logger;


        private readonly IDeviceConnectionManager _deviceManager;

        #endregion

        #region 字段

        /// <summary>
        /// 全部方案列表（原始数据，用于本地过滤检索）
        /// </summary>
        private List<PlanModel> _allPlans = new List<PlanModel>();

        /// <summary>
        /// 标记：是否正在程序化更新机种下拉框（防止联动时重复触发检索）
        /// </summary>
        private bool _isUpdatingMachineTypeProgrammatically = false;

        #endregion

        #region 扫描枪相关属性

        [ObservableProperty]
        private bool _isScannerConnected;

        [ObservableProperty]
        private string _scannerStatusText = "扫描枪未连接";

        [ObservableProperty]
        private string _scannedBarcode = string.Empty;

        [ObservableProperty]
        private string _scannerPortName = "COM8";

        #endregion

        #region 构造函数

        public PlanSettingViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IPlanStorageService planStorageService,
            IDeviceConnectionManager deviceManager,
            ILogger<PlanSettingViewModel> logger)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ⭐ 同步扫描枪初始状态
            IsScannerConnected = _deviceManager.IsScannerConnected;
            ScannerStatusText = _deviceManager.ScannerStatusText;
            ScannerPortName = _deviceManager.IsScannerConnected ? "COM8" : "COM8";  // 从配置读取或使用默认值

            // ⭐ 订阅全局设备管理器事件
            _deviceManager.ScannerConnectionStateChanged += OnScannerConnectionStateChanged;
            _deviceManager.BarcodeScanned += OnScannerBarcodeScanned;

            // 初始化下拉选项集合
            MachineTypeOptions = new ObservableCollection<string>(PlanStorageService.DefaultMachineTypes);
            PlanNameOptions = new ObservableCollection<string>();

            _logger.LogDebug("PlanSettingViewModel 构造完成");
        }

        #endregion

        #region 检索条件属性

        /// <summary>
        /// 当前选中的机种（用户在下拉框中选择或手动输入）
        /// 变更时自动联动刷新方案下拉选项
        /// </summary>
        [ObservableProperty]
        private string _selectedMachineType = string.Empty;

        /// <summary>
        /// 当前选中的方案名称（用户在下拉框中选择或手动输入）
        /// </summary>
        [ObservableProperty]
        private string _selectedPlanName = string.Empty;

        /// <summary>
        /// 机种下拉选项列表
        /// 从所有方案文件夹中提取，合并默认机种
        /// </summary>
        public ObservableCollection<string> MachineTypeOptions { get; }

        /// <summary>
        /// 方案下拉选项列表
        /// 与机种联动：选择机种后自动刷新为该机种下的所有方案
        /// </summary>
        public ObservableCollection<string> PlanNameOptions { get; }

        /// <summary>
        /// 机种选择变更时触发（MVVM CommunityToolkit 自动生成的 partial 方法）
        /// 联动刷新方案下拉列表
        /// </summary>
        partial void OnSelectedMachineTypeChanged(string value)
        {
            // 如果是程序化更新（扫码等），不重复触发检索
            if (_isUpdatingMachineTypeProgrammatically)
                return;

            // 联动刷新方案下拉选项
            _ = RefreshPlanNameOptionsAsync(value);

            // 如果机种被清空，方案也清空
            if (string.IsNullOrWhiteSpace(value))
            {
                SelectedPlanName = string.Empty;
            }
        }

        #endregion

        #region 方案列表属性

        /// <summary>
        /// 当前展示的方案列表（经过检索过滤后的结果）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<PlanModel> _filteredPlans = new ObservableCollection<PlanModel>();

        /// <summary>
        /// 当前选中的方案行
        /// </summary>
        [ObservableProperty]
        private PlanModel? _selectedPlan;

        /// <summary>
        /// 统计文本（如 "共 5 个方案"）
        /// </summary>
        [ObservableProperty]
        private string _totalCountText = "共 0 个方案";

        /// <summary>
        /// 是否有选中的方案行（用于控制编辑/删除按钮的可用状态）
        /// </summary>
        [ObservableProperty]
        private bool _hasSelection;

        /// <summary>
        /// 选中方案变更时自动更新 HasSelection 状态
        /// </summary>
        partial void OnSelectedPlanChanged(PlanModel? value)
        {
            HasSelection = value != null;
        }

        #endregion

        #region 检索命令

        /// <summary>
        /// 执行检索：根据机种和方案名过滤方案列表
        /// </summary>
        [RelayCommand]
        private void Search()
        {
            ApplyFilter();
        }

        /// <summary>
        /// 清空所有检索条件，展示全部方案
        /// </summary>
        [RelayCommand]
        private void ClearFilter()
        {
            _isUpdatingMachineTypeProgrammatically = true;
            SelectedMachineType = string.Empty;
            _isUpdatingMachineTypeProgrammatically = false;

            SelectedPlanName = string.Empty;
            ApplyFilter();
        }

        /// <summary>
        /// 应用过滤条件到方案列表
        /// 机种为空时展示全部方案，方案名为空时不限制方案名
        /// </summary>
        private void ApplyFilter()
        {
            var filtered = _allPlans.AsEnumerable();

            var machineType = SelectedMachineType?.Trim();
            var planName = SelectedPlanName?.Trim();

            // 按机种过滤
            if (!string.IsNullOrWhiteSpace(machineType))
            {
                filtered = filtered.Where(p =>
                    p.MachineType.Contains(machineType, StringComparison.OrdinalIgnoreCase));
            }

            // 按方案名过滤
            if (!string.IsNullOrWhiteSpace(planName))
            {
                filtered = filtered.Where(p =>
                    p.PlanName.Contains(planName, StringComparison.OrdinalIgnoreCase));
            }

            // 排序：按机种→方案名
            var result = filtered
                .OrderBy(p => p.MachineType)
                .ThenBy(p => p.PlanName)
                .ToList();

            // 更新UI列表
            FilteredPlans.Clear();
            foreach (var plan in result)
            {
                FilteredPlans.Add(plan);
            }

            TotalCountText = $"共 {FilteredPlans.Count} 个方案";
            _logger.LogDebug("检索完成: 机种={MachineType}, 方案名={PlanName}, 结果数={Count}",
                machineType ?? "(全部)", planName ?? "(全部)", FilteredPlans.Count);
        }

        #endregion

        #region 方案操作命令

        /// <summary>
        /// 导航到新增方案页面
        /// </summary>
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

        /// <summary>
        /// 导航到编辑方案页面（携带当前选中的方案数据）
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

                _logger.LogInformation("用户点击编辑方案: {Plan}", SelectedPlan.PlanName);

                // 深拷贝方案对象，避免编辑过程中影响原数据
                var planCopy = System.Text.Json.JsonSerializer.Deserialize<PlanModel>(
                    System.Text.Json.JsonSerializer.Serialize(SelectedPlan));

                await _navigationService.NavigateToAsync<PlanEditView>("Main", planCopy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导航到方案编辑页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 删除选中的方案（需用户确认）
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
                    $"确定要删除方案 \"{plan.PlanName}\"（机种：{plan.MachineType}）吗？\n\n此操作不可恢复！",
                    "确认删除");

                if (!confirmed) return;

                await _planStorageService.DeletePlanAsync(plan.MachineType, plan.PlanName);
                await RefreshAllDataAsync();

                _logger.LogInformation("方案已删除: {MachineType}/{PlanName}", plan.MachineType, plan.PlanName);
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

        // ⭐ 新增：扫描枪连接状态变更回调（统一来源）
        private void OnScannerConnectionStateChanged(object? sender, DeviceConnectionStateChangedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsScannerConnected = e.IsConnected;
                ScannerStatusText = e.StatusText;
                _logger.LogInformation("扫描枪状态变更: {Status}", e.StatusText);
            });
        }

        /// <summary>
        /// 扫描枪条码接收回调
        /// 自动填充机种名称，并触发方案联动和检索
        /// </summary>
        private void OnScannerBarcodeScanned(object? sender, BarcodeParsedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ScannedBarcode = e.RawBarcode;
                _logger.LogInformation("扫描枪收到条码: {Barcode}, 机种={Model}", e.RawBarcode, e.ModelName);

                // 自动填充机种名称
                _isUpdatingMachineTypeProgrammatically = true;
                SelectedMachineType = e.ModelName;
                _isUpdatingMachineTypeProgrammatically = false;

                // 触发检索
                Search();
            });
        }


        #endregion

        #region 返回命令

        /// <summary>
        /// 返回主菜单页面
        /// </summary>
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
        /// 刷新所有数据：方案列表、机种下拉选项、方案下拉选项
        /// </summary>
        private async Task RefreshAllDataAsync()
        {
            // 加载全部方案
            _allPlans = await _planStorageService.LoadAllPlansAsync();

            // 刷新机种下拉选项
            await RefreshMachineTypeOptionsAsync();

            // 刷新方案下拉选项（根据当前选中的机种）
            await RefreshPlanNameOptionsAsync(SelectedMachineType);

            // 应用过滤条件
            ApplyFilter();
        }

        /// <summary>
        /// 刷新机种下拉选项列表
        /// </summary>
        private async Task RefreshMachineTypeOptionsAsync()
        {
            var allMachineTypes = await _planStorageService.GetAllMachineTypesAsync();

            MachineTypeOptions.Clear();
            foreach (var mt in allMachineTypes)
            {
                MachineTypeOptions.Add(mt);
            }

            _logger.LogDebug("机种下拉选项已刷新，共 {Count} 个", MachineTypeOptions.Count);
        }

        /// <summary>
        /// 根据机种刷新方案下拉选项列表
        /// 机种为空时方案列表也为空
        /// </summary>
        /// <param name="machineType">机种名称</param>
        private async Task RefreshPlanNameOptionsAsync(string machineType)
        {
            PlanNameOptions.Clear();

            if (string.IsNullOrWhiteSpace(machineType))
            {
                _logger.LogDebug("机种为空，方案下拉选项已清空");
                return;
            }

            var planNames = await _planStorageService.GetPlanNamesByMachineTypeAsync(machineType);

            foreach (var name in planNames)
            {
                PlanNameOptions.Add(name);
            }

            _logger.LogDebug("方案下拉选项已刷新，机种={MachineType}，共 {Count} 个方案",
                machineType, PlanNameOptions.Count);
        }

        #endregion

        #region INavigationAware 实现

        /// <summary>
        /// 页面导航进入时触发
        /// 刷新全部数据，订阅扫描枪事件，自动连接扫描枪
        /// </summary>
        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogDebug("进入方案设定页面");

            // 刷新全部数据
            await RefreshAllDataAsync();

            // ⭐ 重新订阅扫码事件（确保不重复订阅）
            _deviceManager.BarcodeScanned -= OnScannerBarcodeScanned;
            _deviceManager.BarcodeScanned += OnScannerBarcodeScanned;

            // ⭐ 同步扫描枪连接状态（不再手动连接）
            IsScannerConnected = _deviceManager.IsScannerConnected;
            ScannerStatusText = _deviceManager.ScannerStatusText;

        }

        /// <summary>
        /// 页面导航离开时触发
        /// 必须取消订阅扫描枪事件，防止在其他页面扫码时误触发本页逻辑
        /// </summary>
        public Task OnNavigatedFromAsync()
        {
            _logger.LogDebug("离开方案设定页面");

            // ⭐ 页面离开时取消订阅扫码事件，避免在其他页面触发
            if (_deviceManager != null)
            {
                _deviceManager.BarcodeScanned -= OnScannerBarcodeScanned;
            }

            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion
    }
}