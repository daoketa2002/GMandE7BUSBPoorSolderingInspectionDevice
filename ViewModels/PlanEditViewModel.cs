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
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
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
using System.Windows.Threading;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 程序编辑页导航参数，用于明确区分编辑已有程序和复制程序。
    /// </summary>
    public sealed class PlanEditNavigationParameter
    {
        public PlanEditNavigationParameter(PlanModel plan, bool isCopyMode)
        {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            IsCopyMode = isCopyMode;
        }

        public PlanModel Plan { get; }

        public bool IsCopyMode { get; }
    }

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
        /// 是否为编辑模式（true=编辑已有程序，false=新增或复制程序）
        /// </summary>
        private bool _isEditMode = false;

        /// <summary>
        /// 是否为复制模式。复制模式必须按新增语义保存，不能触发源程序文件移动。
        /// </summary>
        private bool _isCopyMode;

        /// <summary>
        /// 复制模式下的源程序身份，用于保存前防止覆盖源程序。
        /// </summary>
        private PlanModel? _copySourcePlan;

        /// <summary>
        /// 编辑模式下的原始程序（用于处理系列、机种或程序名称变更时的文件移动）
        /// </summary>
        private PlanModel? _originalPlan;
        private bool _scannerEventsSubscribed;

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

            // 初始化下拉选项
            MachineTypeOptions = new ObservableCollection<string>(PlanStorageService.DefaultMachineTypes);
            SeriesOptions = new ObservableCollection<string>();
            WorkstationOptions = new ObservableCollection<string>
            {
                WorkstationConstants.Left,
                WorkstationConstants.Right
            };
            PinOptions = new ObservableCollection<string>(PlanStorageService.PinList);
            // ★ CheckMode 下拉选项使用中文（不再需要单独的 CheckModeOptions，已内聚到 PlanItemViewModel）
            ContinuityUnitOptions = new ObservableCollection<string> { "OPEN", "SHORT" };
            PinPolarityOptions = new ObservableCollection<string>
            {
                PinPolarityConstants.Positive,
                PinPolarityConstants.Negative
            };

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

        /// <summary>方案归属系列，允许手工输入合法值。</summary>
        [ObservableProperty]
        private string _series = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLeftWorkstation))]
        [NotifyPropertyChangedFor(nameof(IsRightWorkstation))]
        private string _workstation = WorkstationConstants.Left;

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
        /// 页面标题，根据新增、编辑、复制模式动态显示。
        /// </summary>
        [ObservableProperty]
        private string _pageTitle = "新增程序";

        /// <summary>
        /// 机种下拉选项列表
        /// </summary>
        public ObservableCollection<string> MachineTypeOptions { get; }
        public ObservableCollection<string> SeriesOptions { get; }
        public ObservableCollection<string> WorkstationOptions { get; }
        public bool IsLeftWorkstation => Workstation == WorkstationConstants.Left;
        public bool IsRightWorkstation => Workstation == WorkstationConstants.Right;

        [RelayCommand]
        private void SelectLeftWorkstation() => Workstation = WorkstationConstants.Left;

        [RelayCommand]
        private void SelectRightWorkstation() => Workstation = WorkstationConstants.Right;

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

        /// <summary>
        /// 引脚极性下拉选项。
        /// 方案编辑页使用中文显示和保存，后续传 PLC 时再转换为 1/0 编码。
        /// </summary>
        public ObservableCollection<string> PinPolarityOptions { get; }

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
                PinLeftPolarity = PinPolarityConstants.Positive,
                PinRightPolarity = PinPolarityConstants.Negative,
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
            void ApplyState()
            {
                try
                {
                    if (!_scannerEventsSubscribed)
                    {
                        _logger.LogDebug("[扫码UI][忽略] 方案编辑页已离开，忽略扫描枪状态");
                        return;
                    }

                    IsScannerConnected = e.IsConnected;
                    ScannerStatusText = e.StatusText;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[扫码UI][异常] 方案编辑页应用扫描枪状态失败");
                }
            }

            BeginInvokeScannerUi(ApplyState, "扫描枪状态");
        }

        /// <summary>
        /// 扫描枪条码接收回调
        /// 自动填充机种名称（MachineType）
        /// </summary>
        private void OnScannerBarcodeScanned(object? sender, BarcodeParsedEventArgs e)
        {
            void ApplyBarcode()
            {
                try
                {
                    if (!_scannerEventsSubscribed)
                    {
                        _logger.LogDebug("[扫码UI][忽略] 方案编辑页已离开，丢弃排队中的旧扫码");
                        return;
                    }

                    ScannedBarcode = e.RawBarcode;
                    _logger.LogInformation(
                        "[扫码UI] 方案编辑页已应用条码, Model={Model}, Serial={Serial}",
                        e.ModelName,
                        e.SerialPart);

                    // 自动填充机种名称（如果当前为空）
                    if (string.IsNullOrWhiteSpace(MachineType))
                    {
                        MachineType = e.ModelName;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[扫码UI][异常] 方案编辑页应用条码失败");
                }
            }

            BeginInvokeScannerUi(ApplyBarcode, "条码");
        }

        private void BeginInvokeScannerUi(Action action, string operationName)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null
                || dispatcher.HasShutdownStarted
                || dispatcher.HasShutdownFinished)
            {
                _logger.LogWarning("[扫码UI][忽略] Dispatcher 不可用: Operation={Operation}", operationName);
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }

        #endregion

        #region 保存与返回命令

        /// <summary>
        /// 保存程序
        /// 校验必填项 → 构建PlanModel对象 → 调用存储服务保存 → 返回程序列表页
        /// </summary>
        [RelayCommand]
        private async Task SavePlanAsync()
        {
            try
            {
                // 校验必填项
                if (string.IsNullOrWhiteSpace(Series))
                {
                    await _notificationService.ShowWarningAsync("请输入/选择系列名称！", "校验失败");
                    return;
                }
                if (string.IsNullOrWhiteSpace(MachineType))
                {
                    await _notificationService.ShowWarningAsync("请输入/选择机种名称！", "校验失败");
                    return;
                }
                if (string.IsNullOrWhiteSpace(PlanName))
                {
                    await _notificationService.ShowWarningAsync("请输入程序名称！", "校验失败");
                    return;
                }

                var seriesError = NameValidationHelper.ValidateSeriesName(Series);
                if (seriesError != null)
                {
                    _logger.LogWarning("[用户操作] 程序保存被拒绝：系列名称不合法 Series={Series} Error={Error}", Series, seriesError);
                    await _notificationService.ShowWarningAsync(seriesError, "校验失败");
                    return;
                }

                if (!WorkstationConstants.IsValid(Workstation))
                {
                    await _notificationService.ShowWarningAsync("请选择左工位或右工位！", "校验失败");
                    return;
                }

                var machineTypeError = NameValidationHelper.ValidateMachineType(MachineType);
                if (machineTypeError != null)
                {
                    _logger.LogWarning("[用户操作] 程序保存被拒绝：机种名称不合法 MachineType={MachineType} Error={Error}", MachineType, machineTypeError);
                    await _notificationService.ShowWarningAsync(machineTypeError, "校验失败");
                    return;
                }

                var planNameError = NameValidationHelper.ValidatePlanName(PlanName);
                if (planNameError != null)
                {
                    _logger.LogWarning("[用户操作] 程序保存被拒绝：程序名称不合法 PlanName={PlanName} Error={Error}", PlanName, planNameError);
                    await _notificationService.ShowWarningAsync(ToProgramTerminology(planNameError), "校验失败");
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

                    // 保存前统一标准化，确保大小写和首尾空格不会进入方案文件。
                    item.PinLeft = NormalizePinName(item.PinLeft);
                    item.PinRight = NormalizePinName(item.PinRight);

                    // 校验左引脚
                    string? leftError = ValidateSinglePin(item.PinLeft, "左引脚");
                    if (leftError != null)
                        pinErrors.Add($"第{displayIndex}项 {leftError}");

                    // 校验右引脚
                    string? rightError = ValidateSinglePin(item.PinRight, "右引脚");
                    if (rightError != null)
                        pinErrors.Add($"第{displayIndex}项 {rightError}");

                    // 左右引脚必须不同，否则会形成无意义的自测点，后续 PLC 切换也难以判断接线意图。
                    if (leftError == null
                        && rightError == null
                        && string.Equals(item.PinLeft.Trim(), item.PinRight.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        pinErrors.Add($"第{displayIndex}项 左右引脚不能相同，请选择两个不同引脚");
                    }
                }

                // 如果有任何引脚错误，一次性汇总提示
                if (pinErrors.Count > 0)
                {
                    string errorMessage = "以下检测项目引脚有问题，请修正后再保存：\n\n"
                                        + string.Join("\n", pinErrors);
                    await _notificationService.ShowWarningAsync(errorMessage, "引脚校验失败");
                    _logger.LogWarning("程序保存被拒绝：存在引脚配置错误，数量={Count}", pinErrors.Count);
                    return;
                }

                // ========== 校验左右引脚极性：必须一正一负，避免后续 PLC 接线方向不明确 ==========
                var polarityErrors = new List<string>();

                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];
                    item.PinLeftPolarity = PinPolarityConstants.Normalize(
                        item.PinLeftPolarity, PinPolarityConstants.Positive);
                    item.PinRightPolarity = PinPolarityConstants.Normalize(
                        item.PinRightPolarity, PinPolarityConstants.Negative);

                    if (item.PinLeftPolarity == item.PinRightPolarity)
                    {
                        polarityErrors.Add($"第{item.Index}项 左右引脚极性不能相同，请设置为一正一负");
                    }
                }

                if (polarityErrors.Count > 0)
                {
                    string errorMessage = "以下检测项目极性设置有误，请修正后再保存：\n\n"
                                        + string.Join("\n", polarityErrors);
                    await _notificationService.ShowWarningAsync(errorMessage, "极性校验失败");
                    _logger.LogWarning("程序保存被拒绝：存在左右极性相同的检测项目，数量={Count}", polarityErrors.Count);
                    return;
                }

                // ========== 校验检测方式、导通期望值和电阻值阈值 ==========
                var resistanceErrors = new List<string>();

                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];

                    if (item.CheckMode != CheckModeConstants.Continuity
                        && item.CheckMode != CheckModeConstants.Resistance)
                    {
                        resistanceErrors.Add($"第{item.Index}项 检测方式只能是“导通”或“电阻值”");
                        continue;
                    }

                    if (item.CheckMode == CheckModeConstants.Continuity)
                    {
                        item.ModeValue = item.ModeValue?.Trim().ToUpperInvariant();
                        if (item.ModeValue != "OPEN" && item.ModeValue != "SHORT")
                            resistanceErrors.Add($"第{item.Index}项 导通期望值只能是 OPEN 或 SHORT");

                        continue;
                    }

                    int displayIndex = item.Index;

                    if (!item.LowerLimit.HasValue)
                        resistanceErrors.Add($"第{displayIndex}项 下限不能为空");
                    if (!item.UpperLimit.HasValue)
                        resistanceErrors.Add($"第{displayIndex}项 上限不能为空");

                    // 校验下限电阻值范围（NaN / Infinity / 负数 / 过大）
                    string? lowerError = InputValidationHelper.ValidateResistanceValue(item.LowerLimit);
                    if (lowerError != null)
                        resistanceErrors.Add($"第{displayIndex}项 下限: {lowerError}");

                    // 校验上限电阻值范围
                    string? upperError = InputValidationHelper.ValidateResistanceValue(item.UpperLimit);
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
                    _logger.LogWarning("方案保存被拒绝：存在不符合万用表量程分辨率的电阻阈值，数量={Count}", resistanceErrors.Count);
                    return;
                }

                // 构建方案对象（CheckMode 直接使用中文值存储到JSON）
                var plan = new PlanModel
                {
                    Series = Series.Trim(),
                    MachineType = MachineType.Trim(),
                    PlanName = PlanName.Trim(),
                    Workstation = Workstation,
                    Version = _isEditMode ? Math.Max(1, _originalPlan?.Version ?? 1) : 1,
                    CreatedTime = _isEditMode ? CreatedTime : DateTime.Now,
                    LastModifiedTime = DateTime.Now,
                    Items = Items.Select(item => new PlanItem
                    {
                        Id = Guid.NewGuid().ToString(),
                        Index = item.Index,
                        ItemName = $"{item.PinLeft}-{item.PinRight}",
                        PinLeftPolarity = item.PinLeftPolarity,
                        PinRightPolarity = item.PinRightPolarity,
                        CheckMode = item.CheckMode,  // ★ 中文 "导通" 或 "电阻值"
                        LowerLimit = item.CheckMode == CheckModeConstants.Resistance
                            ? item.LowerLimit
                            : null,
                        UpperLimit = item.CheckMode == CheckModeConstants.Resistance
                            ? item.UpperLimit
                            : null,
                        ModeValue = item.CheckMode == CheckModeConstants.Resistance
                            ? null                          // 电阻模式：实际值运行时由万用表填充
                            : item.ModeValue                // 导通模式：保存用户选择的 OPEN/SHORT
                    }).ToList()
                };

                // 复制必须形成新的程序身份，工位变化不参与文件路径，不能因此绕过源程序覆盖保护。
                if (_isCopyMode
                    && _copySourcePlan != null
                    && IsSamePlanIdentity(_copySourcePlan, plan))
                {
                    _logger.LogWarning(
                        "[安全审计] 复制程序被拒绝覆盖源程序：{Series}/{MachineType}/{PlanName}",
                        plan.Series,
                        plan.MachineType,
                        plan.PlanName);
                    await _notificationService.ShowWarningAsync(
                        "复制程序必须修改系列、机种或程序名称后再保存。",
                        "复制程序校验失败");
                    return;
                }

                bool allowDuplicatePlanName = false;
                var sameNamePlans = await FindSamePlanNameConflictsAsync(plan);
                if (sameNamePlans.Count > 0)
                {
                    string conflictDetails = string.Join(
                        "\n",
                        sameNamePlans.Select((existingPlan, index) =>
                            $"{index + 1}. 系列“{existingPlan.Series}” / 机种“{existingPlan.MachineType}” / 程序“{existingPlan.PlanName}”"));
                    var targetConflict = sameNamePlans.FirstOrDefault(existingPlan =>
                        IsSamePlanIdentity(existingPlan, plan));

                    string conflictMessage = targetConflict != null
                        ? $"程序名称“{plan.PlanName}”已存在以下程序：\n\n{conflictDetails}\n\n"
                          + "当前保存目标与已有程序路径相同，继续保存将覆盖该程序。\n是否继续？"
                        : $"程序名称“{plan.PlanName}”已存在以下程序：\n\n{conflictDetails}\n\n"
                          + "确认后允许使用同名程序名称，是否继续？";

                    _logger.LogWarning(
                        "[程序保存][同名提示] 目标={Series}/{MachineType}/{PlanName}, 冲突数量={Count}, 目标路径冲突={TargetConflict}",
                        plan.Series,
                        plan.MachineType,
                        plan.PlanName,
                        sameNamePlans.Count,
                        targetConflict != null);

                    if (!await _notificationService.ConfirmAsync(conflictMessage, "程序名称重复确认"))
                    {
                        _logger.LogWarning(
                            "[程序保存][同名提示] 用户取消：目标={Series}/{MachineType}/{PlanName}",
                            plan.Series,
                            plan.MachineType,
                            plan.PlanName);
                        return;
                    }

                    allowDuplicatePlanName = true;
                    _logger.LogWarning(
                        "[程序保存][同名提示] 用户确认继续：目标={Series}/{MachineType}/{PlanName}",
                        plan.Series,
                        plan.MachineType,
                        plan.PlanName);
                }

                if (_isEditMode && _originalPlan != null)
                {
                    var contentChanged = HasPlanBusinessContentChanged(_originalPlan, plan);
                    if (contentChanged)
                    {
                        var oldVersion = Math.Max(1, _originalPlan.Version);
                        var newVersion = oldVersion + 1;
                        var confirmed = await _notificationService.ConfirmAsync(
                            $"当前程序内容已发生变化。\n\n保存后，程序版本将由 V{oldVersion} 更新为 V{newVersion}。\n后续检测将使用新的检测条件。\n历史测试记录不会被修改。\n\n是否继续保存？",
                            "程序版本更新确认");

                        if (!confirmed)
                        {
                            _logger.LogWarning("[程序版本] 用户取消版本更新保存：{MachineType}/{PlanName}", plan.MachineType, plan.PlanName);
                            return;
                        }

                        plan.Version = newVersion;
                        _logger.LogWarning("[程序版本] 程序内容变化：V{OldVersion} -> V{NewVersion}", oldVersion, newVersion);
                    }
                    else
                    {
                        _logger.LogWarning("[程序版本] 内容未变化，版本保持 V{Version}", plan.Version);
                    }
                }
                else
                {
                    _logger.LogWarning("[程序版本] 新程序创建：Version=V1");
                }

                // 同时传递原机种名和原方案名，确保方案名变更时也能正确删除旧文件
                await _planStorageService.SavePlanAsync(
                    plan,
                    _originalPlan?.Series,
                    _originalPlan?.MachineType,
                    _originalPlan?.PlanName,
                    allowDuplicatePlanName);

                _logger.LogInformation("程序保存成功: {MachineType}/{PlanName}", plan.MachineType, plan.PlanName);
                await _notificationService.ShowInfoAsync($"程序 \"{plan.PlanName}\" 保存成功！", "保存成功");

                // 返回程序设定页面
                await _navigationService.NavigateToAsync<PlanSettingView>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存程序失败");
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
                        "当前程序有未保存的修改，确定要返回吗？\n未保存的修改将丢失！",
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

            if (!InputValidationHelper.IsValidPinName(pin))
                return $"{side} \"{pin.Trim()}\" 格式错误：仅允许 A1～A12、B1～B12";

            return null;
        }

        /// <summary>
        /// 标准化方案编辑页的引脚输入，非法内容保留其文本供保存校验提示。
        /// </summary>
        private static string NormalizePinName(string? pinName)
            => pinName?.Trim().ToUpperInvariant() ?? string.Empty;

        /// <summary>
        /// 将旧校验器返回的内部方案术语转换为当前页面的客户术语。
        /// </summary>
        private static string ToProgramTerminology(string message)
        {
            return message.Replace("方案名称", "程序名称", StringComparison.Ordinal);
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

        private static bool HasPlanBusinessContentChanged(PlanModel originalPlan, PlanModel currentPlan)
        {
            var originalItems = originalPlan.Items.OrderBy(i => i.Index).ToList();
            var currentItems = currentPlan.Items.OrderBy(i => i.Index).ToList();

            if (originalItems.Count != currentItems.Count)
                return true;

            for (int i = 0; i < originalItems.Count; i++)
            {
                if (!IsSameBusinessItem(originalItems[i], currentItems[i]))
                    return true;
            }

            return false;
        }

        /// <summary>查找同名方案，编辑当前方案自身时不视为冲突。</summary>
        private async Task<List<PlanModel>> FindSamePlanNameConflictsAsync(PlanModel plan)
        {
            var allPlans = await _planStorageService.LoadAllPlansAsync();
            return allPlans
                .Where(existingPlan =>
                    string.Equals(
                        existingPlan.PlanName.Trim(),
                        plan.PlanName.Trim(),
                        StringComparison.OrdinalIgnoreCase)
                    && !IsCurrentEditedPlan(existingPlan))
                .OrderBy(existingPlan => existingPlan.Series, StringComparer.OrdinalIgnoreCase)
                .ThenBy(existingPlan => existingPlan.MachineType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(existingPlan => existingPlan.PlanName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>编辑时排除原方案自身，避免保存未改名方案时重复提示自己。</summary>
        private bool IsCurrentEditedPlan(PlanModel candidate)
        {
            return _isEditMode
                && _originalPlan != null
                && IsSamePlanIdentity(candidate, _originalPlan);
        }

        /// <summary>方案文件的完整业务身份，不包含工位，因为工位不参与文件路径。</summary>
        private static bool IsSamePlanIdentity(PlanModel left, PlanModel right)
        {
            return string.Equals(left.Series.Trim(), right.Series.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.MachineType.Trim(), right.MachineType.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.PlanName.Trim(), right.PlanName.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameBusinessItem(PlanItem left, PlanItem right)
        {
            return left.Index == right.Index
                && string.Equals(left.ItemName, right.ItemName, StringComparison.Ordinal)
                && string.Equals(left.PinLeftPolarity, right.PinLeftPolarity, StringComparison.Ordinal)
                && string.Equals(left.PinRightPolarity, right.PinRightPolarity, StringComparison.Ordinal)
                && string.Equals(left.CheckMode, right.CheckMode, StringComparison.Ordinal)
                && Nullable.Equals(left.LowerLimit, right.LowerLimit)
                && Nullable.Equals(left.UpperLimit, right.UpperLimit)
                && string.Equals(left.ModeValue, right.ModeValue, StringComparison.Ordinal);
        }

        /// <summary>
        /// 将程序模型重新映射到编辑器项目，确保编辑或复制时不共享可修改的项目对象。
        /// </summary>
        private void LoadPlanToEditor(PlanModel sourcePlan)
        {
            Series = sourcePlan.Series;
            MachineType = sourcePlan.MachineType;
            PlanName = sourcePlan.PlanName;
            Workstation = WorkstationConstants.IsValid(sourcePlan.Workstation)
                ? sourcePlan.Workstation
                : WorkstationConstants.Left;
            CreatedTime = sourcePlan.CreatedTime;
            LastModifiedTime = sourcePlan.LastModifiedTime;

            Items.Clear();
            foreach (var item in sourcePlan.Items ?? new List<PlanItem>())
            {
                var parts = item.ItemName.Split('-');
                Items.Add(new PlanItemViewModel
                {
                    Index = item.Index,
                    PinLeft = parts.Length > 0 ? parts[0] : string.Empty,
                    PinRight = parts.Length > 1 ? parts[1] : string.Empty,
                    // 旧程序没有极性字段时，默认按左正右负补齐，避免打开历史程序后逐项手工设置。
                    PinLeftPolarity = PinPolarityConstants.Normalize(
                        item.PinLeftPolarity, PinPolarityConstants.Positive),
                    PinRightPolarity = PinPolarityConstants.Normalize(
                        item.PinRightPolarity, PinPolarityConstants.Negative),
                    CheckMode = item.CheckMode,
                    LowerLimit = item.LowerLimit,
                    UpperLimit = item.UpperLimit,
                    ModeValue = item.ModeValue
                });
            }
        }

        /// <summary>
        /// 生成可直接通过现有程序名称校验的复制名称。
        /// 半角下划线被现有命名协议禁止，因此使用全角括号标识副本。
        /// </summary>
        private static string BuildCopyPlanName(string sourcePlanName)
        {
            const string copySuffix = "（副本）";
            var trimmedSourceName = sourcePlanName.Trim();
            var maxBaseLength = Math.Max(1, InputValidationHelper.MaxPlanNameLength - copySuffix.Length);

            if (trimmedSourceName.Length > maxBaseLength)
                trimmedSourceName = trimmedSourceName[..maxBaseLength];

            return trimmedSourceName + copySuffix;
        }

        #endregion

        #region INavigationAware 实现

        /// <summary>
        /// 页面导航进入时触发
        /// 根据参数判断新增、编辑或复制模式，加载已有程序数据
        /// </summary>
        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogDebug("进入程序编辑页面");

            var allPlans = await _planStorageService.LoadAllPlansAsync();
            SeriesOptions.Clear();
            foreach (var series in allPlans
                .Select(plan => plan.Series.Trim())
                .Where(series => !string.IsNullOrWhiteSpace(series))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(series => series, StringComparer.OrdinalIgnoreCase))
            {
                SeriesOptions.Add(series);
            }

            var machineTypes = await _planStorageService.GetAllMachineTypesAsync();
            MachineTypeOptions.Clear();
            foreach (var machineType in machineTypes)
                MachineTypeOptions.Add(machineType);

            if (parameter is PlanEditNavigationParameter navigationParameter)
            {
                var existingPlan = navigationParameter.Plan;
                LoadPlanToEditor(existingPlan);

                if (navigationParameter.IsCopyMode)
                {
                    // 复制模式按新增语义保存，源程序只保留身份用于防覆盖校验。
                    _isEditMode = false;
                    _isCopyMode = true;
                    _originalPlan = null;
                    _copySourcePlan = existingPlan;
                    PageTitle = "复制程序";
                    PlanName = BuildCopyPlanName(existingPlan.PlanName);
                    CreatedTime = DateTime.Now;
                    LastModifiedTime = DateTime.Now;

                    _logger.LogInformation("复制模式，加载源程序: {MachineType}/{PlanName}",
                        existingPlan.MachineType, existingPlan.PlanName);
                }
                else
                {
                    // 编辑模式保留原程序身份，供存储层执行原有重命名/移动逻辑。
                    _isEditMode = true;
                    _isCopyMode = false;
                    _copySourcePlan = null;
                    _originalPlan = existingPlan;
                    PageTitle = "编辑程序";

                    _logger.LogInformation("编辑模式，加载程序: {MachineType}/{PlanName}",
                        existingPlan.MachineType, existingPlan.PlanName);
                }
            }
            else if (parameter is PlanModel existingPlan)
            {
                // 兼容既有导航调用：PlanModel 参数仍表示编辑模式。
                _isEditMode = true;
                _isCopyMode = false;
                _copySourcePlan = null;
                _originalPlan = existingPlan;
                PageTitle = "编辑程序";
                LoadPlanToEditor(existingPlan);

                _logger.LogInformation("编辑模式，加载程序: {MachineType}/{PlanName}",
                    existingPlan.MachineType, existingPlan.PlanName);
            }
            else
            {
                // 新增模式：清空所有字段，并清除复制源身份。
                _isEditMode = false;
                _isCopyMode = false;
                _copySourcePlan = null;
                _originalPlan = null;
                PageTitle = "新增程序";

                MachineType = string.Empty;
                Series = string.Empty;
                PlanName = string.Empty;
                Workstation = WorkstationConstants.Left;
                CreatedTime = DateTime.Now;
                LastModifiedTime = DateTime.Now;
                Items.Clear();

                _logger.LogInformation("新增程序模式");
            }

            // 页面级事件只在页面活跃期间订阅。
            _deviceManager.ScannerConnectionStateChanged -= OnScannerConnectionStateChanged;
            _deviceManager.ScannerConnectionStateChanged += OnScannerConnectionStateChanged;
            _deviceManager.BarcodeScanned -= OnScannerBarcodeScanned;
            _deviceManager.BarcodeScanned += OnScannerBarcodeScanned;
            _scannerEventsSubscribed = true;

            // ⭐ 同步扫描枪连接状态
            IsScannerConnected = _deviceManager.IsScannerConnected;
            ScannerStatusText = _deviceManager.ScannerStatusText;

            return;
        }

        /// <summary>
        /// 页面导航离开时触发
        /// </summary>
        public Task OnNavigatedFromAsync()
        {
            _logger.LogDebug("离开方案编辑页面");

            if (_deviceManager != null)
            {
                _deviceManager.ScannerConnectionStateChanged -= OnScannerConnectionStateChanged;
                _deviceManager.BarcodeScanned -= OnScannerBarcodeScanned;
            }
            _scannerEventsSubscribed = false;

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
        /// 左引脚极性。只在方案编辑页展示，保存后供检测配置和后续 PLC 写入使用。
        /// </summary>
        [ObservableProperty]
        private string _pinLeftPolarity = PinPolarityConstants.Positive;

        /// <summary>
        /// 右引脚极性。只在方案编辑页展示，保存后供检测配置和后续 PLC 写入使用。
        /// </summary>
        [ObservableProperty]
        private string _pinRightPolarity = PinPolarityConstants.Negative;

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
        /// 下限输入框智能提醒：按当前 Ω 数值自动说明预计量程和分辨率。
        /// </summary>
        public string LowerLimitHintText => IsResistanceMode
            ? InputValidationHelper.GetResistanceInputHint(LowerLimit)
            : string.Empty;

        /// <summary>
        /// 上限输入框智能提醒：按当前 Ω 数值自动说明预计量程和分辨率。
        /// </summary>
        public string UpperLimitHintText => IsResistanceMode
            ? InputValidationHelper.GetResistanceInputHint(UpperLimit)
            : string.Empty;

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
            OnPropertyChanged(nameof(LowerLimitHintText));
            OnPropertyChanged(nameof(UpperLimitHintText));
        }

        partial void OnLowerLimitChanged(double? value)
        {
            // 输入过程中只刷新提示，最终是否符合分辨率由保存校验统一拒绝。
            OnPropertyChanged(nameof(LowerLimitHintText));
        }

        partial void OnUpperLimitChanged(double? value)
        {
            // 输入过程中只刷新提示，最终是否符合分辨率由保存校验统一拒绝。
            OnPropertyChanged(nameof(UpperLimitHintText));
        }
    }
}
