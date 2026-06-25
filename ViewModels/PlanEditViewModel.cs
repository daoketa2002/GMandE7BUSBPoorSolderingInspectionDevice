// ============================================================
// 文件: ViewModels/PlanEditViewModel.cs
// 描述: 方案编辑/新增页面 ViewModel
// 改动:
//   - 去除"系列"字段，改为"机种"字段
//   - 使用 ScannerIntegrationHelper 替代内联扫描枪代码
//   - CheckMode 存储中文 "导通"/"电阻值"
//   - 新增 CheckModeConstants 引用，消除硬编码字符串
// ============================================================

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
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 方案编辑/新增页面 ViewModel
    /// 管理检测方案的创建和编辑，包含检测项目列表的增删改和排序
    /// 集成扫描枪：扫码自动填充机种名称（MachineType）
    /// </summary>
    public partial class PlanEditViewModel : ObservableObject, INavigationAware
    {
        #region 服务注入

        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IPlanStorageService _planStorageService;
        private readonly IDeviceConnectionManager _deviceManager;
        private readonly ILogger<PlanEditViewModel> _logger;

        #endregion

        #region 字段

        /// <summary>
        /// 是否为编辑模式（true=编辑已有方案，false=新增方案）
        /// </summary>
        private bool _isEditMode = false;

        /// <summary>
        /// 编辑模式下的原始方案（用于处理机种变更时的文件移动）
        /// </summary>
        private PlanModel? _originalPlan;

        #endregion

        #region 扫描枪相关属性（通过 ScannerIntegrationHelper 暴露给UI绑定）

        [ObservableProperty]
        private bool _isScannerConnected;

        [ObservableProperty]
        private string _scannerStatusText = "扫描枪未连接";

        [ObservableProperty]
        private string _scannedBarcode = string.Empty;

        [ObservableProperty]
        private string _scannerPortName = "COM9";

        #endregion

        #region 构造函数

        public PlanEditViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IPlanStorageService planStorageService,
            IDeviceConnectionManager deviceManager,
            ILogger<PlanEditViewModel> logger)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ⭐ 同步扫描枪初始状态
            IsScannerConnected = _deviceManager.IsScannerConnected;
            ScannerStatusText = _deviceManager.ScannerStatusText;
            ScannerPortName = _deviceManager.IsScannerConnected ? "COM8" : "COM8";

            // ⭐ 订阅全局设备管理器事件
            _deviceManager.ScannerConnectionStateChanged += OnScannerConnectionStateChanged;
            _deviceManager.BarcodeScanned += OnScannerBarcodeScanned;

            // 初始化下拉选项
            MachineTypeOptions = new ObservableCollection<string>(PlanStorageService.DefaultMachineTypes);
            PinOptions = new ObservableCollection<string>(PlanStorageService.PinList);
            // ★ CheckMode 下拉选项使用中文（不再需要单独的 CheckModeOptions，已内聚到 PlanItemViewModel）
            ContinuityUnitOptions = new ObservableCollection<string> { "OPEN", "SHORT" };

            _logger.LogDebug("PlanEditViewModel 构造完成");
        }

        #endregion

        #region 基本信息属性

        /// <summary>
        /// 机种名称（如 T998248391）
        /// 对应方案保存的文件夹名
        /// </summary>
        [ObservableProperty]
        private string _machineType = string.Empty;

        /// <summary>
        /// 方案名称（如 "方案A"）
        /// 对应方案保存的文件名（不含扩展名）
        /// </summary>
        [ObservableProperty]
        private string _planName = string.Empty;

        /// <summary>
        /// 方案创建时间（只读展示）
        /// </summary>
        [ObservableProperty]
        private DateTime _createdTime = DateTime.Now;

        /// <summary>
        /// 方案最后修改时间（只读展示）
        /// </summary>
        [ObservableProperty]
        private DateTime _lastModifiedTime = DateTime.Now;

        /// <summary>
        /// 机种下拉选项列表
        /// </summary>
        public ObservableCollection<string> MachineTypeOptions { get; }

        /// <summary>
        /// 引脚下拉选项（A1~A20, B1~B20）
        /// </summary>
        public ObservableCollection<string> PinOptions { get; }

        /// <summary>
        /// 导通模式下的期望结果选项
        /// OPEN — 期望开路（回路断开）
        /// SHORT — 期望短路（回路导通）
        /// </summary>
        public ObservableCollection<string> ContinuityUnitOptions { get; }

        #endregion

        #region 项目列表属性

        /// <summary>
        /// 检测项目集合（绑定到DataGrid）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<PlanItemViewModel> _items = new ObservableCollection<PlanItemViewModel>();

        /// <summary>
        /// 当前选中的检测项目行
        /// </summary>
        [ObservableProperty]
        private PlanItemViewModel? _selectedItem;

        /// <summary>
        /// 是否有选中的检测项目（用于控制上移/下移/删除按钮状态）
        /// </summary>
        [ObservableProperty]
        private bool _hasItemSelection;

        partial void OnSelectedItemChanged(PlanItemViewModel? value)
        {
            HasItemSelection = value != null;
        }

        #endregion

        #region 项目操作命令

        /// <summary>
        /// 新增一个检测项目（默认检查方式为导通）
        /// </summary>
        [RelayCommand]
        private void AddItem()
        {
            // ★ 限制检测项目最大数量，防止无限添加导致 UI 卡顿
            if (Items.Count >= Common.Validators.InputValidationHelper.MaxInspectItemsCount)
            {
                _ = _notificationService.ShowWarningAsync(
                    $"检测项目最多 {Common.Validators.InputValidationHelper.MaxInspectItemsCount} 项，无法继续添加！",
                    "数量限制");
                _logger.LogWarning("检测项目已达上限 {Max}，拒绝新增", Common.Validators.InputValidationHelper.MaxInspectItemsCount);
                return;
            }

            var newItem = new PlanItemViewModel
            {
                Index = Items.Count + 1,
                PinLeft = string.Empty,
                PinRight = string.Empty,
                CheckMode = CheckModeConstants.Continuity,  // ★ 使用常量
                ModeValue = "OPEN"
            };
            Items.Add(newItem);
            ReindexItems();
            _logger.LogDebug("新增检测项目，当前共 {Count} 项", Items.Count);
        }

        /// <summary>
        /// 删除选中的检测项目
        /// </summary>
        [RelayCommand]
        private void DeleteItem()
        {
            if (SelectedItem == null) return;
            Items.Remove(SelectedItem);
            ReindexItems();
            _logger.LogDebug("删除检测项目，当前共 {Count} 项", Items.Count);
        }

        /// <summary>
        /// 将选中的检测项目上移一位
        /// </summary>
        [RelayCommand]
        private void MoveItemUp()
        {
            if (SelectedItem == null) return;
            var index = Items.IndexOf(SelectedItem);
            if (index > 0)
            {
                Items.Move(index, index - 1);
                ReindexItems();
            }
        }

        /// <summary>
        /// 将选中的检测项目下移一位
        /// </summary>
        [RelayCommand]
        private void MoveItemDown()
        {
            if (SelectedItem == null) return;
            var index = Items.IndexOf(SelectedItem);
            if (index < Items.Count - 1)
            {
                Items.Move(index, index + 1);
                ReindexItems();
            }
        }

        /// <summary>
        /// 重新编排检测项目的序号（从1开始连续）
        /// </summary>
        private void ReindexItems()
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Index = i + 1;
            }
        }

        #endregion

        #region 扫描枪事件处理

        private void OnScannerConnectionStateChanged(object? sender, DeviceConnectionStateChangedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsScannerConnected = e.IsConnected;
                ScannerStatusText = e.StatusText;
            });
        }

        /// <summary>
        /// 扫描枪条码接收回调
        /// 自动填充机种名称（MachineType）
        /// </summary>
        private void OnScannerBarcodeScanned(object? sender, BarcodeParsedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ScannedBarcode = e.RawBarcode;
                _logger.LogInformation("扫描枪收到条码: {Barcode}, 机种={Model}", e.RawBarcode, e.ModelName);

                // 自动填充机种名称（如果当前为空）
                if (string.IsNullOrWhiteSpace(MachineType))
                {
                    MachineType = e.ModelName;
                }
            });
        }

        #endregion

        #region 保存与返回命令

        /// <summary>
        /// 保存方案
        /// 校验必填项 → 构建PlanModel对象 → 调用存储服务保存 → 返回方案列表页
        /// </summary>
        [RelayCommand]
        private async Task SavePlanAsync()
        {
            try
            {
                // 校验必填项
                if (string.IsNullOrWhiteSpace(MachineType))
                {
                    await _notificationService.ShowWarningAsync("请输入/选择机种名称！", "校验失败");
                    return;
                }
                if (string.IsNullOrWhiteSpace(PlanName))
                {
                    await _notificationService.ShowWarningAsync("请输入方案名称！", "校验失败");
                    return;
                }
                if (Items.Count == 0)
                {
                    await _notificationService.ShowWarningAsync("请至少添加一个检测项目！", "校验失败");
                    return;
                }

                // ========== 校验检测项目引脚（逐个检查，精确定位错误项） ==========
                var pinErrors = new List<string>();

                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];
                    int displayIndex = item.Index;

                    // 校验左引脚
                    string? leftError = ValidateSinglePin(item.PinLeft, "左引脚");
                    if (leftError != null)
                        pinErrors.Add($"第{displayIndex}项 {leftError}");

                    // 校验右引脚
                    string? rightError = ValidateSinglePin(item.PinRight, "右引脚");
                    if (rightError != null)
                        pinErrors.Add($"第{displayIndex}项 {rightError}");
                }

                // 如果有任何引脚错误，一次性汇总提示
                if (pinErrors.Count > 0)
                {
                    string errorMessage = "以下检测项目引脚有问题，请修正后再保存：\n\n"
                                        + string.Join("\n", pinErrors);
                    await _notificationService.ShowWarningAsync(errorMessage, "引脚校验失败");
                    return;
                }

                // ========== 校验电阻值阈值（电阻值模式下的 LowerLimit / UpperLimit） ==========
                var resistanceErrors = new List<string>();

                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];

                    // 只校验电阻值模式下的项
                    if (item.CheckMode != CheckModeConstants.Resistance)
                        continue;

                    int displayIndex = item.Index;

                    // 校验下限电阻值范围（NaN / Infinity / 负数 / 过大）
                    string? lowerError = Common.Validators.InputValidationHelper.ValidateResistanceValue(item.LowerLimit);
                    if (lowerError != null)
                        resistanceErrors.Add($"第{displayIndex}项 下限: {lowerError}");

                    // 校验上限电阻值范围
                    string? upperError = Common.Validators.InputValidationHelper.ValidateResistanceValue(item.UpperLimit);
                    if (upperError != null)
                        resistanceErrors.Add($"第{displayIndex}项 上限: {upperError}");

                    // 校验下限 ≤ 上限
                    string? rangeError = Common.Validators.InputValidationHelper.ValidateLimitRange(
                        item.LowerLimit, item.UpperLimit);
                    if (rangeError != null)
                        resistanceErrors.Add($"第{displayIndex}项 {rangeError}");
                }

                // 如果有任何阈值错误，一次性汇总提示
                if (resistanceErrors.Count > 0)
                {
                    string errorMessage = "以下检测项目电阻值设置有误，请修正后再保存：\n\n"
                                        + string.Join("\n", resistanceErrors);
                    await _notificationService.ShowWarningAsync(errorMessage, "阈值校验失败");
                    return;
                }

                // 构建方案对象（CheckMode 直接使用中文值存储到JSON）
                var plan = new PlanModel
                {
                    MachineType = MachineType.Trim(),
                    PlanName = PlanName.Trim(),
                    CreatedTime = _isEditMode ? CreatedTime : DateTime.Now,
                    LastModifiedTime = DateTime.Now,
                    Items = Items.Select(item => new PlanItem
                    {
                        Id = Guid.NewGuid().ToString(),
                        Index = item.Index,
                        ItemName = $"{item.PinLeft}-{item.PinRight}",
                        CheckMode = item.CheckMode,  // ★ 中文 "导通" 或 "电阻值"
                        LowerLimit = item.CheckMode == CheckModeConstants.Resistance ? item.LowerLimit : null,
                        UpperLimit = item.CheckMode == CheckModeConstants.Resistance ? item.UpperLimit : null,
                        ModeValue = item.CheckMode == CheckModeConstants.Resistance
                            ? null                          // 电阻模式：实际值运行时由万用表填充
                            : item.ModeValue                // 导通模式：保存用户选择的 OPEN/SHORT
                    }).ToList()
                };

                // 同时传递原机种名和原方案名，确保方案名变更时也能正确删除旧文件
                await _planStorageService.SavePlanAsync(plan, _originalPlan?.MachineType, _originalPlan?.PlanName);

                _logger.LogInformation("方案保存成功: {MachineType}/{PlanName}", plan.MachineType, plan.PlanName);
                await _notificationService.ShowInfoAsync($"方案 \"{plan.PlanName}\" 保存成功！", "保存成功");

                // 返回方案设定页面
                await _navigationService.NavigateToAsync<PlanSettingView>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存方案失败");
                await _notificationService.ShowErrorAsync($"保存失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 返回方案设定页面
        /// 如有未保存修改，弹窗确认
        /// </summary>
        [RelayCommand]
        private async Task GoBackAsync()
        {
            try
            {
                if (HasUnsavedChanges())
                {
                    var result = await _notificationService.ConfirmAsync(
                        "当前方案有未保存的修改，确定要返回吗？\n未保存的修改将丢失！",
                        "确认返回");
                    if (!result) return;
                }
                _logger.LogInformation("用户返回方案设定页面");
                await _navigationService.NavigateToAsync<PlanSettingView>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "返回方案设定页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 校验单个引脚格式是否合法
        /// </summary>
        private static string? ValidateSinglePin(string pin, string side)
        {
            if (string.IsNullOrWhiteSpace(pin))
                return $"{side}不能为空";

            if (!System.Text.RegularExpressions.Regex.IsMatch(pin.Trim(), @"^[AB]\d+$"))
                return $"{side} \"{pin.Trim()}\" 格式错误：必须以大写A或B开头+数字（如A1、B20）";

            return null;
        }

        /// <summary>
        /// 判断是否有未保存的修改
        /// </summary>
        private bool HasUnsavedChanges()
        {
            if (!string.IsNullOrWhiteSpace(MachineType)) return true;
            if (!string.IsNullOrWhiteSpace(PlanName)) return true;
            if (Items.Count > 0) return true;
            return false;
        }

        #endregion

        #region INavigationAware 实现

        /// <summary>
        /// 页面导航进入时触发
        /// 根据参数判断新增/编辑模式，加载已有方案数据
        /// </summary>
        public Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogDebug("进入方案编辑页面");

            if (parameter is PlanModel existingPlan)
            {
                // 编辑模式：加载已有方案数据
                _isEditMode = true;
                _originalPlan = existingPlan;

                MachineType = existingPlan.MachineType;
                PlanName = existingPlan.PlanName;
                CreatedTime = existingPlan.CreatedTime;
                LastModifiedTime = existingPlan.LastModifiedTime;

                // 加载检测项目
                Items.Clear();
                foreach (var item in existingPlan.Items)
                {
                    var parts = item.ItemName.Split('-');
                    Items.Add(new PlanItemViewModel
                    {
                        Index = item.Index,
                        PinLeft = parts.Length > 0 ? parts[0] : string.Empty,
                        PinRight = parts.Length > 1 ? parts[1] : string.Empty,
                        CheckMode = item.CheckMode,  // 已是标准中文值
                        LowerLimit = item.LowerLimit,
                        UpperLimit = item.UpperLimit,
                        ModeValue = item.ModeValue
                    });
                }

                _logger.LogInformation("编辑模式，加载方案: {MachineType}/{PlanName}",
                    existingPlan.MachineType, existingPlan.PlanName);
            }
            else
            {
                // 新增模式：清空所有字段
                _isEditMode = false;
                _originalPlan = null;

                MachineType = string.Empty;
                PlanName = string.Empty;
                CreatedTime = DateTime.Now;
                LastModifiedTime = DateTime.Now;
                Items.Clear();

                _logger.LogInformation("新增模式");
            }

            // ⭐ 重新订阅扫码事件
            _deviceManager.BarcodeScanned -= OnScannerBarcodeScanned;
            _deviceManager.BarcodeScanned += OnScannerBarcodeScanned;

            // ⭐ 同步扫描枪连接状态
            IsScannerConnected = _deviceManager.IsScannerConnected;
            ScannerStatusText = _deviceManager.ScannerStatusText;

            return Task.CompletedTask;
        }

        /// <summary>
        /// 页面导航离开时触发
        /// </summary>
        public Task OnNavigatedFromAsync()
        {
            _logger.LogDebug("离开方案编辑页面");

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

    /// <summary>
    /// 检测项目 ViewModel（用于方案编辑 DataGrid 行绑定）
    /// 将 PlanItem 的 ItemName 拆分为左右引脚，便于独立编辑
    ///
    /// 改动说明（方案需求变动）：
    /// CheckCondition 字段删除，改为 CheckMode + LowerLimit + UpperLimit + Unit
    /// CheckMode 存储中文 "导通" / "电阻值"
    /// 通过 CheckMode 控制下限/上限/单位输入框的可用状态
    /// </summary>
    public partial class PlanItemViewModel : ObservableObject
    {
        /// <summary>序号（从1开始）</summary>
        [ObservableProperty]
        private int _index;

        /// <summary>左引脚（如 A1）</summary>
        [ObservableProperty]
        private string _pinLeft = string.Empty;

        /// <summary>右引脚（如 A2）</summary>
        [ObservableProperty]
        private string _pinRight = string.Empty;

        /// <summary>
        /// 检测方式（中文存储）
        /// "导通" — 导通检测
        /// "电阻值" — 电阻值检测
        /// </summary>
        [ObservableProperty]
        private string _checkMode = CheckModeConstants.Continuity;

        /// <summary>
        /// 电阻值下限（Ω），导通模式为 null
        /// </summary>
        [ObservableProperty]
        private double? _lowerLimit;

        /// <summary>
        /// 电阻值上限（Ω），导通模式为 null
        /// </summary>
        [ObservableProperty]
        private double? _upperLimit;

        /// <summary>
        /// 模式值（替代旧字段 Unit）
        /// 导通模式： "OPEN"（期望开路）或 "SHORT"（期望短路）
        /// 电阻值模式： null（实际值在运行时由万用表填充）
        /// 界面绑定到"下限"列的条件渲染控件
        /// </summary>
        [ObservableProperty]
        private string? _modeValue = "OPEN";

        /// <summary>
        /// 检测方式下拉选项（中文）
        /// 绑定到 DataGrid ComboBox.ItemsSource
        /// </summary>
        public ObservableCollection<string> CheckModeOptions { get; } = new ObservableCollection<string>
        {
            CheckModeConstants.Continuity,
            CheckModeConstants.Resistance
        };

        /// <summary>
        /// 是否为电阻值检测模式
        /// 用于控制下限/上限输入框的 IsEnabled 状态
        /// </summary>
        public bool IsResistanceMode => CheckMode == CheckModeConstants.Resistance;

        /// <summary>
        /// 完整的检测项目名称（如 A1-A2）
        /// </summary>
        public string FullItemName => $"{PinLeft}-{PinRight}";

        /// <summary>
        /// 是否为导通检测模式
        /// 用于"下限"列条件渲染：导通 → ComboBox，电阻值 → TextBox
        /// </summary>
        public bool IsContinuityMode => CheckMode == CheckModeConstants.Continuity;

        /// <summary>
        /// 检查方式变更时联动
        /// 切换到电阻模式：清空 ModeValue（运行时由万用表填充），保留上下限作为阈值配置
        /// 切换到导通模式：清空上下限（导通模式下无阈值），ModeValue 初始化为 "OPEN"
        /// </summary>
        partial void OnCheckModeChanged(string value)
        {
            if (value == CheckModeConstants.Resistance)
            {
                // 电阻值模式：ModeValue 由运行时测量值填充，编辑期置为 null
                ModeValue = null;
            }
            else // 导通
            {
                // 导通模式：清空电阻值阈值
                LowerLimit = null;
                UpperLimit = null;
                // ModeValue 恢复为合法导通值
                if (ModeValue != "OPEN" && ModeValue != "SHORT")
                {
                    ModeValue = "OPEN";
                }
            }
            // 通知条件渲染绑定属性已变更
            OnPropertyChanged(nameof(IsResistanceMode));
            OnPropertyChanged(nameof(IsContinuityMode));
        }
    }
}