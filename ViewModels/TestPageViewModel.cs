// ============================================================
// 文件: ViewModels/TestPageViewModel.cs
// 描述: 运行界面 ViewModel
// 重构要点：
//   1. 去掉传感器模拟（_sensorSimTimer），改为 PLC 轮询
//   2. 新增 8 种 UI 状态，完整映射检测引擎内部状态
//   3. 通过 PLC 轮询接收 DT120=1 实体启动信号
//   4. 启动前复核所有条件，失败时写 Warning 日志并提示操作员
//   5. 保存记录使用当前 ModelName + SchemeName 精确匹配
//   6. 急停弹窗锁定逻辑
// ============================================================

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

/// <summary>
/// 运行界面 UI 状态枚举（8种，直接服务运行页"测试状态"大面板显示）
/// Ready:        待机中 — 信息不全或设备未就绪
/// CanStart:     可启动 — 人工启动条件满足
/// Testing:      测试中 — 检测引擎运行中
/// Paused:       已停止 — DT122 停止，保留断点
/// EmergencyStop:急停中 — DT123 急停，必须复位
/// Resetting:    复位中 — DT121 复位处理中
/// PendingSave:  待保存 — 检测完成等待保存
/// Error:        异常 — 板离或不可继续错误
/// </summary>
public enum TestUIState
{
    Ready,
    CanStart,
    Testing,
    Paused,
    EmergencyStop,
    Resetting,
    PendingSave,
    Error
}

public partial class TestPageViewModel : ObservableObject, INavigationAware, IDisposable
{
    #region 服务注入

    private readonly INavigationService _navigationService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<TestPageViewModel> _logger;
    private readonly IPlanStorageService _planStorageService;
    private readonly IOperatorStateService _operatorStateService;
    private readonly IDeviceSettingsService _settingsService;
    private readonly ITestRecordStorage _testRecordStorage;

    // 硬件服务

    private readonly IDeviceConnectionManager _deviceManager;
    private readonly IPlcDevice _plcDevice;
    private readonly InspectionEngine? _inspectionEngine;

    #endregion

    #region 字段

    private DispatcherTimer? _clockTimer;

    /// <summary>PLC 状态轮询定时器（取代旧传感器模拟）</summary>
    private DispatcherTimer? _plcPollingTimer;

    /// <summary>轮询中上一次 PLC 输入快照，用于检测信号变化</summary>
    private PlcMachineInputs? _lastPlcInputs;

    /// <summary>是否已向引擎发起检测（防止 DT120=1 重复触发多次 RunInspectionAsync）</summary>
    private bool _inspectionStarted;

    /// <summary>启动失败后是否已清除 DT120（防止重复触发失败日志刷屏）</summary>
    private bool _startupCleared;

    /// <summary>是否正在显示保存对话框（防止重复弹窗）</summary>
    private bool _isShowingSaveDialog;

    /// <summary>是否正在显示急停对话框（防止轮询重复弹窗）</summary>
    private bool _isShowingEmergencyDialog;

    /// <summary>本轮 DT123 急停弹窗是否已由操作员解除确认。DT123 恢复 0 后重置。</summary>
    private bool _emergencyDialogAcknowledged;

    /// <summary>DT122 停止信号是否已处理，防止轮询期间重复刷日志。</summary>
    private bool _stopSignalHandled;

    /// <summary>DT121 复位信号是否已处理，防止复位按钮保持时重复处理。</summary>
    private bool _resetSignalHandled;

    /// <summary>复位或中止后，忽略上一轮检测流程晚到的 StepStarted/StepCompleted/Completed 回调。</summary>
    private bool _ignoreInspectionCallbacksUntilNextStart;

    /// <summary>
    /// 正在执行复位流程中。在 OnStepStarted/OnStepCompleted/OnInspectionCompleted 中额外守卫，
    /// 防止引擎自然完成后的 InvokeAsync 回调（Normal 优先级）在计时器轮询（Background 优先级）之前执行。
    /// </summary>
    private bool _isResetting;

    /// <summary>急停弹窗 ViewModel 引用。弹窗打开期间非 null，轮询时推送 DT303 状态。</summary>
    private ViewModels.EmergencyStopDialogViewModel? _emergencyStopDialogVM;

    #endregion

    #region 构造函数

    public TestPageViewModel(
        INavigationService navigationService,
        INotificationService notificationService,
        IOperatorStateService operatorStateService,
        ILogger<TestPageViewModel> logger,

        IDeviceSettingsService settingsService,
        IPlanStorageService planStorageService,
        IDeviceConnectionManager deviceManager,
        IScannerBarcodeService scannerBarcodeService,
        ITestRecordStorage testRecordStorage,
        IPlcDevice plcDevice,
        InspectionEngine? inspectionEngine = null)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _operatorStateService = operatorStateService ?? throw new ArgumentNullException(nameof(operatorStateService));
        _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));

        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _testRecordStorage = testRecordStorage ?? throw new ArgumentNullException(nameof(testRecordStorage));
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _inspectionEngine = inspectionEngine;

        // 初始化 UiState 为待机
        UiState = TestUIState.Ready;

        InitializeClock();
        SubscribeToHardwareEvents();

        // 启动时同步设备连接状态
        SyncDeviceStates();
    }

    #endregion

    #region 顶部左侧 - 生产统计面板

    [ObservableProperty]
    private string _schemeName = string.Empty;

    public ObservableCollection<string> PlanNameOptions { get; } = new();

    [ObservableProperty]
    private string? _selectedPlanName;

    [ObservableProperty]
    private bool _isSchemeNameInvalid;

    partial void OnSchemeNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SelectedPlanName = null;
            IsSchemeNameInvalid = false;
            TestItems.Clear();
            AddLog("方案名称已清空，检测项目列表已清空");
            return;
        }

        string? matched = PlanNameOptions.FirstOrDefault(
            planName => string.Equals(planName, value, StringComparison.OrdinalIgnoreCase));

        if (matched != null)
        {
            SelectedPlanName = matched;
            IsSchemeNameInvalid = false;
            _ = LoadPlanItemsAsync();
        }
        else
        {
            SelectedPlanName = null;
            ValidateCurrentSchemeName();
            TestItems.Clear();
            AddLog($"⚠️ 方案 [{value}] 不在当前机种 [{ModelName}] 的方案列表中，检测列表已清空");
        }
    }

    [ObservableProperty]
    private int _totalCount = 0;

    [ObservableProperty]
    private int _passCount = 0;

    [ObservableProperty]
    private int _failCount = 0;

    [RelayCommand]
    private async Task ClearCountersAsync()
    {
        var confirmed = await _notificationService.ConfirmAsync(
            "确定要清零当前批次的统计数据吗？此操作不可恢复！", "清零确认");

        if (confirmed)
        {
            TotalCount = 0;
            PassCount = 0;
            FailCount = 0;
            _logger.LogInformation("计数器已清零");
            await _notificationService.ShowInfoAsync("计数器已清零", "操作成功");
        }
    }

    #endregion

    #region 顶部中间 - 设备连接状态看板

    [ObservableProperty]
    private string _currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    [ObservableProperty]
    private bool _isPlcConnected = false;

    [ObservableProperty]
    private string _plcStatusText = "断开";

    [ObservableProperty]
    private bool _isScannerConnected = false;

    [ObservableProperty]
    private string _scannerStatusText = "断开";

    [ObservableProperty]
    private bool _isDmmConnected = false;

    [ObservableProperty]
    private string _dmmStatusText = "断开";

    /// <summary>PLC 启动请求状态（DT120 值），供 UI 显示"等待 PLC 启动"</summary>
    [ObservableProperty]
    private bool _isPlcStartRequested = false;

    /// <summary>UI 状态文本（测试状态大面板显示）</summary>
    [ObservableProperty]
    private string _sensorStatusText = "待机中";

    private void InitializeClock()
    {
        _clockTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (s, e) =>
        {
            CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        };
        _clockTimer.Start();
    }

    [RelayCommand]
    private async Task ReconnectPlcAsync()
    {
        PlcStatusText = "连接中...";
        await _deviceManager.ReconnectDeviceAsync("PLC");
    }

    [RelayCommand]
    private async Task ReconnectScannerAsync()
    {
        ScannerStatusText = "连接中...";
        IsScannerConnected = false;

        AddLog("🔄 正在尝试重新连接扫描仪...");

        try
        {
            await _deviceManager.ReconnectDeviceAsync("Scanner");
            if (IsScannerConnected)
                AddLog("✅ 扫描仪重连成功");
            else
            {
                AddLog("❌ 扫描仪重连失败 - 请检查：1.USB线是否插好 2.端口配置是否正确 3.设备管理器中COM口是否存在");
                await _notificationService.ShowWarningAsync(
                    "扫描仪连接失败！\n\n请检查：\n1. USB线是否插好\n2. 设备管理器中COM口是否存在\n3. 系统设定中端口配置是否正确",
                    "连接失败");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫描仪重连异常");
            AddLog($"❌ 扫描仪重连异常: {ex.Message}");
            await _notificationService.ShowErrorAsync($"扫描仪重连失败：{ex.Message}", "错误");
        }
    }

    [RelayCommand]
    private async Task ReconnectDmmAsync()
    {
        DmmStatusText = "连接中...";
        await _deviceManager.ReconnectDeviceAsync("DMM");
    }

    #endregion

    #region 顶部右侧 - 测试状态显示

    [ObservableProperty]
    private TestUIState _uiState = TestUIState.Ready;

    [ObservableProperty]
    private bool _isInputEnabled = true;

    /// <summary>
    /// 判断界面上下文是否已经具备人工启动条件。
    /// 扫描仪只是序列号输入方式之一，不作为启动强依赖。
    /// Fake 调试按钮使用该条件，不强依赖 DT120。
    /// </summary>
    private bool CanManualStartInspection()
    {
        return !string.IsNullOrWhiteSpace(ModelName)
               && !string.IsNullOrWhiteSpace(SerialNumber)
               && !IsSchemeNameInvalid
               && !string.IsNullOrWhiteSpace(SchemeName)
               && !string.IsNullOrWhiteSpace(OperatorName)
               && IsPlcConnected
               && IsDmmConnected;
    }

    /// <summary>
    /// 复位结束、保存结束等明确流程收口时使用，强制重新计算待机/可启动。
    /// 不受 Resetting 等状态保护条件拦截。
    /// </summary>
    private void ForceRefreshReadyOrCanStartState()
    {
        bool canStart = CanManualStartInspection();
        UiState = canStart ? TestUIState.CanStart : TestUIState.Ready;
        SensorStatusText = canStart ? "可启动" : "待机中";
        IsInputEnabled = true;
    }

    /// <summary>
    /// 根据当前上下文刷新为待机或可启动，不覆盖检测中、停止、急停、复位、待保存、异常。
    /// </summary>
    private void RefreshReadyOrCanStartState()
    {
        if (UiState == TestUIState.Testing
            || UiState == TestUIState.Paused
            || UiState == TestUIState.EmergencyStop
            || UiState == TestUIState.Resetting
            || UiState == TestUIState.PendingSave
            || UiState == TestUIState.Error)
            return;

        bool canStart = CanManualStartInspection();
        UiState = canStart ? TestUIState.CanStart : TestUIState.Ready;
        SensorStatusText = canStart ? "可启动" : "待机中";
        IsInputEnabled = true;
    }

    /// <summary>
    /// 根据所有设备和 PLC 状态更新 UI 状态。
    /// 使用 RefreshReadyOrCanStartState 统一收口，不再强制依赖 DT120。
    /// </summary>
    private void UpdateUIState()
    {
        RefreshReadyOrCanStartState();

        // 当条件不足但 PLC 已请求启动时，拒绝启动并提示原因
        bool plcStartRequested = IsPlcStartRequested;
        if (plcStartRequested && !CanManualStartInspection() && !_startupCleared)
        {
            RejectStartCondition(
                !string.IsNullOrWhiteSpace(ModelName) && !string.IsNullOrWhiteSpace(SerialNumber),
                !IsSchemeNameInvalid && !string.IsNullOrWhiteSpace(SchemeName),
                !string.IsNullOrWhiteSpace(OperatorName),
                IsPlcConnected && IsDmmConnected);
        }
    }

    /// <summary>
    /// PLC DT120=1 但启动条件不满足时，拒绝启动并记录 Warning 日志。
    /// 无论复核是否通过都清除 DT120，防止重复触发。
    /// </summary>
    private void RejectStartCondition(bool hasProductInfo, bool hasValidPlan,
        bool hasOperator, bool allDevicesReady)
    {
        _startupCleared = true; // 标记已处理，避免重复刷屏

        // 写 Warning 审计日志记录拒绝原因
        if (!hasProductInfo)
        {
            string reason = !string.IsNullOrWhiteSpace(ModelName)
                ? "未输入序列号"
                : "未输入机种名称";
            _logger.LogWarning("[启动复核][拒绝] {Reason}，已清除 DT120", reason);
            AddLog($"❌ 启动拒绝：{reason}。请检查机种名称和序列号。");
            _ = _notificationService.ShowWarningAsync(
                $"PLC已请求启动，但{reason}，无法启动。\n请检查机种名称和序列号。", "启动拒绝");
        }
        else if (!hasValidPlan)
        {
            _logger.LogWarning("[启动复核][拒绝] 机种与方案不匹配，已清除 DT120");
            AddLog("❌ 启动拒绝：机种与方案不匹配。请重新选择方案。");
            _ = _notificationService.ShowWarningAsync(
                "PLC已请求启动，但当前方案无效或不属于当前机种，无法启动。\n请重新选择方案。", "启动拒绝");
        }
        else if (!hasOperator)
        {
            _logger.LogWarning("[启动复核][拒绝] 未指定作业员，已清除 DT120");
            AddLog("❌ 启动拒绝：未指定作业员。");
            _ = _notificationService.ShowWarningAsync(
                "PLC已请求启动，但未指定作业员，无法启动。", "启动拒绝");
        }
        else if (!allDevicesReady)
        {
            var notReady = GetNotReadyDevices();
            _logger.LogWarning("[启动复核][拒绝] 设备未就绪: {Devices}，已清除 DT120",
                string.Join(", ", notReady));
            AddLog($"❌ 启动拒绝：设备未就绪 -> {string.Join(", ", notReady)}");
            _ = _notificationService.ShowWarningAsync(
                $"PLC已请求启动，但以下设备未就绪：\n{string.Join("\n", notReady)}\n请检查设备连接。", "启动拒绝");
        }

        // 清除 DT120，防止重复触发
        _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
    }

    /// <summary>检查影响检测启动的未就绪设备。扫描仪不作为启动强依赖。</summary>
    private List<string> GetNotReadyDevices()
    {
        var notReady = new List<string>();
        if (!IsPlcConnected) notReady.Add("PLC未连接");
        if (!IsDmmConnected) notReady.Add("万用表未连接");
        return notReady;
    }

    #endregion

    #region 中部 - 信息录入区

    [ObservableProperty]
    private string _modelName = string.Empty;

    [ObservableProperty]
    private string _serialNumber = string.Empty;

    [ObservableProperty]
    private string _operatorName = string.Empty;

    [ObservableProperty]
    private bool _isOperatorEditable = true;

    partial void OnModelNameChanged(string value)
    {
        _ = HandleModelNameChangedAsync(value);
    }

    private async Task HandleModelNameChangedAsync(string newMachineType)
    {
        await RefreshPlanNameOptionsAsync(newMachineType);
        ValidateCurrentSchemeName();

        if (IsSchemeNameInvalid)
        {
            TestItems.Clear();
            AddLog($"⚠️ 机种已切换为 [{newMachineType}]，方案 [{SchemeName}] 不属于该机种，检测列表已清空，请重新选择方案");
        }
    }

    #endregion

    #region 下部 - 测试项目列表

    public ObservableCollection<TestItemModel> TestItems { get; } = new();

    private async Task LoadPlanItemsAsync()
    {
        TestItems.Clear();

        if (string.IsNullOrWhiteSpace(ModelName) || string.IsNullOrWhiteSpace(SchemeName))
        {
            AddLog("⚠️ 未指定机种或方案，检测列表为空");
            return;
        }

        var allPlans = await _planStorageService.LoadAllPlansAsync();

        // 精确匹配机种和方案名（替换旧 allPlans.FirstOrDefault() 错误用法）
        var currentPlan = allPlans.FirstOrDefault(p =>
            string.Equals(p.MachineType, ModelName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.PlanName, SchemeName, StringComparison.OrdinalIgnoreCase));

        if (currentPlan == null)
        {
            AddLog($"⚠️ 未找到匹配方案: 机种={ModelName}, 方案={SchemeName}，检测列表为空");
            return;
        }

        if (currentPlan.Items.Count == 0)
        {
            AddLog($"⚠️ 方案 [{currentPlan.PlanName}] 无检测项目，检测列表为空");
            return;
        }

        AddLog($"📋 已加载方案: {currentPlan.MachineType} - {currentPlan.PlanName}");

        var orderedItems = currentPlan.Items.OrderBy(i => i.Index).ToList();

        _inspectionEngine?.SetConfig(new InspectionConfig
        {
            PlanName = currentPlan.PlanName,
            TestPoints = orderedItems
                .Select(TestPointConfig.FromPlanItem)
                .ToList()
        });

        foreach (var item in orderedItems)
        {
            TestItems.Add(new TestItemModel
            {
                Index = item.Index,
                ItemName = item.ItemName,
                CheckMode = item.CheckMode,
                LowerLimitText = FormatLowerLimit(item),
                UpperLimitText = FormatUpperLimit(item),
                CheckResult = "未检测",
                Judgment = string.Empty
            });
        }
    }

    private static string FormatLowerLimit(PlanItem item)
    {
        if (item.CheckMode == CheckModeConstants.Resistance)
            return item.LowerLimit.HasValue
                ? InputValidationHelper.FormatResistanceValue(item.LowerLimit.Value)
                : "-";

        return item.ModeValue switch
        {
            "SHORT" => "SHORT(短路)",
            _ => "OPEN(开路)"
        };
    }

    private static string FormatUpperLimit(PlanItem item)
    {
        if (item.CheckMode == CheckModeConstants.Resistance)
            return item.UpperLimit.HasValue
                ? InputValidationHelper.FormatResistanceValue(item.UpperLimit.Value)
                : "-";

        return "-";
    }

    #endregion

    #region 待保存态 —— 弹窗确认与事务保存

    /// <summary>
    /// 全部 Pin 检测完成后触发。
    /// 使用当前 ModelName + SchemeName 精确匹配方案，不再 allPlans.FirstOrDefault()。
    /// </summary>
    private async Task OnAllPinsTestedAsync()
    {
        // 防重入：如果已经弹出保存对话框，直接返回，避免弹窗堆叠
        if (_isShowingSaveDialog)
        {
            _logger.LogWarning("[UI流程] 保存对话框已在显示中，跳过重复弹窗");
            return;
        }

        _isShowingSaveDialog = true;
        try
        {
            UiState = TestUIState.PendingSave;

            var finalResult = TestItems.All(i => i.Judgment == "OK") ? "OK" : "NG";

            TotalCount++;
            if (finalResult == "OK") PassCount++;
            else FailCount++;

            // ★ 使用当前 ModelName + SchemeName 精确匹配，不再取 allPlans.FirstOrDefault()
            var machineType = string.IsNullOrWhiteSpace(ModelName) ? "Unknown" : ModelName;
            var planName = string.IsNullOrWhiteSpace(SchemeName) ? "Unknown" : SchemeName;

            var ngItems = TestItems.Where(i => i.Judgment == "NG").ToList();
            var ngDetail = ngItems.Any()
                ? string.Join("\n", ngItems.Select(i => $"  • {i.ItemName}: {i.CheckResult} → NG"))
                : "无";

            var confirmed = await _notificationService.ConfirmAsync(
                $"当前方案 [{planName}] 所有项目已检测完毕\n\n" +
                $"综合判定: [{finalResult}]\n\n" +
                $"NG项目:\n{ngDetail}\n\n" +
                $"是否保存本次检测记录？",
                "检测完成");

            if (confirmed)
            {
                await SaveLogToDatabaseAsync(machineType, planName, finalResult);
                await _notificationService.ShowInfoAsync("检测记录已保存！", "保存成功");
                ResetToReadyState();
            }
            else
            {
                ClearTestItemsForRestart();
                ForceRefreshReadyOrCanStartState();
                AddLog("📝 操作员取消保存，检测结果已清空，可重新测试");
            }
        }
        finally
        {
            _isShowingSaveDialog = false;
        }
    }

    /// <summary>
    /// 保存检测记录到 CSV。
    /// ★ 使用当前 ModelName + SchemeName 精确匹配，不再 allPlans.FirstOrDefault()。
    /// </summary>
    private async Task SaveLogToDatabaseAsync(string machineType, string planName, string finalResult)
    {
        try
        {
            // ★ 直接使用当前界面上的 ModelName 和 SchemeName
            var record = new LogRecord
            {
                Timestamp = DateTime.Now,
                Series = machineType,
                MachineType = machineType,
                SerialNumber = SerialNumber,
                PlanName = planName,
                Operator = OperatorName,
                FinalResult = finalResult,
                PinResults = TestItems.Select(item => new PinResult
                {
                    PinName = item.ItemName,
                    Result = item.CheckResult
                }).ToList()
            };

            await _testRecordStorage.SaveRecordAsync(record);
            AddLog($"💾 检测记录已保存 - SN:{SerialNumber}, 结果:{finalResult}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存检测记录失败");
            AddLog($"❌ 保存失败: {ex.Message}");
            await _notificationService.ShowErrorAsync($"保存失败：{ex.Message}", "错误");
        }
    }

    /// <summary>
    /// 清空检测项目的运行结果，用于复位、取消保存和准备重新检测。
    /// </summary>
    private void ClearTestItemsForRestart()
    {
        foreach (var item in TestItems)
        {
            item.CheckResult = "未检测";
            item.Judgment = string.Empty;
        }
    }

    /// <summary>
    /// 回到准备态，使用强制收口自动计算待机/可启动。
    /// </summary>
    private void ResetToReadyState()
    {
        SerialNumber = string.Empty;
        ClearTestItemsForRestart();
        IsPlcStartRequested = false;
        _inspectionStarted = false;
        _startupCleared = false;
        ForceRefreshReadyOrCanStartState();
        AddLog("✅ 准备就绪，可进行下一次检测");
    }

    #endregion

    #region 终了按钮

    [RelayCommand]
    private async Task FinishAndReturnAsync()
    {
        string confirmMsg = UiState switch
        {
            TestUIState.Testing => "正在测试中，确定要终止当前测试并返回主菜单吗？\n未完成的测试数据将丢失！",
            TestUIState.PendingSave => "有未保存的检测结果，返回将丢失本次所有数据，确定继续吗？",
            TestUIState.EmergencyStop => "急停中返回主菜单将丢失当前数据，确定继续吗？",
            _ => "确定要返回主菜单吗？"
        };

        var confirmed = await _notificationService.ConfirmAsync(confirmMsg, "确认返回");
        if (!confirmed) return;

        if (UiState == TestUIState.Testing)
        {
            _inspectionEngine?.Stop();
            AddLog("⚠️ 操作员终止了当前测试");
        }

        StopPlcPolling();
        ResetToReadyState();

        await _navigationService.NavigateToAsync<MainMenuView>();
    }

    #endregion

    #region 底部 - 日志区

    public ObservableCollection<string> LogMessages { get; } = new();

    private void AddLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogMessages.Add($"[{timestamp}] {message}");

            while (LogMessages.Count > 500)
                LogMessages.RemoveAt(0);
        });
    }

    #endregion

    #region PLC 轮询

    /// <summary>
    /// 启动 PLC 状态轮询（取代旧传感器模拟）。
    /// 每 300ms 读取一次 PLC 输入信号，更新 UI 状态。
    /// </summary>
    private void StartPlcPolling()
    {
        if (_plcPollingTimer != null) return;

        _plcPollingTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };

        _plcPollingTimer.Tick += async (s, e) =>
        {
            await PollPlcInputsAsync();
        };

        _plcPollingTimer.Start();
        _logger.LogDebug("PLC 轮询已启动");
    }

    /// <summary>
    /// 停止 PLC 轮询。
    /// </summary>
    private void StopPlcPolling()
    {
        if (_plcPollingTimer != null)
        {
            _plcPollingTimer.Stop();
            _plcPollingTimer = null;
        }
        _logger.LogDebug("PLC 轮询已停止");
    }

    /// <summary>
    /// PLC 轮询主体：读取输入信号 → 更新 UI 状态 → 检测 DT120 启动请求。
    /// </summary>
    private async Task PollPlcInputsAsync()
    {
        if (!_plcDevice.IsConnected || _inspectionEngine == null) return;

        try
        {
            var result = await _plcDevice.ReadMachineInputsAsync(CancellationToken.None).ConfigureAwait(false);
            if (!result.IsSuccess || result.Value == null) return;

            var inputs = result.Value;
            _lastPlcInputs = inputs;

            // 更新 PLC 启动请求状态（供 UI 显示）
            IsPlcStartRequested = inputs.IsStartRequested;

            // 更新 UI 状态（根据当前模式）
            // [修改] InvokeAsync 内改为 async lambda，等待 UpdateUiStateFromPlcInputsAsync 执行完成
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await UpdateUiStateFromPlcInputsAsync(inputs);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PLC 轮询异常");
        }
    }

    /// <summary>
    /// 根据 PLC 输入信号更新 UI 状态（在 UI 线程执行）。
    /// 复位信号 DT121 的处理遵循握手协议：
    ///   1. 收到 DT121=1 → 上位机执行所有复位操作
    ///   2. 上位机操作全部完成后 → 最后清除 DT121
    ///   3. 清除 DT121 后 → 通知 PLC 上位机已就绪
    /// </summary>
    private async Task UpdateUiStateFromPlcInputsAsync(PlcMachineInputs inputs)
    {
        // ── 复位信号边沿检测：DT121 从 1→0 时重置处理标志 ──
        if (!inputs.IsResetRequested)
        {
            _resetSignalHandled = false;
            _isResetting = false;
        }

        // ════════════════════════════════════════════════════════
        // 复位信号处理（优先级最高，急停状态下也能触发）
        // ════════════════════════════════════════════════════════
        if (inputs.IsResetRequested)
        {
            if (_resetSignalHandled)
                return;

            _resetSignalHandled = true;
            // 1. 立即锁门：屏蔽所有待处理的引擎回调
            _ignoreInspectionCallbacksUntilNextStart = true;
            _isResetting = true;

            UiState = TestUIState.Resetting;
            SensorStatusText = "复位中";
            AddLog("PLC 复位信号(DT121)，正在执行上位机复位操作...");

            // ──────────────────────────────────────────────────
            // 步骤2：停止检测引擎（如果正在运行）
            // ──────────────────────────────────────────────────
            if (_inspectionEngine!.IsRunning)
            {
                _inspectionEngine.Stop();
                _logger.LogInformation("[复位流程] 已停止检测引擎");
            }

            // ──────────────────────────────────────────────────
            // 步骤3：清空 PLC 引脚输出区 DT130~DT185
            //        （所有引脚取消选择，极性归零）
            // ──────────────────────────────────────────────────
            await _plcDevice.ClearPinOutputsAsync(CancellationToken.None);
            _logger.LogInformation("[复位流程] 已清空 PLC 引脚输出区 DT130~DT185");

            // ──────────────────────────────────────────────────
            // 步骤4：清除 DT234（上位机允许开始检测）
            //        通知 PLC 上位机已退出检测状态
            // ──────────────────────────────────────────────────
            await _plcDevice.ClearPcReadyAsync(CancellationToken.None);
            _logger.LogInformation("[复位流程] 已清除 DT234（上位机允许开始检测）");

            // ──────────────────────────────────────────────────
            // 步骤5：Fake 模式下同步清除停止/急停信号
            //        （接入真实 PLC 后此分支不会执行）
            // ──────────────────────────────────────────────────
            if (_plcDevice is Devices.Fakes.FakeInspectionHardware fake)
            {
                await fake.WriteInputRegisterAsync(PlcAddressMap.StopSignal, 0, CancellationToken.None);
                await fake.WriteInputRegisterAsync(PlcAddressMap.EmergencyStopSignal, 0, CancellationToken.None);
                _logger.LogInformation("[复位流程][Fake] 已清除 DT122(停止) 和 DT123(急停)");
            }

            // ──────────────────────────────────────────────────
            // 步骤6：清空界面检测项目显示
            //        （将 TestItems 中所有项的 CheckResult 和 Judgment 重置）
            // ──────────────────────────────────────────────────
            ClearTestItemsForRestart();
            _logger.LogInformation("[复位流程] 已清空界面检测项目结果");

            // ──────────────────────────────────────────────────
            // 步骤7：强制刷新 Dispatcher 队列
            //        确保所有先前排队的、优先级低于 Background 的
            //        引擎回调（OnStepCompleted 等）全部执行完毕。
            //        由于 _isResetting=true，这些回调会被忽略，
            //        不会污染已清空的 TestItems。
            //        这是解决"偶尔没清空"问题的关键步骤。
            // ──────────────────────────────────────────────────
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background);

            // ──────────────────────────────────────────────────
            // 步骤8：重置上位机内部状态标志
            //        （启动标记、急停对话框标记、PLC启动请求等）
            // ──────────────────────────────────────────────────
            _inspectionStarted = false;
            _startupCleared = false;
            _isShowingEmergencyDialog = false;
            _emergencyDialogAcknowledged = false;
            _emergencyStopDialogVM?.StopPolling();
            _emergencyStopDialogVM = null;
            IsPlcStartRequested = false;
            _inspectionEngine.ClearResetState();
            _logger.LogInformation("[复位流程] 已重置内部状态标志");

            // ──────────────────────────────────────────────────
            // 步骤9：【关键】所有上位机操作完成后，最后清除 DT121
            //        告知 PLC："上位机复位已完成，可以接收新的启动请求"
            // ──────────────────────────────────────────────────
            await _plcDevice.ClearResetRequestAsync(CancellationToken.None);
            _logger.LogInformation("[复位流程] 上位机复位操作全部完成，已清除 DT121 复位信号");

            // ──────────────────────────────────────────────────
            // 步骤10：解除保护状态，强制刷新 UI 到最终态
            //        根据当前设备/信息条件，显示"可启动"或"待机中"
            // ──────────────────────────────────────────────────
            _isResetting = false;
            ForceRefreshReadyOrCanStartState();
            AddLog("✅ 复位完成，已清空所有检测结果，可重新开始测试");
            await _notificationService.ShowInfoAsync(
                "复位完成，已清空所有检测结果，可重新开始测试。",
                "复位完成");

            // 直接 return，不再执行后续 UpdateUIState()，
            // 避免 ForceRefreshReadyOrCanStartState 的结果被覆盖
            return;
        }

        // DT123 回到 0，表示上一轮急停已真正复位，允许下一次急停重新弹窗。
        if (!inputs.IsEmergencyStop)
        {
            _emergencyDialogAcknowledged = false;
        }

        // ════════════════════════════════════════════════════════
        // 急停信号（不锁定检测中状态，检测中也能触发）
        // ════════════════════════════════════════════════════════
        if (inputs.IsEmergencyStop)
        {
            if (UiState != TestUIState.EmergencyStop)
            {
                UiState = TestUIState.EmergencyStop;
                SensorStatusText = "急停中";
                _logger.LogWarning("[PLC轮询] 检测到急停信号(DT123)");
                if (_inspectionEngine!.IsRunning)
                    _inspectionEngine.Stop();
            }

            // 弹窗触发必须独立于状态切换。
            // 检测引擎可能先把 UiState 切到 EmergencyStop，轮询层仍要补弹急停锁定弹窗。
            // 解除按钮只清 DT303，不清 DT123；因此解除确认后要等待复位/DT123=0，再允许下一次弹窗。
            if (!_isShowingEmergencyDialog && !_emergencyDialogAcknowledged)
            {
                _isShowingEmergencyDialog = true;
                ShowEmergencyStopDialog();
            }
            return;
        }

        // ════════════════════════════════════════════════════════
        // 停止信号 — 边沿处理，只在 0→1 时触发一次
        // ════════════════════════════════════════════════════════
        if (!inputs.IsStopRequested)
        {
            _stopSignalHandled = false;
        }

        if (inputs.IsStopRequested)
        {
            if (!_stopSignalHandled)
            {
                _stopSignalHandled = true;
                UiState = TestUIState.Paused;
                SensorStatusText = "已停止";
                _inspectionEngine?.PauseWithCheckpoint();
            }
            return;
        }

        // ════════════════════════════════════════════════════════
        // 启动请求 DT120=1（仅在未启动时处理）
        // ════════════════════════════════════════════════════════
        if (inputs.IsStartRequested && !_inspectionStarted)
        {
            HandlePlcStartRequest();
        }

        // ════════════════════════════════════════════════════════
        // 更新普通待机/可启用状态
        // ════════════════════════════════════════════════════════
        UpdateUIState();
    }

    /// <summary>
    /// 处理 PLC DT120=1 启动请求。
    /// 复核所有启动条件，通过后调用 RunInspectionAsync。
    /// </summary>
    private void HandlePlcStartRequest()
    {
        _logger.LogWarning("[PLC轮询][审计] 收到 PLC 启动请求(DT120=1)");

        // 启动复核
        string? rejectReason = ValidateStartConditions();
        if (rejectReason != null)
        {
            // 复核不通过：清除 DT120，写 Warning 日志，提示操作员
            _logger.LogWarning("[启动复核][拒绝] {Reason}", rejectReason);
            _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
            _startupCleared = true;
            AddLog($"❌ {rejectReason}");
            _ = _notificationService.ShowWarningAsync(rejectReason, "启动拒绝");
            return;
        }

        // 复核通过：清除 DT120，锁存启动标记，触发检测
        _logger.LogWarning("[启动复核][通过] 所有条件满足，开始检测");
        _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
        _inspectionStarted = true;

        // 锁定当前机种、方案、作业员
        string lockedModel = ModelName;
        string lockedBarcode = SerialNumber;
        string lockedOperator = OperatorName;

        _ = RunInspectionAsync(lockedBarcode, lockedModel, lockedOperator);
    }

    /// <summary>
    /// 弹出急停模态弹窗。
    /// 弹窗独立轮询 DT303，关闭后页面保持急停保护状态。
    /// </summary>
    private void ShowEmergencyStopDialog()
    {
        bool isFake = _plcDevice is Devices.Fakes.FakeInspectionHardware;

        // 先创建 dialog，确保 closeDialog 回调能捕获 dialog 引用
        var dialog = new Views.EmergencyStopDialog
        {
            Owner = Application.Current.MainWindow
        };

        var vm = new ViewModels.EmergencyStopDialogViewModel(
            _plcDevice,
            closeDialog: () =>
            {
                // 使用 InvokeAsync 确保在 UI 线程安全关闭
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dialog.AllowClose();
                    dialog.DialogResult = true;
                    dialog.Close();

                    _isShowingEmergencyDialog = false;
                    _emergencyDialogAcknowledged = true;
                    _emergencyStopDialogVM?.StopPolling();
                    _emergencyStopDialogVM = null;
                });
            },
            isFakeMode: isFake);

        dialog.DataContext = vm;
        _emergencyStopDialogVM = vm;

        // 启动 DT303 后台轮询
        vm.StartPolling();

        // 模态显示弹窗（阻塞，直到用户点击"解除"）
        _ = vm; // 保持引用
        dialog.ShowDialog();
    }

    /// <summary>
    /// 启动前复核。
    /// 返回 null 表示通过，返回字符串表示拒绝原因。
    /// </summary>
    private string? ValidateStartConditions()
    {
        // 急停状态下拒绝一切启动请求
        if (UiState == TestUIState.EmergencyStop)
            return "设备处于急停状态，请先复位后再启动检测";

        if (string.IsNullOrWhiteSpace(ModelName))
            return "未输入机种名称，无法启动";
        if (string.IsNullOrWhiteSpace(SerialNumber))
            return "未输入序列号，无法启动";
        if (string.IsNullOrWhiteSpace(SchemeName) || IsSchemeNameInvalid)
            return "当前方案无效或不属于当前机种，无法启动";
        if (string.IsNullOrWhiteSpace(OperatorName))
            return "未指定作业员，无法启动";
        if (!IsPlcConnected || !IsDmmConnected)
        {
            var notReady = GetNotReadyDevices();
            return $"设备未就绪：{string.Join(", ", notReady)}，无法启动";
        }
        return null;
    }

    /// <summary>
    /// 临时 Fake 启动按钮：用于最小闭环调试。
    /// 只负责复核界面上下文并调用 InspectionEngine，硬件流程仍由引擎负责。
    /// </summary>
    [RelayCommand]
    private async Task StartFakeMinimalInspectionAsync()
    {
        if (_inspectionEngine == null)
            return;

        // 急停状态下禁止启动，必须复位后才能开始
        if (UiState == TestUIState.EmergencyStop)
        {
            await _notificationService.ShowWarningAsync(
                "急停状态中，请先点击复位后再启动检测。",
                "启动拒绝");
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelName)
            || string.IsNullOrWhiteSpace(SerialNumber)
            || string.IsNullOrWhiteSpace(SchemeName)
            || IsSchemeNameInvalid
            || string.IsNullOrWhiteSpace(OperatorName))
        {
            await _notificationService.ShowWarningAsync(
                "机种、序列号、方案、作业员必须完整后才能启动 Fake 检测。",
                "Fake 启动拒绝");
            return;
        }

        if (TestItems.Count == 0)
            await LoadPlanItemsAsync();

        if (TestItems.Count == 0)
        {
            await _notificationService.ShowWarningAsync("当前方案没有可检测项目。", "Fake 启动拒绝");
            return;
        }

        _logger.LogWarning("[Fake最小闭环][审计] 操作员手动点击开始检测(Fake)");
        AddLog("开始 Fake 最小闭环检测");
        _ignoreInspectionCallbacksUntilNextStart = false;
        _isResetting = false;

        // 清除残留的 PLC 信号（模拟 PLC 启动时自动清零的行为）
        // 注意：不清除 DT123（急停），急停后必须先复位才能启动
        if (_plcDevice is Devices.Fakes.FakeInspectionHardware fake)
        {
            await fake.WriteInputRegisterAsync(PlcAddressMap.StopSignal, 0);
            AddLog("⚠️ [Fake调试] 已清除 DT122 信号");
        }

        await RunInspectionAsync(SerialNumber, ModelName, OperatorName);
    }

    // ═══════════════════════════════════════════════════════════════
    // 临时异常信号触发按钮：仅 Fake 模式调试用。
    // 通过 FakeInspectionHardware 直接写入 DT 寄存器，触发 PLC 信号中断路径。
    // 接入真实 PLC 后删除这组命令和对应的 XAML 按钮。
    // ═══════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task FakeTriggerStopAsync()
    {
        if (_plcDevice is not Devices.Fakes.FakeInspectionHardware fake)
        {
            AddLog("⚠️ 触发停止仅支持 Fake 模式");
            return;
        }

        await fake.WriteInputRegisterAsync(PlcAddressMap.StopSignal, 1);
        await _notificationService.ShowWarningAsync(
            "已触发停止信号(DT122)，检测暂停，保留断点。",
            "Fake 触发");
    }

    [RelayCommand]
    private async Task FakeTriggerResetAsync()
    {
        if (_plcDevice is not Devices.Fakes.FakeInspectionHardware fake)
        {
            AddLog("⚠️ 触发复位仅支持 Fake 模式");
            return;
        }

        // 只写寄存器，由 PLC 轮询和 InspectionEngine 处理信号，不直接取消引擎
        await fake.WriteInputRegisterAsync(PlcAddressMap.ResetSignal, 1);
        AddLog("⚠️ [Fake调试] 已写入 DT121=1（复位信号）");
    }

    [RelayCommand]
    private async Task FakeTriggerEmergencyStopAsync()
    {
        if (_plcDevice is not Devices.Fakes.FakeInspectionHardware fake)
        {
            AddLog("⚠️ 触发急停仅支持 Fake 模式");
            return;
        }

        // 只写寄存器，由 PLC 轮询和 InspectionEngine 处理信号
        await fake.WriteInputRegisterAsync(PlcAddressMap.EmergencyStopSignal, 1);
        AddLog("⚠️ [Fake调试] 已写入 DT123=1（急停信号）");
    }

    /// <summary>
    /// 在后台线程执行检测（异步非阻塞 UI）。
    /// </summary>
    private async Task RunInspectionAsync(string barcode, string modelName, string operatorName)
    {
        if (_inspectionEngine == null) return;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            UiState = TestUIState.Testing;
            SensorStatusText = "测试中";
            IsInputEnabled = false;
        });

        var result = await Task.Run(async () =>
        {
            return await _inspectionEngine.RunInspectionAsync(
                barcode, modelName, operatorName, 0, CancellationToken.None);
        }).ConfigureAwait(false);
    }

    #endregion

    #region 硬件事件订阅

    private void SubscribeToHardwareEvents()
    {
        _deviceManager.PlcConnectionStateChanged += (s, e) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsPlcConnected = e.IsConnected;
                PlcStatusText = e.StatusText;
                UpdateUIState();
            });
        };

        _deviceManager.DmmConnectionStateChanged += (s, e) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsDmmConnected = e.IsConnected;
                DmmStatusText = e.StatusText;
                UpdateUIState();
            });
        };

        _deviceManager.ScannerConnectionStateChanged += (s, e) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsScannerConnected = e.IsConnected;
                ScannerStatusText = e.StatusText;
                UpdateUIState();
            });
        };

        _deviceManager.BarcodeScanned += OnScannerBarcodeParsed;

        if (_inspectionEngine != null)
        {
            _inspectionEngine.StateChanged += OnInspectionStateChanged;
            _inspectionEngine.StepStarted += OnStepStarted;
            _inspectionEngine.StepCompleted += OnStepCompleted;
            _inspectionEngine.InspectionCompleted += OnInspectionCompleted;
            _inspectionEngine.LogMessage += (s, msg) => AddLog(msg);
        }
    }

    private void OnScannerBarcodeParsed(object? sender, BarcodeParsedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (UiState == TestUIState.Testing || UiState == TestUIState.PendingSave)
            {
                AddLog("⚠️ 测试中禁止扫码，条码已忽略");
                return;
            }

            ModelName = e.ModelName;
            SerialNumber = e.SerialPart ?? string.Empty;
            ValidateCurrentSchemeName();

            AddLog($"📷 扫描到条码: 机种={e.ModelName}, 序列号={e.SerialPart}");
        });
    }

    /// <summary>
    /// 检测引擎状态变更 → 映射为 UI 状态。
    /// </summary>
    private void OnInspectionStateChanged(object? sender, InspectionStateChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            UiState = e.NewState switch
            {
                InspectionState.Testing => TestUIState.Testing,
                InspectionState.CompletedPass => TestUIState.PendingSave,
                InspectionState.CompletedFail => TestUIState.PendingSave,
                InspectionState.PausedByStop => TestUIState.Paused,
                InspectionState.PausedByEmergencyStop => TestUIState.EmergencyStop,
                InspectionState.ResetRequested => TestUIState.Resetting,
                InspectionState.Aborted => UiState switch
                {
                    TestUIState.Paused => TestUIState.Paused,
                    TestUIState.Resetting => TestUIState.Resetting,
                    TestUIState.EmergencyStop => TestUIState.EmergencyStop,
                    _ => TestUIState.Error
                },
                InspectionState.Error => TestUIState.Error,
                _ => UiState
            };

            SensorStatusText = e.NewState switch
            {
                InspectionState.Testing => "测试中",
                InspectionState.CompletedPass => "待保存",
                InspectionState.CompletedFail => "待保存",
                InspectionState.PausedByStop => "已停止",
                InspectionState.PausedByEmergencyStop => "急停中",
                InspectionState.ResetRequested => "复位中",
                InspectionState.Aborted => UiState switch
                {
                    TestUIState.Paused => "已停止",
                    TestUIState.Resetting => "复位中",
                    TestUIState.EmergencyStop => "急停中",
                    _ => "已中止"
                },
                InspectionState.Error => "异常",
                _ => SensorStatusText
            };

            IsInputEnabled = (UiState != TestUIState.Testing
                              && UiState != TestUIState.PendingSave
                              && UiState != TestUIState.EmergencyStop
                              && UiState != TestUIState.Paused);
        });
    }

    /// <summary>
    /// 单步检测开始回调。
    /// </summary>
    private void OnStepStarted(object? sender, StepStartedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_ignoreInspectionCallbacksUntilNextStart || _isResetting)
                return;

            var item = TestItems.ElementAtOrDefault(e.StepIndex);
            if (item != null)
            {
                item.CheckResult = "测试中";
                item.Judgment = string.Empty;
                AddLog($"🔍 [{e.StepIndex + 1}/{TestItems.Count}] {e.TestPoint.Name} 检测中...");
            }
            else
            {
                _logger.LogWarning("StepStarted 事件中的 StepIndex={StepIndex} 超出 TestItems 范围（Count={Count}）",
                    e.StepIndex, TestItems.Count);
            }
        });
    }

    /// <summary>
    /// 单步检测完成回调。
    /// </summary>
    private void OnStepCompleted(object? sender, StepCompletedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_ignoreInspectionCallbacksUntilNextStart || _isResetting)
                return;

            var item = TestItems.ElementAtOrDefault(e.StepIndex);
            if (item != null)
            {
                item.CheckResult = FormatMeasurementResult(e.Measurement, e.TestPoint);
                item.Judgment = e.TestPoint.Judgment;
            }
        });
    }

    private static string FormatMeasurementResult(MeasurementResult measurement, TestPointConfig testPoint)
    {
        if (!measurement.IsValid)
            return "测量失败";

        double value = measurement.Value;

        if (testPoint.CheckMode == CheckModeConstants.Continuity)
        {
            if (value > 1_000_000.0) return "开路";
            if (value < 1.0) return "短路";
            return $"{InputValidationHelper.FormatResistanceValue(value)} Ω";
        }

        return $"{InputValidationHelper.FormatResistanceValue(value)} Ω";
    }

    /// <summary>
    /// 全部检测完成回调。
    /// </summary>
    private void OnInspectionCompleted(object? sender, InspectionCompletedEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            if (_ignoreInspectionCallbacksUntilNextStart || _isResetting)
                return;

            if (e.Result.IsAborted)
            {
                AddLog($"⚠️ 检测中止: {e.Result.ErrorMessage}");
                return;
            }

            var msg = e.Result.IsAllPassed
                ? $"✅ 检测完成: 良品 (耗时{e.Result.Duration.TotalSeconds:F1}s)"
                : $"❌ 检测完成: 不良 (耗时{e.Result.Duration.TotalSeconds:F1}s)";
            AddLog(msg);

            await OnAllPinsTestedAsync();
        });
    }

    #endregion

    #region INavigationAware

    public async Task OnNavigatedToAsync(object? parameter = null)
    {
        _logger.LogInformation("进入运行界面");

        var operatorName = _operatorStateService?.CurrentOperatorName ?? "默认作业员";
        OperatorName = operatorName;
        IsOperatorEditable = false;
        IsInputEnabled = true;
        UiState = TestUIState.Ready;

        AddLog($"当前作业员: {operatorName}");

        // 加载方案信息
        await RefreshPlanNameOptionsAsync(ModelName);
        ValidateCurrentSchemeName();
        await LoadPlanItemsAsync();

        // 重新订阅扫码事件
        _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;
        _deviceManager.BarcodeScanned += OnScannerBarcodeParsed;

        // 同步设备连接状态
        SyncDeviceStates();

        // ★ 启动 PLC 轮询替代传感器模拟
        StartPlcPolling();
        AddLog("正在等待 PLC 启动信号...");
    }

    public Task OnNavigatedFromAsync()
    {
        _logger.LogInformation("离开运行界面");
        StopPlcPolling();

        if (_deviceManager != null)
        {
            _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;
        }

        return Task.CompletedTask;
    }

    public Task<bool> CanNavigateFromAsync()
    {
        if (UiState == TestUIState.Testing || UiState == TestUIState.PendingSave)
            return Task.FromResult(false);
        return Task.FromResult(true);
    }

    #endregion

    #region 方案名称联动逻辑（机种 ↔ 方案）
    
    private async Task RefreshPlanNameOptionsAsync(string machineType)
    {
        PlanNameOptions.Clear();

        if (string.IsNullOrWhiteSpace(machineType))
        {
            _logger.LogDebug("机种为空，方案下拉选项已清空");
            return;
        }

        try
        {
            var planNames = await _planStorageService.GetPlanNamesByMachineTypeAsync(machineType);

            foreach (var name in planNames)
            {
                PlanNameOptions.Add(name);
            }

            _logger.LogDebug("方案下拉选项已刷新，机种={MachineType}，共 {Count} 个方案",
                machineType, PlanNameOptions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载机种 {MachineType} 的方案列表失败", machineType);
        }
    }

    private void ValidateCurrentSchemeName()
    {
        if (string.IsNullOrWhiteSpace(ModelName))
        {
            IsSchemeNameInvalid = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(SchemeName))
        {
            IsSchemeNameInvalid = false;
            return;
        }

        bool isValid = PlanNameOptions.Any(
            planName => string.Equals(planName, SchemeName, StringComparison.OrdinalIgnoreCase));

        IsSchemeNameInvalid = !isValid;

        if (!isValid)
        {
            string warningMessage = $"⚠️ 方案无效: 机种=[{ModelName}]，方案=[{SchemeName}]，该方案不属于当前机种，请重新选择方案";
            AddLog(warningMessage);
            _logger.LogWarning("方案名无效: 机种={ModelName}, 方案名={SchemeName}", ModelName, SchemeName);
        }
    }

    #endregion

    #region 自动连接硬件

    private void SyncDeviceStates()
    {
        IsPlcConnected = _deviceManager.IsPlcConnected;
        PlcStatusText = _deviceManager.PlcStatusText;
        IsDmmConnected = _deviceManager.IsDmmConnected;
        DmmStatusText = _deviceManager.DmmStatusText;
        IsScannerConnected = _deviceManager.IsScannerConnected;
        ScannerStatusText = _deviceManager.ScannerStatusText;
        UpdateUIState();
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _clockTimer?.Stop();
        _clockTimer = null;
        StopPlcPolling();

        if (_deviceManager != null)
        {
            _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;
        }

        if (_inspectionEngine != null)
        {
            _inspectionEngine.StateChanged -= OnInspectionStateChanged;
            _inspectionEngine.StepStarted -= OnStepStarted;
            _inspectionEngine.StepCompleted -= OnStepCompleted;
            _inspectionEngine.InspectionCompleted -= OnInspectionCompleted;
        }

        GC.SuppressFinalize(this);
    }

    #endregion
}
