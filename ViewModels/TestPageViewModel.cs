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
using Microsoft.Extensions.Configuration;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Fakes;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

// 修改后：
/// <summary>
/// 运行界面 UI 状态枚举
/// Ready:          待机中 — 信息不全或设备未就绪
/// CanStart:       可启动 — 人工启动条件满足
/// Testing:        测试中 — 检测引擎运行中
/// Paused:         已停止 — DT122 停止，保留断点
/// EmergencyStop:  急停中 — DT123 急停，必须复位
/// Resetting:      复位中 — DT121 复位处理中
/// CompletedPass:  检测完成-良品（OK）— 等待操作员保存/取消
/// CompletedFail:  检测完成-不良（NG）— 等待操作员保存/取消
/// Error:          异常 — 板离或不可继续错误
/// SingleItemNgStopped: 单项 NG 后按系统设置停止本轮
/// </summary>
public enum TestUIState
{
    Ready,
    CanStart,
    Testing,
    Paused,
    EmergencyStop,
    Resetting,
    CompletedPass,
    CompletedFail,
    Error,
    SingleItemNgStopped
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
    private readonly IConfiguration _configuration;

    // 硬件服务

    private readonly IDeviceConnectionManager _deviceManager;
    private readonly IMultimeterDevice _multimeterDevice;
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

    /// <summary>终了流程是否正在执行中。true 时 OnNavigatedFromAsync 跳过 PLC 清理，避免重复。</summary>
    private bool _isFinishing;

    /// <summary>急停弹窗 ViewModel 引用。弹窗打开期间非 null，轮询时推送 DT303 状态。</summary>
    private ViewModels.EmergencyStopDialogViewModel? _emergencyStopDialogVM;

    /// <summary>
    /// 检测运行版本号。每次启动检测或复位时递增。
    /// 用于 RunInspectionAsync 返回时判断当前结果是否属于最新一轮，旧任务结果直接丢弃。
    /// </summary>
    private int _inspectionRunVersion;

    /// <summary>
    /// 复位后等待 DT120 启动请求释放。
    /// 复位流程会主动清 DT120，但真实 PLC 或按钮链路可能需要一个轮询周期才读回 0。
    /// 该标志为 true 时，即使读到 DT120=1 也不允许重新启动检测。
    /// </summary>
    private bool _waitDt120ReleaseAfterReset;

    /// <summary>
    /// 复位流程是否正在执行中。
    /// 用于防止按钮入口、Fake 入口、PLC 轮询入口并发进入同一次复位流程。
    /// _resetSignalHandled 处理 DT121 电平保持时的边沿防重复，
    /// _resetFlowInProgress 处理多个入口同时调用 ExecuteResetFlowAsync 的竞态。
    /// </summary>
    private bool _resetFlowInProgress;

    /// <summary>
    /// 停止流程是否正在执行中，防止 PLC 轮询和调试按钮并发进入停止流程。
    /// </summary>
    private bool _stopFlowInProgress;

    /// <summary>
    /// 原子标志：InspectionCompleted 回调是否已被处理。
    /// 使用 Interlocked.Exchange 原子操作保证线程安全，
    /// 防止引擎多次触发 InspectionCompleted 导致重复弹出保存对话框。
    /// 0 = 未处理（允许进入），1 = 已处理（拦截重复）。
    /// </summary>
    private int _inspectionCompletedHandled;

    /// <summary>
    /// DT120 启动信号是否已处理（上升沿触发）。
    /// DT120=0 时重置为 false，DT120=1 且 false 时才允许进入启动复核。
    /// 复位后立即置 true，避免残留电平重新启动。
    /// </summary>
    private bool _startSignalHandled;

    /// <summary>
    /// 硬件和检测引擎事件是否已订阅。运行页是瞬时页面，必须在离开时退订，
    /// 否则旧 ViewModel 会继续响应单例 InspectionEngine 的完成事件并重复弹保存窗口。
    /// </summary>
    private bool _hardwareEventsSubscribed;

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
        IMultimeterDevice multimeterDevice,
        IConfiguration configuration,
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
        _multimeterDevice = multimeterDevice ?? throw new ArgumentNullException(nameof(multimeterDevice));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
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
    private string? _finalJudgment;

    [ObservableProperty]
    private TestUIState _uiState = TestUIState.Ready;

    [ObservableProperty]
    private bool _isInputEnabled = true;

    /// <summary>是否为 Fake 调试模式（_plcDevice 为 FakeInspectionHardware）</summary>
    public bool IsFakeMode => _plcDevice is FakeInspectionHardware;
    /// <summary>半实物联调入口模式：非 Fake 且 SemiPhysicalDebug=true</summary>
    public bool IsSemiPhysicalDebugMode => !IsFakeMode && _configuration.GetValue<bool>("Hardware:SemiPhysicalDebug");
    /// <summary>调试面板可见（Fake 或半实物）</summary>
    public bool IsDebugControlPanelVisible => IsFakeMode || IsSemiPhysicalDebugMode;
    /// <summary>真实模式复位可见（非 Fake 且非半实物）</summary>
    public bool IsRealModeResetVisible => !IsFakeMode && !IsSemiPhysicalDebugMode;

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
    /// 根据当前上下文刷新为待机或可启动，不覆盖检测中、停止、急停、复位、待保存、单项 NG 停止、异常。
    /// </summary>
    private void RefreshReadyOrCanStartState()
    {
        if (UiState == TestUIState.Testing
        || UiState == TestUIState.Paused
        || UiState == TestUIState.EmergencyStop
        || UiState == TestUIState.Resetting
        || UiState == TestUIState.CompletedPass
        || UiState == TestUIState.CompletedFail
        || UiState == TestUIState.SingleItemNgStopped
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

        // 从设备设置读取导通阈值，传入 InspectionConfig
        double continuityThreshold = 10.0;
        try
        {
            var deviceSettings = _settingsService.LoadSettings();
            if (deviceSettings?.GDM9060Communication != null)
            {
                double configuredThreshold = deviceSettings.GDM9060Communication.ContinuityThresholdOhm;
                if (configuredThreshold >= 1.0 && configuredThreshold <= 1000.0)
                {
                    continuityThreshold = configuredThreshold;
                }
                else
                {
                    _logger.LogWarning("[运行页] 导通阈值配置非法：{Threshold}Ω，已使用默认值 10Ω", configuredThreshold);
                }
            }
        }
        catch (Exception ex)
        {
            // 加载失败使用默认值 10Ω，不影响后续流程
            _logger.LogWarning(ex, "[运行页] 读取导通阈值失败，已使用默认值 10Ω");
        }

        bool continueTestingAfterNg = _settingsService.LoadSettings().ContinueTestingAfterNg;
        bool skipDt302Wait = _configuration.GetValue<bool>("Hardware:SkipDt302Wait");

        _inspectionEngine?.SetConfig(new InspectionConfig
        {
            PlanName = currentPlan.PlanName,
            TestPoints = orderedItems
                .Select(TestPointConfig.FromPlanItem)
                .ToList(),
            ContinuityThresholdOhm = continuityThreshold,
            SkipDt302Wait = skipDt302Wait,
            ContinueTestingAfterNg = continueTestingAfterNg
        });

        if (skipDt302Wait)
        {
            _logger.LogWarning("[运行页][审计] 半实物调试：已启用 DT302 临时旁路。正式整机联调必须关闭 Hardware:SkipDt302Wait。");
            AddLog("⚠️ 半实物调试：已启用 DT302 临时旁路");
        }

        _logger.LogInformation("[运行页][审计] 检测配置已更新：单项 NG 后继续测试={ContinueAfterNg}", continueTestingAfterNg);
        AddLog($"⚙ 单项 NG 后继续测试={(continueTestingAfterNg ? "开启" : "关闭")}");

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
            var finalResult = TestItems.All(i => i.Judgment == "OK") ? "OK" : "NG";
            FinalJudgment = finalResult;

            UiState = finalResult == "OK" ? TestUIState.CompletedPass : TestUIState.CompletedFail;

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
                await CompleteNormalInspectionHandshakeAsync().ConfigureAwait(false);
                await Application.Current.Dispatcher.InvokeAsync(ResetToReadyState);
            }
            else
            {
                await CompleteNormalInspectionHandshakeAsync().ConfigureAwait(false);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _inspectionStarted = false;      // ★ 新增：清除残留启动标志
                    _startSignalHandled = false;     // ★ 新增：允许下次 DT120 边沿触发
                    ClearTestItemsForRestart();
                    ForceRefreshReadyOrCanStartState();
                });
                AddLog("📝 操作员取消保存，检测结果已清空，可重新测试");
            }
        }
        finally
        {
            _isShowingSaveDialog = false;
        }

        // 恢复万用表为远程可控的 2 线电阻空闲态，等待下次检测
        await _multimeterDevice.PrepareIdleResistanceModeAsync().ConfigureAwait(false);
        _logger.LogInformation("[万用表收尾] 万用表已恢复为远程 2 线电阻空闲态");
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

    /// <summary>
    /// 复位收口后读取一次 PLC 输入快照，便于判断 DT120/DT121 是否真的释放。
    /// 诊断失败不影响复位主流程。
    /// </summary>
    private async Task LogPlcInputsAfterResetAsync()
    {
        try
        {
            var snapshot = await _plcDevice.ReadMachineInputsAsync(CancellationToken.None).ConfigureAwait(false);
            if (!snapshot.IsSuccess || snapshot.Value == null)
            {
                _logger.LogWarning("[复位流程][诊断] 复位后读取 PLC 输入快照失败：{Message}", snapshot.Message);
                return;
            }

            var inputs = snapshot.Value;
            _logger.LogWarning(
                "[复位流程][诊断] 复位后 PLC 快照：DT120={DT120}, DT121={DT121}, DT122={DT122}, DT123={DT123}, DT302={DT302}",
                inputs.IsStartRequested ? 1 : 0,
                inputs.IsResetRequested ? 1 : 0,
                inputs.IsStopRequested ? 1 : 0,
                inputs.IsEmergencyStop ? 1 : 0,
                inputs.IsRelayActionCompleted ? 1 : 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[复位流程][诊断] 复位后读取 PLC 输入快照异常");
        }
    }

    /// <summary>
    /// 正常完成后的 PLC 握手收口。
    /// 必须在用户处理保存/取消弹窗之后执行，不能在启动复核通过或检测刚完成时提前清 DT120。
    /// </summary>
    private async Task CompleteNormalInspectionHandshakeAsync()
    {
        _logger.LogWarning("[PLC动作][审计] 检测完成且保存弹窗已处理，开始清理本轮启动握手：DT234、DT120、DT130~DT185、DT302、DT304/DT305");

        await _plcDevice.ClearPcReadyAsync(CancellationToken.None).ConfigureAwait(false);
        await _plcDevice.ClearStartRequestAsync(CancellationToken.None).ConfigureAwait(false);
        await _plcDevice.ClearPinOutputsAsync(CancellationToken.None).ConfigureAwait(false);
        await _plcDevice.ClearRelayActionCompletedAsync(CancellationToken.None).ConfigureAwait(false);
        await _plcDevice.ClearFinalResultAsync(CancellationToken.None).ConfigureAwait(false);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _inspectionStarted = false;
            _startupCleared = true;
            IsPlcStartRequested = false;
        });

        _logger.LogWarning("[PLC动作][审计] 本轮正常完成收口结束：DT120/DT234/DT304/DT305 已清除，界面结果保留到复位");
    }

    #endregion

    #region 统一 PLC 输出清理

    /// <summary>
    /// 清当前运行输出：DT120、DT234、DT302、DT130~185、DT304/DT305。
    /// 停止、单项 NG、正常完成收口、紧急停止后调用。
    /// </summary>
    private async Task ClearCurrentRunOutputsAsync(CancellationToken ct = default)
    {
        await _plcDevice.ClearStartRequestAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearPcReadyAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearRelayActionCompletedAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearPinOutputsAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearFinalResultAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("[PLC清理][审计] 已清当前运行输出：DT120、DT234、DT302、DT130~185、DT304/DT305");
    }

    /// <summary>
    /// 清所有运行信号：DT120/DT121/DT122/DT123/DT234/DT302/DT303/DT130~185/DT304/DT305/DT306。
    /// 进入/离开页面、终了、复位时调用。
    /// 真实模式下跳过 DT122/DT123/DT306（PLC 只读信号）。
    /// </summary>
    private async Task ClearAllRunSignalsAsync(CancellationToken ct = default)
    {
        await _plcDevice.ClearStartRequestAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearResetRequestAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearPcReadyAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearRelayActionCompletedAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearPinOutputsAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearFinalResultAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearAlarmReleasedAsync(ct).ConfigureAwait(false);

        // Fake 模式下额外清除 PLC 只读信号
        if (_plcDevice is Devices.Fakes.FakeInspectionHardware fake)
        {
            await fake.WriteInputRegisterAsync(PlcAddressMap.StopSignal, 0, ct);
            await fake.WriteInputRegisterAsync(PlcAddressMap.EmergencyStopSignal, 0, ct);
            await fake.WriteInputRegisterAsync(PlcAddressMap.TerminateSignal, 0, ct);
        }
        else
        {
            // 真实模式下尝试清除（PLC 端可能允许上位机写 0）
            await _plcDevice.ClearStopRequestAsync(ct).ConfigureAwait(false);
            await _plcDevice.ClearEmergencyStopRequestAsync(ct).ConfigureAwait(false);
            await _plcDevice.ClearTerminateRequestAsync(ct).ConfigureAwait(false);
        }

        _logger.LogWarning("[PLC清理][审计] 已清所有运行信号");
    }

    #endregion

    #region 终了按钮

    [RelayCommand]
    private async Task FinishAndReturnAsync()
    {
        string confirmMsg = UiState switch
        {
            TestUIState.Testing => "正在测试中，确定要终止当前测试并返回主菜单吗？\n未完成的测试数据将丢失！",
            TestUIState.CompletedPass or TestUIState.CompletedFail => "有未保存的检测结果，返回将丢失本次所有数据，确定继续吗？",
            TestUIState.EmergencyStop => "急停中返回主菜单将丢失当前数据，确定继续吗？",
            _ => "确定要返回主菜单吗？"
        };

        var confirmed = await _notificationService.ConfirmAsync(confirmMsg, "确认返回");
        if (!confirmed) return;

        // ★ 标记终了流程进行中，防止 OnNavigatedFromAsync 重复清理 PLC
        _isFinishing = true;

        try
        {
            // 写 DT306=1 通知 PLC 终了
            _logger.LogWarning("[终了按钮][审计] 终了按钮触发，写 DT306=1");
            await _plcDevice.RequestTerminateAsync(CancellationToken.None).ConfigureAwait(false);
            AddLog("终了信号(DT306=1)已写入");

            // 停止检测引擎
            if (UiState == TestUIState.Testing)
            {
                _inspectionEngine?.Stop();
                AddLog("⚠️ 操作员终止了当前测试");
            }

            StopPlcPolling();

            // 清全部运行信号（PLC 清理只在这里执行一次）
            await ClearAllRunSignalsAsync(CancellationToken.None).ConfigureAwait(false);

            // 终了：万用表退出远程控制
            await _multimeterDevice.ReleaseToLocalAsync().ConfigureAwait(false);
            _logger.LogInformation("[万用表收尾] 终了按钮触发，万用表已退出远程控制");

            // ★ 核心修复：使用同步 Dispatcher.Invoke 确保导航全程在 UI 线程
            // Dispatcher.InvokeAsync 返回的 Task 在内部异常时仍会传播，
            // 而 NavigationService 内部 HandleCurrentViewLeavingAsync 访问
            // DependencyObject 必须在 UI 线程。同步 Invoke 阻塞等待完成，
            // 确保整个调用链（含 OnNavigatedFromAsync）都在 UI 线程执行。
            await Application.Current.Dispatcher.Invoke(async () =>
            {
                ResetToReadyState();
                await _navigationService.NavigateToAsync<MainMenuView>();
            });

            // 返回主菜单后延时 1 秒清 DT306
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await _plcDevice.ClearTerminateRequestAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogWarning("[终了按钮][审计] 延时 1 秒后已清除 DT306=0");
            });
        }
        finally
        {
            _isFinishing = false;
        }
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
            if (!result.IsSuccess || result.Value == null)
            {
                _logger.LogWarning("[PLC轮询][诊断] 读取 PLC 输入失败：{Message}", result.Message);
                return;
            }

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
        // 真实模式下工人按实体按钮 → PLC DT121=1 → 轮询捕获 → 执行复位
        // ════════════════════════════════════════════════════════
        if (inputs.IsResetRequested)
        {
            if (_resetSignalHandled)
                return;

            AddLog("PLC 复位信号(DT121)，正在执行上位机复位操作...");
            _logger.LogWarning("[PLC轮询][审计] 检测到 DT121=1，执行复位流程");

            await ExecuteResetFlowAsync();
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
                    _inspectionEngine.StopForEmergencyStop();
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
                await ExecuteStopFlowAsync();
            }
            return;
        }

        // ════════════════════════════════════════════════════════
        // DT120 复位后释放保护：复位后即使 PLC 下一拍仍返回 DT120=1，
        // 也不进入 HandlePlcStartRequest()，避免残留启动信号重新触发检测。
        // 读到 DT120=0 后自动解除保护。
        // ════════════════════════════════════════════════════════
        if (_waitDt120ReleaseAfterReset)
        {
            if (inputs.IsStartRequested)
            {
                _logger.LogWarning("[复位流程][审计] 复位后仍读到 DT120=1，已忽略本次启动请求并再次尝试清 DT120");
                _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
                UpdateUIState();
                return;
            }

            _waitDt120ReleaseAfterReset = false;
            _logger.LogWarning("[复位流程][审计] 已确认 DT120=0，复位后的启动保护解除");
        }

        // ════════════════════════════════════════════════════════
        // DT120 边沿检测：上升沿（0→1）允许启动，电平保持不重复触发。
        // DT120=0 时重置处理标志，DT120=1 且未处理且未启动时才进入启动复核。
        // ════════════════════════════════════════════════════════
        if (!inputs.IsStartRequested)
        {
            _startSignalHandled = false;
        }

        if (inputs.IsStartRequested && !_startSignalHandled && !_inspectionStarted)
        {
            _startSignalHandled = true;
            await HandlePlcStartRequest();
        }

        // ════════════════════════════════════════════════════════
        // 更新普通待机/可启用状态
        // ════════════════════════════════════════════════════════
        UpdateUIState();
    }

    /// <summary>
    /// 处理 PLC DT120=1 启动请求。
    /// 复核所有启动条件，通过后先验证万用表通信，再写 DT234=1，最后调用 RunInspectionAsync。
    /// </summary>
    private async Task HandlePlcStartRequest()
    {
        _logger.LogWarning("[PLC轮询][审计] 收到 PLC 启动请求(DT120=1)");

        // 启动复核
        string? rejectReason = ValidateStartConditions();
        if (rejectReason != null)
        {
            _logger.LogWarning("[启动复核][拒绝] {Reason}", rejectReason);
            _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
            _startupCleared = true;
            AddLog($"❌ {rejectReason}");
            _ = _notificationService.ShowWarningAsync(rejectReason, "启动拒绝");
            return;
        }

        // ★ 新增：复核通过后验证万用表是否真正可通信（500ms 超时，不阻塞 UI 轮询）
        bool dmmPingOk = await _multimeterDevice.PingAsync(CancellationToken.None).ConfigureAwait(false);
        if (!dmmPingOk)
        {
            _logger.LogWarning("[启动复核][拒绝] 万用表通信验证失败（*IDN? 无响应），已清除 DT120");
            await _plcDevice.ClearStartRequestAsync(CancellationToken.None).ConfigureAwait(false);
            _startupCleared = true;
            AddLog("❌ 启动失败：万用表无法通信，请检查网络连接后重试");
            _ = _notificationService.ShowWarningAsync("万用表无法通信，请检查网络连接后重试。", "启动拒绝");
            return;
        }

        // 复核通过后先写 DT234=1，通知 PLC 上位机已就绪
        var pcReadyResult = await _plcDevice.WritePcReadyAsync(CancellationToken.None).ConfigureAwait(false);
        if (!pcReadyResult.IsSuccess)
        {
            _logger.LogWarning("[启动复核][拒绝] 写 DT234=1 失败：{Message}，已清除 DT120", pcReadyResult.Message);
            _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
            _startupCleared = true;
            AddLog($"❌ 启动失败：写 DT234=1 失败 - {pcReadyResult.Message}");
            _ = _notificationService.ShowWarningAsync($"写 DT234 失败：{pcReadyResult.Message}", "启动拒绝");
            return;
        }

        _logger.LogWarning("[启动复核][通过] 所有条件满足，万用表通信正常，DT234=1 已写入，开始检测");
        _inspectionStarted = true;
        _startSignalHandled = true;
        _ignoreInspectionCallbacksUntilNextStart = false;
        _isResetting = false;
        _waitDt120ReleaseAfterReset = false;
        Interlocked.Exchange(ref _inspectionCompletedHandled, 0);

        // 锁定当前机种、方案、作业员
        string lockedModel = ModelName;
        string lockedBarcode = SerialNumber;
        string lockedOperator = OperatorName;

        _ = RunInspectionAsync(lockedBarcode, lockedModel, lockedOperator);
    }

    #region 统一复位流程

    /// <summary>
    /// 执行上位机完整复位流程（三种模式统一收口）。
    /// 供以下路径复用：
    ///   1. 半实物联调按钮（TriggerPlcResetAsync）— 写 DT121=1 读回确认后主动执行
    ///   2. Fake 调试按钮（FakeTriggerResetAsync）— 写 DT121=1 读回确认后主动执行
    ///   3. PLC 轮询捕获 DT121=1（真实实体按钮触发）
    ///
    /// 执行顺序（12步）：
    ///   1. 锁门 — 屏蔽引擎回调
    ///   2. 停止检测引擎（如果正在运行）
    ///   3. 清 DT120（启动请求）
    ///   4. 清 DT304/DT305（产品综合结果）
    ///   5. 清 DT130~DT185（引脚输出区）
    ///   6. 清 DT302（继电器动作完成）
    ///   7. 清 DT234（上位机允许开始）
    ///   8. Fake 模式下额外清 DT122/DT123
    ///   9. 清空界面检测项目结果
    ///   10. 刷新 Dispatcher 队列（确保晚到的引擎回调全部执行完）
    ///   11. 重置内部状态标志（含检测引擎断点 _inspectionEngine.ClearResetState）
    ///   12. 清除 DT121（握手完成信号，最后清）
    ///   13. 复位后诊断快照
    ///   14. 解除保护，刷新 UI 到可启动/待机
    /// </summary>
    private async Task ExecuteResetFlowAsync()
    {
        // 防重入：已在复位流程中则跳过
        if (_resetSignalHandled)
            return;

        // 防重入：多入口同时调用时跳过
        if (_resetFlowInProgress)
        {
            _logger.LogWarning("[复位流程][审计] 复位流程正在执行中，忽略本次重复复位请求");
            return;
        }

        _resetSignalHandled = true;
        _resetFlowInProgress = true;

        // ── 步骤1：立即锁门，屏蔽所有待处理的引擎回调 ──
        _ignoreInspectionCallbacksUntilNextStart = true;
        _isResetting = true;
        _waitDt120ReleaseAfterReset = true;
        Interlocked.Increment(ref _inspectionRunVersion);
        Interlocked.Exchange(ref _inspectionCompletedHandled, 0);  // ★ 新增：复位时重置原子标志

        UiState = TestUIState.Resetting;
        SensorStatusText = "复位中";
        AddLog("正在执行上位机复位操作...");

        try
        {
            // ── 步骤2：停止检测引擎（如果正在运行），等待引擎安全退出 ──
            if (_inspectionEngine!.IsRunning)
            {
                await _inspectionEngine.StopAndWaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                _logger.LogInformation("[复位流程] 已停止检测引擎");
            }

            // ── 步骤3：清除 DT120（启动请求），防止复位后残留启动信号重新触发检测 ──
            await _plcDevice.ClearStartRequestAsync(CancellationToken.None);
            _logger.LogWarning("[复位流程][审计] 已清除 DT120（启动请求），防止复位后残留启动信号重新触发检测");

            // ── 步骤4：清空产品综合结果 DT304/DT305 ──
            await _plcDevice.ClearFinalResultAsync(CancellationToken.None);
            _logger.LogWarning("[复位流程][审计] 已清除 DT304/DT305（复位清空产品结果）");

            // ── 步骤5：清空 PLC 引脚输出区 DT130~DT185 ──
            await _plcDevice.ClearPinOutputsAsync(CancellationToken.None);
            _logger.LogInformation("[复位流程] 已清空 PLC 引脚输出区 DT130~DT185");

            // ── 步骤6：清除 DT302（继电器动作完成）──
            await _plcDevice.ClearRelayActionCompletedAsync(CancellationToken.None);
            _logger.LogInformation("[复位流程] 已清除 DT302（继电器动作完成标志）");

            // ── 步骤7：清除 DT234（上位机允许开始检测）──
            await _plcDevice.ClearPcReadyAsync(CancellationToken.None);
            _logger.LogInformation("[复位流程] 已清除 DT234（上位机允许开始检测）");

            // ── 步骤8：Fake 模式下额外清除停止/急停信号 ──
            // 真实 PLC 模式下此分支不执行，PLC 自行管理 DT122/DT123
            if (_plcDevice is Devices.Fakes.FakeInspectionHardware fake)
            {
                await fake.WriteInputRegisterAsync(PlcAddressMap.StopSignal, 0, CancellationToken.None);
                await fake.WriteInputRegisterAsync(PlcAddressMap.EmergencyStopSignal, 0, CancellationToken.None);
                _logger.LogInformation("[复位流程][Fake] 已清除 DT122(停止) 和 DT123(急停)");
            }

            // ── 步骤9：清空界面检测项目结果 ──
            ClearTestItemsForRestart();
            _logger.LogInformation("[复位流程] 已清空界面检测项目结果");

            // ── 步骤10：强制刷新 Dispatcher 队列 ──
            // 确保引擎晚到的回调（OnStepCompleted 等）全部执行完毕。
            // 由于 _isResetting=true，这些回调会被忽略，不会污染已清空的 TestItems。
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background);

            // ── 步骤11：重置上位机内部状态标志 ──
            _inspectionStarted = false;
            _startupCleared = true;
            _startSignalHandled = true;
            // 注意：_ignoreInspectionCallbacksUntilNextStart 保留为 true，
            // 直到下一次合法启动通过 HandlePlcStartRequest 再恢复。
            // 这样复位完成到下一次启动之间，所有旧检测晚到回调都被丢弃。
            _isShowingEmergencyDialog = false;
            _emergencyDialogAcknowledged = false;
            _emergencyStopDialogVM?.StopPolling();
            _emergencyStopDialogVM = null;
            IsPlcStartRequested = false;
            _inspectionEngine.ClearResetState();
            _logger.LogInformation("[复位流程] 已重置内部状态标志");

            // ── 步骤12：【关键】清除 DT121 ──
            // 真实 PLC 侧 DT121 由工人实体按钮触发后保持，上位机复位完成后清零。
            // Fake 模式下清除字典中的值，防止轮询重复触发。
            await _plcDevice.ClearResetRequestAsync(CancellationToken.None);
            _logger.LogInformation("[复位流程] 上位机复位操作全部完成，已清除 DT121 复位信号");

            // ── 步骤13：复位后诊断快照 ──
            await LogPlcInputsAfterResetAsync().ConfigureAwait(false);

            // ── 步骤14：解除保护状态，强制刷新 UI 到最终态 ──
            _isResetting = false;
            _resetFlowInProgress = false;
            ForceRefreshReadyOrCanStartState();
            AddLog("✅ 复位完成，已清空所有检测结果，可重新开始测试");
            await _notificationService.ShowInfoAsync(
                "复位完成，已清空所有检测结果，可重新开始测试。",
                "复位完成");
        }
        catch (Exception ex)
        {
            // 异常时解除保护，允许下次重试
            _logger.LogError(ex, "[复位流程] 复位过程发生异常");
            _isResetting = false;
            _resetSignalHandled = false;
            _resetFlowInProgress = false;
            UiState = TestUIState.Error;
            SensorStatusText = "异常";
            AddLog($"❌ 复位异常: {ex.Message}");
            throw;
        }
    }

    #endregion

    #region 停止流程

    /// <summary>
    /// 执行完整停止流程（DT122 停止）。
    /// 停止检测、保留已测显示、清断点、清 PLC 输出、清 DT122。
    /// </summary>
    private async Task ExecuteStopFlowAsync()
    {
        if (_stopFlowInProgress)
        {
            _logger.LogWarning("[停止流程][审计] 停止流程正在执行中，忽略本次重复");
            return;
        }

        _stopFlowInProgress = true;
        try
        {
            UiState = TestUIState.Paused;
            SensorStatusText = "已停止";
            AddLog("[停止流程] 收到停止信号(DT122)，正在停止检测...");
            _logger.LogWarning("[停止流程][审计] DT122 停止信号，执行停止收口");

            // 停止检测引擎
            if (_inspectionEngine!.IsRunning)
            {
                await _inspectionEngine.StopAndWaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }

            // 清断点
            _inspectionEngine.ClearResetState();

            // 清 PLC 输出
            await ClearCurrentRunOutputsAsync(CancellationToken.None).ConfigureAwait(false);

            // 清 DT122
            await _plcDevice.ClearStopRequestAsync(CancellationToken.None);

            UiState = TestUIState.Paused;
            SensorStatusText = "已停止";
            IsPlcStartRequested = false;
            AddLog("[停止流程] 已停止，已保留已测结果显示，等待复位后重新从第一项开始");
            _logger.LogWarning("[停止流程][审计] 停止收口完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[停止流程] 停止过程发生异常");
        }
        finally
        {
            _stopFlowInProgress = false;
        }
    }

    #endregion

    #region 急停流程

    /// <summary>
    /// 执行急停流程（DT123 急停）。
    /// 停止检测、清 PLC 输出、清断点、弹急停窗。
    /// </summary>
    private async Task ExecuteEmergencyStopFlowAsync()
    {
        _logger.LogWarning("[急停流程][审计] 急停信号 DT123，执行急停收口");
        AddLog("[急停流程] 急停信号，正在停止检测...");

        try
        {
            // 停止当前检测
            if (_inspectionEngine!.IsRunning)
                _inspectionEngine.StopForEmergencyStop();

            // 清 PLC 输出
            await _plcDevice.ClearStartRequestAsync(CancellationToken.None);
            await _plcDevice.ClearPcReadyAsync(CancellationToken.None);
            await _plcDevice.ClearRelayActionCompletedAsync(CancellationToken.None);
            await _plcDevice.ClearPinOutputsAsync(CancellationToken.None);
            await _plcDevice.ClearFinalResultAsync(CancellationToken.None);

            // 清断点
            _inspectionEngine.ClearResetState();

            UiState = TestUIState.EmergencyStop;
            SensorStatusText = "急停中";
            IsPlcStartRequested = false;

            // 弹急停窗（如果尚未弹出）
            if (!_isShowingEmergencyDialog && !_emergencyDialogAcknowledged)
            {
                _isShowingEmergencyDialog = true;
                ShowEmergencyStopDialog();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[急停流程] 急停过程发生异常");
        }
    }

    #endregion

    /// <summary>
    /// 弹出急停模态弹窗。
    /// 弹窗独立轮询 DT303，关闭后页面保持急停保护状态。
    /// </summary>
    private void ShowEmergencyStopDialog()
    {
        bool isFake = _plcDevice is Devices.Fakes.FakeInspectionHardware;
        bool canSimulateAlarmRelease = isFake || IsSemiPhysicalDebugMode;

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
            canSimulateAlarmRelease: canSimulateAlarmRelease);

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
    /// 补齐方案项目、引脚合法性、极性、阈值等校验。
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

        // 最新 PLC 快照未读到复位/停止/急停信号
        if (_lastPlcInputs != null)
        {
            if (_lastPlcInputs.IsResetRequested)
                return "PLC 复位信号(DT121)未释放，无法启动";
            if (_lastPlcInputs.IsStopRequested)
                return "PLC 停止信号(DT122)未释放，无法启动";
            if (_lastPlcInputs.IsEmergencyStop)
                return "PLC 急停信号(DT123)未释放，无法启动";
        }

        // 方案项目列表非空
        if (TestItems.Count == 0)
            return "当前方案没有可检测项目，无法启动";

        // 每项引脚合法性校验
        if (_inspectionEngine != null)
        {
            var config = _inspectionEngine.Config;
            if (config.TestPoints.Count == 0)
                return "检测配置无测试点，无法启动";

            for (int i = 0; i < config.TestPoints.Count; i++)
            {
                var tp = config.TestPoints[i];
                if (string.IsNullOrWhiteSpace(tp.PinLeft))
                    return $"第 {i + 1} 项 [{tp.Name}] 左引脚为空，无法启动";
                if (string.IsNullOrWhiteSpace(tp.PinRight))
                    return $"第 {i + 1} 项 [{tp.Name}] 右引脚为空，无法启动";

                // 引脚能被 PlcAddressMap 识别
                try
                {
                    PlcAddressMap.GetPinAddresses(tp.PinLeft);
                    PlcAddressMap.GetPinAddresses(tp.PinRight);
                }
                catch (ArgumentException ex)
                {
                    return $"第 {i + 1} 项 [{tp.Name}] 引脚无效：{ex.Message}";
                }

                // 左右引脚不相同
                if (string.Equals(tp.PinLeft, tp.PinRight, StringComparison.OrdinalIgnoreCase))
                    return $"第 {i + 1} 项 [{tp.Name}] 左右引脚相同({tp.PinLeft})，无法启动";

                // 左右极性必须一正一负
                bool leftIsPositive = string.Equals(tp.PinLeftPolarity, PinPolarityConstants.Positive, StringComparison.OrdinalIgnoreCase);
                bool rightIsNegative = string.Equals(tp.PinRightPolarity, PinPolarityConstants.Negative, StringComparison.OrdinalIgnoreCase);
                if (!leftIsPositive || !rightIsNegative)
                    return $"第 {i + 1} 项 [{tp.Name}] 极性必须左正右负，当前左={tp.PinLeftPolarity} 右={tp.PinRightPolarity}";

                // 电阻模式校验
                if (string.Equals(tp.CheckMode, CheckModeConstants.Resistance, StringComparison.OrdinalIgnoreCase))
                {
                    if (!tp.LowerLimit.HasValue)
                        return $"第 {i + 1} 项 [{tp.Name}] 电阻模式下限为空，无法启动";
                    if (!tp.UpperLimit.HasValue)
                        return $"第 {i + 1} 项 [{tp.Name}] 电阻模式上限为空，无法启动";
                    if (InputValidationHelper.ValidateResistanceValue(tp.LowerLimit.Value) != null)
                        return $"第 {i + 1} 项 [{tp.Name}] 电阻模式下限({tp.LowerLimit})无效，无法启动";
                    if (InputValidationHelper.ValidateResistanceValue(tp.UpperLimit.Value) != null)
                        return $"第 {i + 1} 项 [{tp.Name}] 电阻模式上限({tp.UpperLimit})无效，无法启动";
                    if (tp.LowerLimit.Value > tp.UpperLimit.Value)
                        return $"第 {i + 1} 项 [{tp.Name}] 电阻模式下限({tp.LowerLimit})大于上限({tp.UpperLimit})，无法启动";
                }

                // 导通模式校验
                if (string.Equals(tp.CheckMode, CheckModeConstants.Continuity, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(tp.ModeValue, "OPEN", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(tp.ModeValue, "SHORT", StringComparison.OrdinalIgnoreCase))
                        return $"第 {i + 1} 项 [{tp.Name}] 导通模式期望值无效(期望 OPEN 或 SHORT)，当前={tp.ModeValue}";
                }
            }
        }

        return null;
    }

    #region 统一调试命令（Fake 和半实物共用）

    /// <summary>
    /// 调试启动：写 DT120=1，走 PLC 轮询启动复核链路，不直接 RunInspectionAsync。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugStartAsync()
    {
        if (_inspectionEngine == null) return;

        if (UiState == TestUIState.EmergencyStop)
        {
            await _notificationService.ShowWarningAsync("急停状态中，请先复位后再启动检测。", "启动拒绝");
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelName) || string.IsNullOrWhiteSpace(SerialNumber)
            || string.IsNullOrWhiteSpace(SchemeName) || IsSchemeNameInvalid
            || string.IsNullOrWhiteSpace(OperatorName))
        {
            await _notificationService.ShowWarningAsync("机种、序列号、方案、作业员必须完整后才能启动。", "启动拒绝");
            return;
        }

        if (TestItems.Count == 0)
            await LoadPlanItemsAsync();

        if (TestItems.Count == 0)
        {
            await _notificationService.ShowWarningAsync("当前方案没有可检测项目。", "启动拒绝");
            return;
        }

        _logger.LogWarning("[调试面板][审计] 上位机写入 DT120=1，通过 PLC 轮询进入启动复核");
        AddLog("[调试面板] 写入 DT120=1，等待 PLC 轮询进入启动复核...");

        var result = await _plcDevice.RequestStartAsync(CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            AddLog($"[调试面板] 写入 DT120=1 失败：{result.Message}");
        }
    }

    /// <summary>
    /// 调试停止：写 DT122=1，成功后执行停止收口流程。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugStopAsync()
    {
        _logger.LogWarning("[调试面板][审计] 上位机写入 DT122=1（停止信号）");
        AddLog("[调试面板] 写入 DT122=1...");

        var result = await _plcDevice.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            AddLog($"[调试面板] 写入 DT122=1 失败：{result.Message}");
            return;
        }

        await ExecuteStopFlowAsync();
    }

    /// <summary>
    /// 调试复位：写 DT121=1，写成功后直接执行复位收口（不再读回确认）。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugResetAsync()
    {
        _logger.LogWarning("[调试面板][审计] 上位机写入 DT121=1（复位信号）");
        AddLog("[调试面板] 写入 DT121=1...");

        var result = await _plcDevice.RequestResetAsync(CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            AddLog($"[调试面板] 写入 DT121=1 失败：{result.Message}");
            await _notificationService.ShowWarningAsync("复位失败：写入 DT121 失败", "复位失败");
            return;
        }

        await ExecuteResetFlowAsync();
    }

    /// <summary>
    /// 调试急停：写 DT123=1，成功后执行急停收口。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugEmergencyStopAsync()
    {
        _logger.LogWarning("[调试面板][审计] 上位机写入 DT123=1（急停信号）");
        AddLog("[调试面板] 写入 DT123=1...");

        var result = await _plcDevice.RequestEmergencyStopAsync(CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            AddLog($"[调试面板] 写入 DT123=1 失败：{result.Message}");
            return;
        }

        await ExecuteEmergencyStopFlowAsync();
    }

    #endregion

    /// <summary>
    /// 半实物/真实模式复位按钮（写 DT121=1，写后直接执行复位，不再读回确认）。
    /// 真实模式下工人按实体按钮时由 PLC 轮询触发 ExecuteResetFlowAsync，
    /// 上位机按钮作为备用入口。
    /// </summary>
    [RelayCommand]
    private async Task TriggerPlcResetAsync()
    {
        var result = await _plcDevice.RequestResetAsync(CancellationToken.None).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            AddLog($"[复位] 写入 DT121=1 失败：{result.Message}");
            _logger.LogWarning("[复位诊断] 写入 DT121=1 失败：{Message}", result.Message);
            return;
        }

        _logger.LogWarning("[复位诊断] 已写入 DT121=1");
        AddLog("[复位] 已写入 DT121=1，执行上位机复位...");

        await ExecuteResetFlowAsync();
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

        int runVersion = Interlocked.Increment(ref _inspectionRunVersion);

        var result = await Task.Run(async () =>
        {
            return await _inspectionEngine.RunInspectionAsync(
                barcode, modelName, operatorName, 0, CancellationToken.None);
        }).ConfigureAwait(false);

        // 复位后返回的旧检测任务直接丢弃，不再处理结果和弹提示
        if (runVersion != _inspectionRunVersion || _ignoreInspectionCallbacksUntilNextStart)
        {
            _logger.LogWarning("[复位流程][审计] 旧检测任务已返回但被丢弃，RunVersion={RunVersion}, CurrentRunVersion={CurrentVersion}",
                runVersion, _inspectionRunVersion);
            return;
        }

        if (result.IsAborted || !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            bool isPlcSignalAbort = result.StopReason == InspectionStopReason.Reset
                                    || result.StopReason == InspectionStopReason.PlcStop
                                    || result.StopReason == InspectionStopReason.EmergencyStop;
            if (isPlcSignalAbort)
            {
                _logger.LogWarning("[运行页][审计] 检测因 {StopReason} 收口，中止提示交由对应流程处理", result.StopReason);
                return;
            }

            string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "检测已中止，请查看运行日志。"
                : $"检测中止：{result.ErrorMessage}";

            _logger.LogWarning("[运行页][审计] {Message}", message);
            await _notificationService.ShowWarningAsync(message, "检测中止").ConfigureAwait(false);
        }
    }

    #endregion

    #region 硬件事件订阅

    private void SubscribeToHardwareEvents()
    {
        if (_hardwareEventsSubscribed)
            return;

        _deviceManager.PlcConnectionStateChanged += OnPlcConnectionStateChanged;
        _deviceManager.DmmConnectionStateChanged += OnDmmConnectionStateChanged;
        _deviceManager.ScannerConnectionStateChanged += OnScannerConnectionStateChanged;
        _deviceManager.BarcodeScanned += OnScannerBarcodeParsed;

        if (_inspectionEngine != null)
        {
            _inspectionEngine.StateChanged += OnInspectionStateChanged;
            _inspectionEngine.StepStarted += OnStepStarted;
            _inspectionEngine.StepCompleted += OnStepCompleted;
            _inspectionEngine.InspectionCompleted += OnInspectionCompleted;
            _inspectionEngine.LogMessage += OnInspectionLogMessage;
        }

        _hardwareEventsSubscribed = true;
    }

    private void UnsubscribeFromHardwareEvents()
    {
        if (!_hardwareEventsSubscribed)
            return;

        _deviceManager.PlcConnectionStateChanged -= OnPlcConnectionStateChanged;
        _deviceManager.DmmConnectionStateChanged -= OnDmmConnectionStateChanged;
        _deviceManager.ScannerConnectionStateChanged -= OnScannerConnectionStateChanged;
        _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;

        if (_inspectionEngine != null)
        {
            _inspectionEngine.StateChanged -= OnInspectionStateChanged;
            _inspectionEngine.StepStarted -= OnStepStarted;
            _inspectionEngine.StepCompleted -= OnStepCompleted;
            _inspectionEngine.InspectionCompleted -= OnInspectionCompleted;
            _inspectionEngine.LogMessage -= OnInspectionLogMessage;
        }

        _hardwareEventsSubscribed = false;
        _logger.LogInformation("[运行页收尾] 已退订硬件和检测引擎事件，避免旧页面重复响应完成事件");
    }

    private void OnPlcConnectionStateChanged(object? sender, DeviceConnectionStateChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsPlcConnected = e.IsConnected;
            PlcStatusText = e.StatusText;
            UpdateUIState();
        });
    }

    private void OnDmmConnectionStateChanged(object? sender, DeviceConnectionStateChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsDmmConnected = e.IsConnected;
            DmmStatusText = e.StatusText;
            UpdateUIState();
        });
    }

    private void OnScannerConnectionStateChanged(object? sender, DeviceConnectionStateChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsScannerConnected = e.IsConnected;
            ScannerStatusText = e.StatusText;
            UpdateUIState();
        });
    }

    private void OnScannerBarcodeParsed(object? sender, BarcodeParsedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (UiState == TestUIState.Testing
                || UiState == TestUIState.CompletedPass
                || UiState == TestUIState.CompletedFail)
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
            // 复位或忽略回调期间丢弃旧检测引擎的状态变更，防止旧 Aborted 覆盖复位后的 UI
            if (_ignoreInspectionCallbacksUntilNextStart || _isResetting || _stopFlowInProgress)
            {
                _logger.LogDebug("[运行页] 已忽略收口期间检测引擎状态回调：{OldState} -> {NewState}",
                    e.OldState, e.NewState);
                return;
            }

            UiState = e.NewState switch
            {
                InspectionState.Testing => TestUIState.Testing,
                InspectionState.CompletedPass => TestUIState.CompletedPass,
                InspectionState.CompletedFail => TestUIState.CompletedFail,
                InspectionState.PausedByStop => TestUIState.Paused,
                InspectionState.PausedByEmergencyStop => TestUIState.EmergencyStop,
                InspectionState.StoppedBySingleItemNg => TestUIState.SingleItemNgStopped,
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
                InspectionState.CompletedPass => "OK",
                InspectionState.CompletedFail => "NG",
                InspectionState.PausedByStop => "已停止",
                InspectionState.PausedByEmergencyStop => "急停中",
                InspectionState.StoppedBySingleItemNg => "NG",
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
                  && UiState != TestUIState.CompletedPass
                  && UiState != TestUIState.CompletedFail
                  && UiState != TestUIState.EmergencyStop
                  && UiState != TestUIState.Paused
                  && UiState != TestUIState.SingleItemNgStopped);
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
        // DisplayTextOverride 优先：异常值直接显示 "NG"，避免超长数字进入格式化
        if (!string.IsNullOrWhiteSpace(measurement.DisplayTextOverride))
            return measurement.DisplayTextOverride;

        if (!measurement.IsValid)
            return "测量失败";

        double value = measurement.Value;

        if (testPoint.CheckMode == CheckModeConstants.Continuity)
        {
            if (!string.IsNullOrWhiteSpace(testPoint.ActualContinuityState))
                return testPoint.ActualContinuityState;

            return InspectionEngine.ResolveContinuityState(value, testPoint.ContinuityThresholdOhm);
        }

        return $"{InputValidationHelper.FormatResistanceValue(value)} Ω";
    }

    /// <summary>
    /// 全部检测完成回调。
    /// 使用 Interlocked.Exchange 原子抢占，确保 InspectionCompleted 事件只被处理一次，
    /// 从根本上杜绝引擎多次触发导致重复弹出保存对话框。
    /// </summary>
    private void OnInspectionCompleted(object? sender, InspectionCompletedEventArgs e)
    {
        // ★ 原子抢占：只有第一个到达的线程能拿到 0 并置为 1
        // 后续到达的线程拿到 1 直接返回，不进入 InvokeAsync 排队
        if (Interlocked.Exchange(ref _inspectionCompletedHandled, 1) == 1)
        {
            _logger.LogWarning("[运行页] 检测完成回调重复触发（已被原子拦截），已忽略");
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (_ignoreInspectionCallbacksUntilNextStart || _isResetting)
                    return;

                if (e.Result.IsAborted)
                {
                    AddLog($"⚠️ 检测中止: {e.Result.ErrorMessage}");

                    bool isPlcSignalAbort = e.Result.StopReason == InspectionStopReason.Reset
                                            || e.Result.StopReason == InspectionStopReason.PlcStop
                                            || e.Result.StopReason == InspectionStopReason.EmergencyStop;

                    if (isPlcSignalAbort)
                    {
                        _logger.LogWarning("[万用表收尾] 检测因 {StopReason} 中止，保持万用表远程控制模式，等待后续操作",
                            e.Result.StopReason);
                    }
                    else
                    {
                        await _multimeterDevice.ReleaseToLocalAsync().ConfigureAwait(false);
                        _logger.LogWarning("[万用表收尾] 检测异常中止，万用表已退出远程控制");
                    }
                    return;
                }

                if (e.Result.StopReason == InspectionStopReason.SingleItemNg)
                {
                    AddLog($"❎ {e.Result.ErrorMessage}");
                    _logger.LogWarning("[运行页][审计] 单项 NG 停止: {ErrorMessage}", e.Result.ErrorMessage);
                    return;
                }

                var msg = e.Result.IsAllPassed
                    ? $"✅ 检测完成: 良品 (耗时{e.Result.Duration.TotalSeconds:F1}s)"
                    : $"❌ 检测完成: 不良 (耗时{e.Result.Duration.TotalSeconds:F1}s)";
                AddLog(msg);

                await OnAllPinsTestedAsync();
            }
            finally
            {
                // ★ 无论正常完成还是异常，都要重置原子标志
                Interlocked.Exchange(ref _inspectionCompletedHandled, 0);
            }
        });
    }

    /// <summary>
    /// 检测引擎日志订阅。
    /// 复位或忽略回调期间不输出旧检测日志，避免旧任务晚到的日志出现在已清空的日志区。
    /// </summary>
    private void OnInspectionLogMessage(object? sender, string message)
    {
        if (_ignoreInspectionCallbacksUntilNextStart || _isResetting)
        {
            _logger.LogDebug("[复位流程] 已忽略旧检测日志：{Message}", message);
            return;
        }

        AddLog(message);
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

        // 重新进入运行页时恢复硬件和检测引擎事件订阅。
        SubscribeToHardwareEvents();

        // 同步设备连接状态
        SyncDeviceStates();

        // ★ 进入运行界面时，清除上一轮残留的全部 PLC 信号
        // 防止上一次异常退出（急停、断电、终了等）后信号残留，
        // 导致本轮页面初始化后误触发启动、复位、停止等流程
        try
        {
            await ClearAllRunSignalsAsync(CancellationToken.None);
            _logger.LogInformation("[运行页初始化] 已主动清除所有残留 PLC 信号");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[运行页初始化] 清除残留 PLC 信号异常，如后续误触发请检查 PLC 状态");
        }

        // ★ 启动 PLC 轮询替代传感器模拟
        StartPlcPolling();
        AddLog("正在等待 PLC 启动信号...");
    }

    public async Task OnNavigatedFromAsync()
    {
        _logger.LogInformation("离开运行界面");
        StopPlcPolling();
        UnsubscribeFromHardwareEvents();

        // ★ 终了流程已提前完成 PLC 清理，此处只做非 PLC 的页面离开收尾
        if (_isFinishing)
        {
            _logger.LogInformation("[运行页收尾] 终了流程已清理 PLC，跳过重复清理");
            return;
        }

        // 正常离开运行页（非终了场景）：清运行信号
        await ClearAllRunSignalsAsync(CancellationToken.None).ConfigureAwait(false);

        // 离开运行页：万用表退出远程控制
        await _multimeterDevice.ReleaseToLocalAsync().ConfigureAwait(false);
        _logger.LogInformation("[万用表收尾] 离开运行界面，万用表已退出远程控制");
    }

    public Task<bool> CanNavigateFromAsync()
    {
        if (UiState == TestUIState.Testing
            || UiState == TestUIState.CompletedPass
            || UiState == TestUIState.CompletedFail)
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
        UnsubscribeFromHardwareEvents();

        GC.SuppressFinalize(this);
    }

    #endregion
}
