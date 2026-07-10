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
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.Inspection;
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
/// ResetFailed:    复位失败 — 引擎超时未退出，待操作员重试
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
    AwaitingReset,
    EmergencyStop,
    Resetting,
    ResetFailed,
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

    /// <summary>PLC 轮询非阻塞门禁。上一轮未结束时跳过本轮，避免控制信号快照排队。</summary>
    private int _plcPollingInProgress;

    /// <summary>PLC 轮询暂停标志。控制动作（Stop/Reset/EmergencyStop）执行期间为 true，暂停低优先级 UI 轮询。</summary>
    private bool _plcPollingSuspended;

    /// <summary>当前轮询操作的取消令牌源。供控制动作取消正在进行的 Poll 请求。</summary>
    private CancellationTokenSource? _plcPollingOperationCts;

    /// <summary>Start/Stop/Reset/EmergencyStop/Finish 统一动作门禁。</summary>
    private readonly SemaphoreSlim _controlActionLock = new(1, 1);

    /// <summary>当前正在执行的控制动作。它不是 UI 状态，只用于跨动作互斥和日志诊断。</summary>
    private InspectionControlAction _currentControlAction = InspectionControlAction.None;

    /// <summary>轮询中上一次 PLC 输入快照，用于检测信号变化</summary>
    private PlcControlSignals? _lastPlcInputs;

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
    /// 正在执行复位流程中。在 OnStepStarted/OnStepCompleted 中额外守卫，
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
    /// Starting 流程的取消令牌源。Stop/Reset/EmergencyStop 可取消 Starting 以立即执行自身流程。</summary>
    private CancellationTokenSource? _startingCts;

    /// <summary>
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

    /// <summary>
    /// Reset Timeout 错误弹窗是否已显示（0=未显示，1=已显示）。
    /// 使用 Interlocked.Exchange 防重，DT121=0 后重置为 0。
    /// </summary>
    private int _resetFailureNotificationShown;

    /// <summary>
    /// 启动请求决策枚举。在完整启动校验前，先按当前状态做分类。
    /// </summary>
    private enum StartRequestDecision
    {
        /// <summary>继续执行完整启动校验</summary>
        ContinueValidation,
        /// <summary>检测中/启动中，静默忽略</summary>
        IgnoreDuplicate,
        /// <summary>需要先复位</summary>
        RejectNeedReset,
        /// <summary>急停状态</summary>
        RejectEmergencyStop,
        /// <summary>当前忙（如正在复位），拒绝启动</summary>
        RejectBusy
    }

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
    /// 统一切换运行页状态。只同步 UI 状态、状态灯文字和输入可用性，不做 PLC 通信或业务动作。
    /// </summary>
    private void SetUiState(TestUIState state)
    {
        var oldState = UiState;
        UiState = state;
        SensorStatusText = state switch
        {
            TestUIState.Ready => "待机中",
            TestUIState.CanStart => "可启动",
            TestUIState.Testing => "测试中",
            TestUIState.Paused => "已停止",
            TestUIState.AwaitingReset => "请复位",
            TestUIState.EmergencyStop => "急停中",
            TestUIState.Resetting => "复位中",
            TestUIState.ResetFailed => "复位失败",
            TestUIState.CompletedPass => "OK",
            TestUIState.CompletedFail => "NG",
            TestUIState.Error => "异常",
            TestUIState.SingleItemNgStopped => "NG",
            _ => SensorStatusText
        };

        IsInputEnabled = state is not (TestUIState.Testing
            or TestUIState.CompletedPass
            or TestUIState.CompletedFail
            or TestUIState.EmergencyStop
            or TestUIState.Resetting
            or TestUIState.ResetFailed
            or TestUIState.Paused
            or TestUIState.AwaitingReset
            or TestUIState.SingleItemNgStopped);

        if (oldState != state)
        {
            _logger.LogInformation("[状态切换] {OldState} -> {NewState}, Text={SensorStatusText}",
                oldState, state, SensorStatusText);
        }
    }

    /// <summary>
    /// 进入运行控制动作门禁。Start/Stop/Reset/EmergencyStop/Finish 同一时间只允许一个动作收口。
    /// </summary>
    private async Task<bool> TryEnterControlActionAsync(InspectionControlAction action)
    {
        if (!await _controlActionLock.WaitAsync(0))
        {
            _logger.LogWarning("[运行控制][{Action}][拒绝] 当前已有动作 {CurrentAction} 正在执行，UiState={UiState}",
                action, _currentControlAction, UiState);
            return false;
        }

        _currentControlAction = action;
        _logger.LogWarning("[运行控制][{Action}][进入] UiState={UiState}", action, UiState);
        return true;
    }

    /// <summary>
    /// 取消当前正在执行的控制动作并等待其让出锁。
    /// 用于 EmergencyStop/Stop/Reset 在 Starting 持有锁时让其取消退出。
    /// 超时返回 false，不阻塞调用方。
    /// </summary>
    private async Task<bool> CancelCurrentActionAndWaitLockAsync(CancellationToken ct = default)
    {
        // 取消 Starting（如有）
        if (_currentControlAction == InspectionControlAction.Starting && _startingCts != null)
        {
            _logger.LogWarning("[运行控制][取消] 正在取消 Starting 以执行更高优先级动作");
            await _startingCts.CancelAsync().ConfigureAwait(false);
        }

        // 释放锁需要等待 Starting 的 catch → ExitControlAction
        for (int i = 0; i < 20; i++)
        {
            if (_currentControlAction == InspectionControlAction.None)
                return true;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }

        _logger.LogWarning("[运行控制][取消] 等待当前动作退出超时（200ms），将尝试直接获取锁");
        return false;
    }

    /// <summary>
    /// 离开运行控制动作门禁，必须与 TryEnterControlActionAsync 成对出现。
    /// 防御检查：如果 _currentControlAction 已是 None，不 Release 防止 SemaphoreFullException。
    /// </summary>
    private void ExitControlAction(InspectionControlAction action)
    {
        if (_currentControlAction == InspectionControlAction.None)
        {
            _logger.LogError("[运行控制][{Action}][防御] ExitControlAction 被重复调用（_currentControlAction 已是 None），已跳过 Release 防止 SemaphoreFullException", action);
            return;
        }

        _logger.LogWarning("[运行控制][{Action}][退出] UiState={UiState}", action, UiState);
        _currentControlAction = InspectionControlAction.None;
        _controlActionLock.Release();
    }

    /// <summary>
    /// 旧检测任务回调到达时，如果页面正在控制动作收口或已明确屏蔽旧回调，则直接丢弃。
    /// </summary>
    private bool ShouldIgnoreInspectionCallback()
    {
        return _ignoreInspectionCallbacksUntilNextStart
               || _isResetting
               || _currentControlAction is InspectionControlAction.Stopping
                   or InspectionControlAction.Resetting
                   or InspectionControlAction.EmergencyStopping
                   or InspectionControlAction.Finishing;
    }

    /// <summary>
    /// 判断检测中止是否属于预期控制动作。此类结果不得映射为普通检测异常。
    /// </summary>
    private static bool IsExpectedControlStopReason(InspectionStopReason reason)
    {
        return reason is InspectionStopReason.PlcStop
            or InspectionStopReason.Reset
            or InspectionStopReason.EmergencyStop
            or InspectionStopReason.Canceled;
    }

    /// <summary>
    /// 复位结束、保存结束等明确流程收口时使用，强制重新计算待机/可启动。
    /// 不受 Resetting 等状态保护条件拦截。
    /// </summary>
    private void ForceRefreshReadyOrCanStartState()
    {
        bool canStart = CanManualStartInspection();
        SetUiState(canStart ? TestUIState.CanStart : TestUIState.Ready);
    }

    /// <summary>
    /// 根据当前上下文刷新为待机或可启动，不覆盖检测中、停止、急停、复位、待保存、单项 NG 停止、异常。
    /// </summary>
    private void RefreshReadyOrCanStartState()
    {
        if (UiState == TestUIState.Testing
        || UiState == TestUIState.Paused
        || UiState == TestUIState.AwaitingReset
        || UiState == TestUIState.EmergencyStop
        || UiState == TestUIState.Resetting
        || UiState == TestUIState.ResetFailed
        || UiState == TestUIState.CompletedPass
        || UiState == TestUIState.CompletedFail
        || UiState == TestUIState.SingleItemNgStopped
        || UiState == TestUIState.Error)
            return;

        bool canStart = CanManualStartInspection();
        SetUiState(canStart ? TestUIState.CanStart : TestUIState.Ready);
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

            SetUiState(finalResult == "OK" ? TestUIState.CompletedPass : TestUIState.CompletedFail);

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

                bool cleanupSucceeded = await CompleteNormalInspectionHandshakeAsync().ConfigureAwait(false);
                if (!cleanupSucceeded)
                    return;

                await Application.Current.Dispatcher.InvokeAsync(ResetToReadyState);
            }
            else
            {
                var cleanupOk2 = await CompleteNormalInspectionHandshakeAsync().ConfigureAwait(false);
                if (!cleanupOk2)
                    return;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _inspectionStarted = false;
                    _startSignalHandled = false;
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

        // 本轮检测完成后保持当前测量模式，不强制恢复电阻模式
        // 下一件开始时会根据第一个检测项自动切换正确模式
        _logger.LogInformation("[万用表收尾] 本轮检测完成，保持当前测量模式，等待下一轮检测");
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
    /// 停止流程只做有限 DT122 收口。若稳定确认后仍为 1，交给 ResetFlow 最终兜底。
    /// </summary>
    private async Task<bool> CompleteStopSignalHandshakeAsync(string context, CancellationToken ct)
    {
        _logger.LogWarning("[停止流程][DT122] 开始收口，Context={Context}", context);

        var clearResult = await _plcDevice.ClearStopRequestAsync(ct).ConfigureAwait(false);
        if (!clearResult.IsSuccess)
        {
            _logger.LogWarning("[停止流程][DT122] 清除失败：{Message}，仍保持 AwaitingReset", clearResult.Message);
            return false;
        }

        await Task.Delay(100, ct).ConfigureAwait(false);
        var first = await _plcDevice.ReadControlSignalsAsync(ct).ConfigureAwait(false);
        if (!first.IsSuccess || first.Value == null)
        {
            _logger.LogWarning("[停止流程][DT122] 第一次稳定确认读取失败：{Message}", first.Message);
            return false;
        }

        if (!first.Value.IsStopRequested)
        {
            _logger.LogWarning("[停止流程][DT122] 已确认释放");
            return true;
        }

        await Task.Delay(100, ct).ConfigureAwait(false);
        var second = await _plcDevice.ReadControlSignalsAsync(ct).ConfigureAwait(false);
        if (!second.IsSuccess || second.Value == null)
        {
            _logger.LogWarning("[停止流程][DT122] 第二次稳定确认读取失败：{Message}", second.Message);
            return false;
        }

        if (!second.Value.IsStopRequested)
        {
            _logger.LogWarning("[停止流程][DT122] 第二次确认已释放");
            return true;
        }

        _logger.LogWarning("[停止流程][DT122] 稳定确认仍为 1，保持 AwaitingReset，等待 ResetFlow 兜底");
        return false;
    }

    /// <summary>
    /// ResetFlow 对 DT122 做最终兜底。有限重试仍失败时，复位不得进入 CanStart。
    /// </summary>
    private async Task<bool> CompleteResetStopSignalFinalizationAsync(CancellationToken ct)
    {
        _logger.LogWarning("[复位流程][DT122] 开始兜底清理");

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var clearResult = await _plcDevice.ClearStopRequestAsync(ct).ConfigureAwait(false);
            if (!clearResult.IsSuccess)
            {
                _logger.LogWarning("[复位流程][DT122] 第 {Attempt} 次清除失败：{Message}", attempt, clearResult.Message);
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
            var readResult = await _plcDevice.ReadControlSignalsAsync(ct).ConfigureAwait(false);
            if (!readResult.IsSuccess || readResult.Value == null)
            {
                _logger.LogWarning("[复位流程][DT122] 第 {Attempt} 次读取失败：{Message}", attempt, readResult.Message);
                continue;
            }

            if (!readResult.Value.IsStopRequested)
            {
                _logger.LogWarning("[复位流程][DT122] 最终确认成功，第 {Attempt} 次后 DT122=0", attempt);
                return true;
            }

            _logger.LogWarning("[复位流程][DT122] 第 {Attempt} 次确认仍为 1", attempt);
        }

        _logger.LogWarning("[复位流程][DT122] 最终确认失败，DT122 仍未释放");
        return false;
    }

    /// <summary>
    /// 复位收口最终验证。当前快照能确认 DT120/DT121/DT122/DT123 均释放，才允许进入 Ready/CanStart。
    /// </summary>
    private async Task<ResetCompletionValidationResult> ValidateResetCompletionAsync(CancellationToken ct)
    {
        try
        {
            PlcOperationResult<PlcControlSignals>? snapshot;
            using (new PlcCallerScope(_logger, "ResetFlow"))
                snapshot = await _plcDevice.ReadControlSignalsAsync(ct).ConfigureAwait(false);
            if (snapshot == null || !snapshot.IsSuccess || snapshot.Value == null)
            {
                _logger.LogWarning("[复位流程][验证] 读取 PLC 控制信号快照失败：{Message}", snapshot.Message);
                return ResetCompletionValidationResult.PlcReadFailed;
            }

            var signals = snapshot.Value;
            _logger.LogWarning(
                "[复位流程][验证] PLC 快照：DT120={DT120}, DT121={DT121}, DT122={DT122}, DT123={DT123}",
                signals.IsStartRequested ? 1 : 0,
                signals.IsResetRequested ? 1 : 0,
                signals.IsStopRequested ? 1 : 0,
                signals.IsEmergencyStop ? 1 : 0);

            if (signals.IsStartRequested)
                return ResetCompletionValidationResult.StartSignalStillActive;
            if (signals.IsResetRequested)
                return ResetCompletionValidationResult.ResetSignalStillActive;
            if (signals.IsStopRequested)
                return ResetCompletionValidationResult.StopSignalStillActive;
            if (signals.IsEmergencyStop)
                return ResetCompletionValidationResult.EmergencyStopStillActive;
            return ResetCompletionValidationResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[复位流程][验证] 复位后读取 PLC 输入快照异常");
            return ResetCompletionValidationResult.PlcReadFailed;
        }
    }

    private static string FormatResetValidationFailure(ResetCompletionValidationResult result)
    {
        return result switch
        {
            ResetCompletionValidationResult.StartSignalStillActive => "启动信号 DT120 仍有效",
            ResetCompletionValidationResult.ResetSignalStillActive => "复位信号 DT121 仍有效",
            ResetCompletionValidationResult.StopSignalStillActive => "停止信号 DT122 无法清除",
            ResetCompletionValidationResult.EmergencyStopStillActive => "急停信号 DT123 仍有效",
            ResetCompletionValidationResult.PlcReadFailed => "PLC 输入快照读取失败",
            _ => "未知控制信号未释放"
        };
    }

    /// <summary>
    /// 正常完成后的 PLC 握手收口。
    /// 必须在用户处理保存/取消弹窗之后执行，不能在启动复核通过或检测刚完成时提前清 DT120。
    /// </summary>
    private async Task<bool> CompleteNormalInspectionHandshakeAsync()
    {
        _logger.LogWarning("[PLC动作][审计] 检测完成且保存弹窗已处理，开始清理本轮启动握手信号");

        var cleanupResult = await ClearCurrentRunOutputsAsync(CancellationToken.None).ConfigureAwait(false);

        if (!cleanupResult.AllSucceeded)
        {
            _logger.LogError("[PLC收口][审计] 正常完成收口失败：Start={Start}, PcReady={PcReady}, Relay={Relay}, Pins={Pins}, Final={Final}",
                cleanupResult.StartCleared, cleanupResult.PcReadyCleared,
                cleanupResult.RelayCleared, cleanupResult.PinsCleared,
                cleanupResult.FinalResultCleared);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SetUiState(TestUIState.Error);
                AddLog("PLC 收口失败，请执行复位后再重新启动");
            });

            return false;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _inspectionStarted = false;
            _startupCleared = true;
            IsPlcStartRequested = false;
        });

        _logger.LogWarning("[PLC动作][审计] 本轮正常完成收口结束：DT120/DT234/DT304/DT305 已清除，界面结果保留到复位");
        return true;
    }

    #endregion

    #region 统一 PLC 输出清理

    /// <summary>
    /// 清当前运行输出：DT120、DT234、DT302、DT130~185、DT304/DT305。
    /// 停止、单项 NG、正常完成收口、紧急停止后调用。
    /// 返回逐项清理结果。
    /// </summary>
    private async Task<RunOutputCleanupResult> ClearCurrentRunOutputsAsync(CancellationToken ct = default)
    {
        var start = await _plcDevice.ClearStartRequestAsync(ct).ConfigureAwait(false);
        var pcReady = await _plcDevice.ClearPcReadyAsync(ct).ConfigureAwait(false);
        var relay = await _plcDevice.ClearRelayActionCompletedAsync(ct).ConfigureAwait(false);
        var pins = await _plcDevice.ClearPinOutputsAsync(ct).ConfigureAwait(false);
        var finalResult = await _plcDevice.ClearFinalResultAsync(ct).ConfigureAwait(false);

        var result = new RunOutputCleanupResult(
            start.IsSuccess,
            pcReady.IsSuccess,
            relay.IsSuccess,
            pins.IsSuccess,
            finalResult.IsSuccess);

        if (result.AllSucceeded)
        {
            _logger.LogWarning("[PLC清理][成功] DT120={Start}, DT234={PcReady}, DT302={Relay}, DT130~185={Pins}, DT304/305={Final}",
                start.IsSuccess, pcReady.IsSuccess, relay.IsSuccess, pins.IsSuccess, finalResult.IsSuccess);
        }
        else
        {
            _logger.LogError("[PLC清理][失败] DT120={Start}, DT234={PcReady}, DT302={Relay}, DT130~185={Pins}, DT304/305={Final}",
                start.IsSuccess, pcReady.IsSuccess, relay.IsSuccess, pins.IsSuccess, finalResult.IsSuccess);
        }

        return result;
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

        if (!await TryEnterControlActionAsync(InspectionControlAction.Finishing))
        {
            AddLog("终止请求已拒绝：当前已有运行控制动作正在执行。");
            return;
        }

        _isFinishing = true;

        try
        {
            _logger.LogWarning("[终止按钮][审计] 终止按钮触发，写 DT306=1");
            await _plcDevice.RequestTerminateAsync(CancellationToken.None).ConfigureAwait(false);
            AddLog("终止信号(DT306=1)已写入");

            if (UiState == TestUIState.Testing && _inspectionEngine != null)
            {
                var stopResult = await _inspectionEngine.StopAndWaitAsync(TimeSpan.FromSeconds(2), InspectionStopReason.Canceled, CancellationToken.None);
                if (stopResult == InspectionStopWaitResult.Timeout)
                {
                    SetUiState(TestUIState.Error);
                    AddLog("终止失败：检测任务未能在超时时间内安全停止，请检查设备状态后重试。");
                    return;
                }

                AddLog("操作员终止了当前测试");
            }

            StopPlcPolling();
            await ClearAllRunSignalsAsync(CancellationToken.None).ConfigureAwait(false);
            await _multimeterDevice.ReleaseToLocalAsync().ConfigureAwait(false);
            _logger.LogInformation("[万用表收尾] 终止按钮触发，万用表已退出远程控制");

            await Application.Current.Dispatcher.Invoke(async () =>
            {
                ResetToReadyState();
                await _navigationService.NavigateToAsync<MainMenuView>();
            });

            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await _plcDevice.ClearTerminateRequestAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogWarning("[终止按钮][审计] 延时 1 秒后已清除 DT306=0");
            });
        }
        finally
        {
            _isFinishing = false;
            ExitControlAction(InspectionControlAction.Finishing);
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
    /// 真实模式 200ms，半实物/Fake 100ms；不低于 100ms，避免 PLC 通信堆积。
    /// </summary>
    private void StartPlcPolling()
    {
        if (_plcPollingTimer != null) return;

        var pollingInterval = GetPlcPollingInterval();
        _plcPollingTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = pollingInterval
        };

        _plcPollingTimer.Tick += async (s, e) =>
        {
            await PollPlcInputsAsync();
        };

        _plcPollingTimer.Start();
        _logger.LogDebug("PLC 轮询已启动，模式 Fake={IsFakeMode}, SemiPhysical={IsSemiPhysical}, IntervalMs={IntervalMs}",
            IsFakeMode,
            IsSemiPhysicalDebugMode,
            pollingInterval.TotalMilliseconds);
    }

    /// <summary>
    /// 根据当前运行模式选择 PLC 轮询周期。
    /// 统一 200ms，降低常驻 Modbus 请求负载。
    /// </summary>
    private TimeSpan GetPlcPollingInterval()
    {
        return TimeSpan.FromMilliseconds(200);
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
    /// 暂停低优先级 UI 轮询并取消当前正在进行的轮询请求。
    /// 控制动作（Stop/Reset/EmergencyStop）执行前调用。
    /// </summary>
    private void SuspendPlcPolling()
    {
        _plcPollingSuspended = true;
        _plcPollingOperationCts?.Cancel();
        _plcPollingOperationCts?.Dispose();
        _plcPollingOperationCts = null;
        _logger.LogDebug("[PLC轮询] 已暂停");
    }

    /// <summary>
    /// 恢复低优先级 UI 轮询。控制动作完成后调用。
    /// </summary>
    private void ResumePlcPolling()
    {
        _plcPollingSuspended = false;
        _logger.LogDebug("[PLC轮询] 已恢复");
    }

    /// <summary>
    /// PLC 轮询主体：读取控制信号 → 更新 UI 状态 → 检测 DT120 启动请求。
    /// </summary>
    private async Task PollPlcInputsAsync()
    {
        // 控制动作执行期间暂停低优先级轮询
        if (_plcPollingSuspended)
        {
            _logger.LogDebug("[PLC轮询][暂停] 控制动作执行中，跳过本轮");
            return;
        }

        if (Interlocked.Exchange(ref _plcPollingInProgress, 1) == 1)
        {
            _logger.LogDebug("[PLC轮询][跳过] 上一轮尚未结束，跳过本轮");
            return;
        }

        try
        {
            if (!_plcDevice.IsConnected || _inspectionEngine == null) return;

            // 创建本轮可取消令牌，供控制动作中断等待
            _plcPollingOperationCts?.Cancel();
            _plcPollingOperationCts?.Dispose();
            _plcPollingOperationCts = new CancellationTokenSource();
            var pollingCt = _plcPollingOperationCts.Token;

            PlcOperationResult<PlcControlSignals>? pollResult;
            using (new PlcCallerScope(_logger, "UiPolling"))
            {
                pollResult = await _plcDevice.ReadControlSignalsAsync(pollingCt).ConfigureAwait(false);
            }
            if (pollResult == null || !pollResult.IsSuccess)
            {
                if (pollResult?.IsCancelled == true)
                {
                    _logger.LogInformation("[PLC轮询] 本轮读取因控制动作切换已取消");
                }
                else
                {
                    _logger.LogWarning("[PLC轮询][诊断] 读取控制信号失败：{Message}", pollResult?.Message ?? "null");
                }
                return;
            }

            var previousInputs = _lastPlcInputs;
            var inputs = pollResult.Value;

            IsPlcStartRequested = inputs.IsStartRequested;

            var operation = Application.Current.Dispatcher.InvokeAsync(
                () => UpdateUiStateFromPlcInputsAsync(previousInputs, inputs));
            await operation.Task.Unwrap().ConfigureAwait(false);

            if (ReferenceEquals(_lastPlcInputs, previousInputs))
            {
                _lastPlcInputs = inputs;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[PLC轮询][取消] 本轮轮询被控制动作取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC轮询] 轮询异常");
        }
        finally
        {
            Volatile.Write(ref _plcPollingInProgress, 0);
        }
    }
    /// <summary>
    /// 根据 PLC 输入信号更新 UI 状态（在 UI 线程执行）。
    /// 复位信号 DT121 的处理遵循握手协议：
    ///   1. 收到 DT121=1 → 上位机执行所有复位操作
    ///   2. 上位机操作全部完成后 → 最后清除 DT121
    ///   3. 清除 DT121 后 → 通知 PLC 上位机已就绪
    /// </summary>
    private async Task UpdateUiStateFromPlcInputsAsync(PlcControlSignals? previousInputs, PlcControlSignals inputs)
    {
        // ── 复位信号边沿检测：DT121 从 1→0 时重置处理标志 ──
        if (!inputs.IsResetRequested)
        {
            _resetSignalHandled = false;
            _isResetting = false;
            Interlocked.Exchange(ref _resetFailureNotificationShown, 0);

            // DT121=0 只表示复位请求信号释放，不代表旧检测引擎已经退出。
            // ResetFailed 必须等下一次完整复位成功后才能恢复 Ready/CanStart。
            if (UiState == TestUIState.ResetFailed)
            {
                _logger.LogWarning("[复位状态] DT121 已释放，但当前仍为 ResetFailed，不自动恢复 CanStart");
                AddLog("复位请求信号已释放，但上次复位未完成，请重新执行复位");
            }
        }

        // ── DT123 回到 0，重置弹窗确认标志 ──
        if (!inputs.IsEmergencyStop)
        {
            _emergencyDialogAcknowledged = false;
        }

        bool emergencyStopTriggered = IsEmergencyStopTriggered(previousInputs, inputs);

        // ════════════════════════════════════════════════════════
        // 急停按事件处理：首次 DT123=1 或真正 0→1 才启动急停 Flow。
        // 持续高电平只表示现场仍处于急停，不允许把 AwaitingReset 旧快照再次解释成新急停。
        // ════════════════════════════════════════════════════════
        if (emergencyStopTriggered)
        {
            _logger.LogWarning(
                "[急停边沿][触发] Previous={Previous}, Current={Current}, UiState={UiState}, CurrentAction={CurrentAction}",
                previousInputs?.IsEmergencyStop == true ? 1 : 0,
                inputs.IsEmergencyStop ? 1 : 0,
                UiState,
                _currentControlAction);

            await ExecuteEmergencyStopFlowAsync(InspectionActionSource.PlcPolling);

            if (!_isShowingEmergencyDialog)
            {
                _isShowingEmergencyDialog = true;
                ShowEmergencyStopDialog();
            }
            return; // 急停状态下不处理启动/停止/复位信号
        }

        if (inputs.IsEmergencyStop)
        {
            if (UiState == TestUIState.AwaitingReset)
            {
                _logger.LogDebug("[PLC快照][忽略旧急停] UiState=AwaitingReset，未检测到新的 DT123 上升沿");
            }

            return;
        }


        // ════════════════════════════════════════════════════════
        // 复位信号处理（优先级最高，急停状态下也能触发）
        // 真实模式下工人按实体按钮 → PLC DT121=1 → 轮询捕获 → 执行复位
        // ════════════════════════════════════════════════════════
        if (inputs.IsResetRequested)
        {
            if (_resetSignalHandled)
                return;

            if (_currentControlAction == InspectionControlAction.EmergencyStopping)
            {
                _logger.LogWarning("[急停流程] Reset 请求被拒绝，需先解除急停后人工复位，来源=PLC轮询");
                AddLog("[急停流程] 当前处于急停中，请先解除急停，解除后再执行复位");
                _resetSignalHandled = true;
                return;
            }

            AddLog("PLC 复位信号(DT121)，正在执行上位机复位操作...");
            _logger.LogWarning("[PLC轮询][审计] 检测到 DT121=1，执行复位流程");
            _resetSignalHandled = true;

            await ExecuteResetFlowAsync(InspectionActionSource.PlcPolling);
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

                if (_currentControlAction == InspectionControlAction.Resetting || _isResetting)
                {
                    _logger.LogWarning("[动作仲裁][吸收] Resetting 期间收到 Stop，交由 ResetFlow 兜底 DT122");
                    return;
                }

                await ExecuteStopFlowAsync(InspectionActionSource.PlcPolling);
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
            await HandlePlcStartRequestAsync(InspectionActionSource.PlcPolling);
        }

        // ════════════════════════════════════════════════════════
        // 更新普通待机/可启用状态
        // ════════════════════════════════════════════════════════
        UpdateUIState();
    }

    /// <summary>
    /// DT123 只按首次高电平或 0→1 上升沿解释为新的急停事件，避免旧快照重复触发急停流程。
    /// </summary>
    private static bool IsEmergencyStopTriggered(PlcControlSignals? previousInputs, PlcControlSignals currentInputs)
    {
        return previousInputs == null
            ? currentInputs.IsEmergencyStop
            : !previousInputs.IsEmergencyStop && currentInputs.IsEmergencyStop;
    }

    /// <summary>
    /// 启动请求状态分类。在进入完整启动校验前先做静默忽略或拒绝。
    /// </summary>
    private StartRequestDecision EvaluateStartRequestDecision(InspectionActionSource source)
    {
        // Testing → IgnoreDuplicate
        if (UiState == TestUIState.Testing)
        {
            _logger.LogInformation("[启动请求][忽略] 当前检测已运行(UiState=Testing)，来源={Source}", source);
            return StartRequestDecision.IgnoreDuplicate;
        }

        // Starting → IgnoreDuplicate
        if (_currentControlAction == InspectionControlAction.Starting)
        {
            _logger.LogInformation("[启动请求][忽略] 启动流程正在处理中，来源={Source}", source);
            return StartRequestDecision.IgnoreDuplicate;
        }

        // EmergencyStop → RejectEmergencyStop
        if (UiState == TestUIState.EmergencyStop)
        {
            _logger.LogWarning("[启动请求][拒绝] 当前处于急停状态，来源={Source}", source);
            return StartRequestDecision.RejectEmergencyStop;
        }

        // Resetting / ResetFailed → RejectBusy
        if (UiState is TestUIState.Resetting or TestUIState.ResetFailed || _currentControlAction == InspectionControlAction.Resetting)
        {
            _logger.LogWarning("[启动请求][拒绝] 当前正在复位或复位失败，来源={Source}", source);
            return StartRequestDecision.RejectBusy;
        }

        // AwaitingReset → RejectNeedReset
        if (UiState == TestUIState.AwaitingReset)
        {
            _logger.LogWarning("[启动请求][拒绝] 当前状态需要先复位，来源={Source}", source);
            return StartRequestDecision.RejectNeedReset;
        }

        // Paused → RejectNeedReset
        if (UiState == TestUIState.Paused)
        {
            _logger.LogWarning("[启动请求][拒绝] 当前已暂停，需要先复位，来源={Source}", source);
            return StartRequestDecision.RejectNeedReset;
        }

        // CompletedPass / CompletedFail → RejectNeedReset
        if (UiState is TestUIState.CompletedPass or TestUIState.CompletedFail)
        {
            _logger.LogWarning("[启动请求][拒绝] 当前检测结果尚未处理完成，来源={Source}", source);
            return StartRequestDecision.RejectNeedReset;
        }

        // Error / SingleItemNgStopped → RejectNeedReset
        if (UiState is TestUIState.Error or TestUIState.SingleItemNgStopped)
        {
            _logger.LogWarning("[启动请求][拒绝] 当前状态需要先复位，来源={Source}", source);
            return StartRequestDecision.RejectNeedReset;
        }

        // Ready / CanStart → 继续完整校验
        return StartRequestDecision.ContinueValidation;
    }

    /// <summary>
    /// 处理 PLC DT120=1 启动请求。
    /// 先按状态分类决策，再复核启动条件，通过后写 DT234=1，最后调用 RunInspectionAsync。
    /// </summary>
    private async Task HandlePlcStartRequestAsync(InspectionActionSource source)
    {
        _logger.LogWarning("[PLC轮询][审计] 收到 PLC 启动请求(DT120=1)，来源={Source}", source);

        // ── 状态级启动分类 ──
        var decision = EvaluateStartRequestDecision(source);
        switch (decision)
        {
            case StartRequestDecision.IgnoreDuplicate:
                _logger.LogInformation("[启动请求][忽略] 当前检测已运行，来源={Source}", source);
                AddLog($"[启动请求][忽略] 当前检测已运行，来源={source}");
                return;

            case StartRequestDecision.RejectNeedReset:
                _logger.LogWarning("[启动请求][拒绝] 当前状态需要先复位，来源={Source}", source);
                _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
                _startupCleared = true;
                AddLog($"启动拒绝：当前状态需要先复位，来源={source}");
                if (source == InspectionActionSource.DebugPanel || source == InspectionActionSource.RealModeButton)
                {
                    _ = _notificationService.ShowWarningAsync("当前状态需要先复位，无法启动检测", "启动拒绝");
                }
                return;

            case StartRequestDecision.RejectEmergencyStop:
                _logger.LogWarning("[启动请求][拒绝] 当前处于急停状态，来源={Source}", source);
                _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
                _startupCleared = true;
                AddLog($"启动拒绝：当前处于急停状态，请先解除急停并复位，来源={source}");
                if (source == InspectionActionSource.DebugPanel || source == InspectionActionSource.RealModeButton)
                {
                    _ = _notificationService.ShowWarningAsync("当前处于急停状态，请先解除急停并复位", "启动拒绝");
                }
                return;

            case StartRequestDecision.RejectBusy:
                _logger.LogWarning("[启动请求][拒绝] 当前忙（ControlAction={Action}），来源={Source}", _currentControlAction, source);
                _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
                _startupCleared = true;
                string busyMessage = UiState == TestUIState.ResetFailed
                    ? "上次复位未完成，请重新执行复位后再启动检测"
                    : "当前系统忙，请稍后重试";
                AddLog($"启动拒绝：{busyMessage}，来源={source}");
                if (source == InspectionActionSource.DebugPanel || source == InspectionActionSource.RealModeButton)
                {
                    _ = _notificationService.ShowWarningAsync(busyMessage, "启动拒绝");
                }
                return;

            case StartRequestDecision.ContinueValidation:
                break;
        }

        // ── 继续完整启动校验 ──
        if (!await TryEnterControlActionAsync(InspectionControlAction.Starting))
        {
            _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
            _startupCleared = true;
            AddLog("启动请求已拒绝：当前已有运行控制动作正在执行。");
            return;
        }

        try
        {
            _startingCts = new CancellationTokenSource();
            var startingToken = _startingCts.Token;

            // 启动复核
            string? rejectReason = ValidateStartConditions();
            if (rejectReason != null)
            {
                _logger.LogWarning("[启动复核][拒绝] {Reason}", rejectReason);
                _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
                _startupCleared = true;
                AddLog($"启动拒绝：{rejectReason}");
                _ = _notificationService.ShowWarningAsync(rejectReason, "启动拒绝");
                return;
            }

            startingToken.ThrowIfCancellationRequested();

            bool dmmPingOk = await _multimeterDevice.PingAsync(CancellationToken.None).ConfigureAwait(false);
            if (!dmmPingOk)
            {
                _logger.LogWarning("[启动复核][拒绝] 万用表通信验证失败，已清除 DT120");
                await _plcDevice.ClearStartRequestAsync(CancellationToken.None).ConfigureAwait(false);
                _startupCleared = true;
                AddLog("启动失败：万用表无法通信，请检查网络连接后重试");
                _ = _notificationService.ShowWarningAsync("万用表无法通信，请检查网络连接后重试。", "启动拒绝");
                return;
            }

            startingToken.ThrowIfCancellationRequested();

            var pcReadyResult = await _plcDevice.WritePcReadyAsync(CancellationToken.None).ConfigureAwait(false);
            if (!pcReadyResult.IsSuccess)
            {
                _logger.LogWarning("[启动复核][拒绝] 写 DT234=1 失败：{Message}，已清除 DT120", pcReadyResult.Message);
                _ = _plcDevice.ClearStartRequestAsync(CancellationToken.None);
                _startupCleared = true;
                AddLog($"启动失败：写 DT234=1 失败 - {pcReadyResult.Message}");
                _ = _notificationService.ShowWarningAsync($"写 DT234 失败：{pcReadyResult.Message}", "启动拒绝");
                return;
            }

            startingToken.ThrowIfCancellationRequested();

            _logger.LogWarning("[启动复核][通过] 条件满足，万用表通信正常，DT234=1 已写入，开始检测");
            _inspectionStarted = true;
            _startSignalHandled = true;
            _ignoreInspectionCallbacksUntilNextStart = false;
            _isResetting = false;
            _waitDt120ReleaseAfterReset = false;

            string lockedModel = ModelName;
            string lockedBarcode = SerialNumber;
            string lockedOperator = OperatorName;

            _ = RunInspectionAsync(lockedBarcode, lockedModel, lockedOperator);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[启动流程][取消] Starting 被外部取消（Stop/Reset/EmergencyStop），已释放锁交由新 Flow 执行");
            _startingCts = null;
            // DT234 可能已写入，由新 Flow（如 ResetFlow）的 ClearCurrentRunOutputsAsync 清理
            return;
        }
        finally
        {
            _startingCts?.Dispose();
            _startingCts = null;
            ExitControlAction(InspectionControlAction.Starting);
        }

    }
    #region 统一复位流程

    /// <summary>
    /// 执行上位机完整复位流程（三种模式统一收口）。
    /// 供以下路径复用：
    ///   1. 半实物联调按钮（TriggerPlcResetAsync）— 写 DT121=1 后等待 PLC 轮询统一消费
    ///   2. Fake 调试按钮（FakeTriggerResetAsync）— 写 DT121=1 后等待 PLC 轮询统一消费
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
    private async Task ExecuteResetFlowAsync(InspectionActionSource source)
    {
        // ── 层 2：请求门禁（重复复位忽略，不弹错误，不切 Error）──
        if (_currentControlAction == InspectionControlAction.Resetting)
        {
            _logger.LogInformation("[复位请求][忽略] 当前复位流程正在执行，来源={Source}", source);
            AddLog($"[复位请求][忽略] 当前复位流程正在执行，来源={source}");
            return;
        }

        if (_currentControlAction == InspectionControlAction.Stopping)
        {
            _logger.LogInformation("[复位请求][忽略] 当前正在停止，忽略复位请求，来源={Source}", source);
            AddLog($"[复位请求][忽略] 当前正在停止，忽略复位请求，来源={source}");
            return;
        }

        // Starting 中 Reset → 取消 Starting 后执行复位
        if (_currentControlAction == InspectionControlAction.Starting)
        {
            _logger.LogWarning("[复位请求][抢占] Starting 中，取消后执行复位");
            if (_startingCts != null)
                await _startingCts.CancelAsync().ConfigureAwait(false);

            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(10).ConfigureAwait(false);
                if (_currentControlAction == InspectionControlAction.None)
                    break;
            }
        }

        if (_currentControlAction == InspectionControlAction.EmergencyStopping)
        {
            _logger.LogWarning("[急停流程] Reset 请求被拒绝，需先解除急停后人工复位，来源={Source}", source);
            AddLog("[急停流程] 当前处于急停中，请先解除急停，解除后再执行复位");
            if (source == InspectionActionSource.DebugPanel || source == InspectionActionSource.RealModeButton)
            {
                _ = _notificationService.ShowWarningAsync("当前处于急停状态，请先解除急停，解除后再执行复位。", "复位拒绝");
            }
            return;
        }

        if (UiState == TestUIState.EmergencyStop)
        {
            _logger.LogWarning("[急停流程] Reset 请求被拒绝，当前 UiState=EmergencyStop，来源={Source}", source);
            AddLog("[急停流程] 当前处于急停状态，请先解除急停，解除后再执行复位");
            if (source == InspectionActionSource.DebugPanel || source == InspectionActionSource.RealModeButton)
            {
                _ = _notificationService.ShowWarningAsync("当前处于急停状态，请先解除急停，解除后再执行复位。", "复位拒绝");
            }
            return;
        }

        if (_currentControlAction == InspectionControlAction.Finishing)
        {
            _logger.LogInformation("[复位请求][忽略] 当前正在执行 {Action}，来源={Source}", _currentControlAction, source);
            AddLog($"[复位请求][忽略] 当前正在执行 {_currentControlAction}，来源={source}");
            return;
        }

        if (!await TryEnterControlActionAsync(InspectionControlAction.Resetting))
        {
            _logger.LogInformation("[复位请求][忽略] 当前已有控制动作正在执行，来源={Source}", source);
            return;
        }

        // 暂停低优先级 UI 轮询，减少请求并发，避免 Polling 与 Engine/复位清理交叉
        SuspendPlcPolling();

        // 复位开始即锁门，旧检测任务和旧回调只能到这里为止。
        _ignoreInspectionCallbacksUntilNextStart = true;
        _isResetting = true;
        _waitDt120ReleaseAfterReset = true;
        Interlocked.Increment(ref _inspectionRunVersion);
        SetUiState(TestUIState.Resetting);
        _logger.LogWarning("[复位请求][执行] 开始执行复位流程，来源={Source}", source);
        AddLog($"正在执行上位机复位操作...（来源={source}）");

        try
        {
            if (_inspectionEngine!.IsRunning)
            {
                // ── 第 1 级：软超时（2 秒）──
                var stopResult = await _inspectionEngine.StopAndWaitAsync(TimeSpan.FromSeconds(2), InspectionStopReason.Reset, CancellationToken.None);

                if (stopResult == InspectionStopWaitResult.Timeout)
                {
                    // 软超时：引擎未立即退出，但不切 Error，在 Resetting 中继续等待
                    string engineStage = _inspectionEngine.CurrentExecutionStage;
                    _logger.LogWarning(
                        "[复位流程][停止-软超时] 2s 软超时，引擎仍在退出 Stage={Stage}，继续等待最多 4s",
                        engineStage);
                    AddLog("复位：检测引擎停止中，请稍候...");

                    // ── 第 2 级：硬超时等待（再等 4 秒，总计 6 秒）──
                    var hardDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
                    bool engineExited = false;

                    while (DateTime.UtcNow < hardDeadline)
                    {
                        await Task.Delay(200, CancellationToken.None);
                        if (!_inspectionEngine.IsRunning)
                        {
                            engineExited = true;
                            break;
                        }
                    }

                    if (!engineExited)
                    {
                        // 硬超时 → ResetFailed（不是 Error）
                        string hardStage = _inspectionEngine.CurrentExecutionStage;
                        bool hardIsRunning = _inspectionEngine.IsRunning;
                        _logger.LogError(
                            "[复位流程][停止-硬超时] UiState={UiState}, Action={Action}, EngineState={EngineState}, EngineStage={Stage}, EngineIsRunning={IsRunning}, RunVersion={Version}",
                            UiState, _currentControlAction, _inspectionEngine.CurrentState, hardStage, hardIsRunning, _inspectionRunVersion);

                        SetUiState(TestUIState.ResetFailed);

                        // 硬超时：清 DT121 标记为失败清理
                        try
                        {
                            await _plcDevice.ClearResetRequestAsync(CancellationToken.None);
                            _logger.LogWarning("[复位流程][审计][硬超时] 复位硬超时后清理 DT121");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[复位流程][硬超时] 清除 DT121 失败");
                        }

                        // 只弹一次错误
                        if (Interlocked.Exchange(ref _resetFailureNotificationShown, 1) == 0)
                        {
                            AddLog("复位操作超时：检测引擎未能安全停止，请确认设备就绪后重新尝试复位。");
                            await _notificationService.ShowErrorAsync(
                                "复位操作超时：检测引擎未能安全停止。\n请确认设备就绪后重新尝试复位。",
                                "复位超时");
                        }
                        else
                        {
                            _logger.LogWarning("[复位流程][审计] 复位失败弹窗已显示过，跳过重复弹窗");
                            AddLog("复位超时：检测引擎未能停止（弹窗已显示）");
                        }

                        _isResetting = false;
                        // 不在此处 ExitControlAction，统一由 finally 释放一次
                        return;
                    }

                    // 引擎在宽限期内退出，继续正常复位
                    _logger.LogInformation("[复位流程][停止] 引擎在软超时后已退出，继续复位流程");
                }
            }

            // 统一调用运行输出清理替换手写序列
            await ClearCurrentRunOutputsAsync(CancellationToken.None);

            if (_plcDevice is Devices.Fakes.FakeInspectionHardware fake)
            {
                await fake.WriteInputRegisterAsync(PlcAddressMap.StopSignal, 0, CancellationToken.None);
                await fake.WriteInputRegisterAsync(PlcAddressMap.EmergencyStopSignal, 0, CancellationToken.None);
                _logger.LogInformation("[复位流程][Fake] 已清除 DT122(停止) 和 DT123(急停)");
            }

            ClearTestItemsForRestart();
            _logger.LogInformation("[复位流程] 已清空界面检测项目结果");

            // 让已排队的低优先级回调先被调度一次；这些回调会被 ShouldIgnoreInspectionCallback 丢弃。
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background);

            _inspectionStarted = false;
            _startupCleared = true;
            _startSignalHandled = true;
            _isShowingEmergencyDialog = false;
            _emergencyDialogAcknowledged = false;
            _emergencyStopDialogVM?.StopPolling();
            _emergencyStopDialogVM = null;
            IsPlcStartRequested = false;
            _inspectionEngine.ClearResetState();
            _logger.LogInformation("[复位流程] 已重置内部状态标志");

            bool stopSignalReleased = await CompleteResetStopSignalFinalizationAsync(CancellationToken.None).ConfigureAwait(false);
            if (!stopSignalReleased)
            {
                SetUiState(TestUIState.ResetFailed);
                _isResetting = false;
                AddLog("复位未完成：停止信号 DT122 无法清除，请检查 PLC 通信后重新复位");
                _logger.LogWarning("[复位流程][DT122] 最终确认失败，禁止进入 CanStart");
                await _notificationService.ShowWarningAsync(
                    "复位未完成：停止信号 DT122 无法清除，请检查 PLC 通信后重新复位。",
                    "复位失败").ConfigureAwait(false);
                return;
            }

            await _plcDevice.ClearResetRequestAsync(CancellationToken.None);
            _logger.LogInformation("[复位流程] 上位机复位操作全部完成，已清除 DT121 复位信号");

            var validationResult = await ValidateResetCompletionAsync(CancellationToken.None).ConfigureAwait(false);
            if (validationResult != ResetCompletionValidationResult.Success)
            {
                string failure = FormatResetValidationFailure(validationResult);
                SetUiState(TestUIState.ResetFailed);
                _isResetting = false;
                AddLog($"复位未完成：{failure}，请检查 PLC 通信后重新复位");
                _logger.LogWarning("[复位流程][验证] 失败：{Result}，禁止进入 CanStart", validationResult);
                await _notificationService.ShowWarningAsync(
                    $"复位未完成：{failure}，请检查 PLC 通信后重新复位。",
                    "复位失败").ConfigureAwait(false);
                return;
            }

            _isResetting = false;
            ForceRefreshReadyOrCanStartState();
            AddLog("复位完成，已清空所有检测结果，可重新开始测试");
            await _notificationService.ShowInfoAsync(
                "复位完成，已清空所有检测结果，可重新开始测试。",
                "复位完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[复位流程] 复位过程发生异常");
            _isResetting = false;
            SetUiState(TestUIState.Error);
            AddLog($"复位异常: {ex.Message}");
            throw;
        }
        finally
        {
            ResumePlcPolling();
            ExitControlAction(InspectionControlAction.Resetting);
        }

    }

    #endregion

    #region 停止流程

    /// <summary>
    /// 执行完整停止流程（DT122 停止）。
    /// 停止检测、保留已测显示、清断点、清 PLC 输出、清 DT122。
    /// 重复停止请求幂等忽略。
    /// </summary>
    private async Task ExecuteStopFlowAsync(InspectionActionSource source)
    {
        // ── 重复停止幂等忽略 ──
        if (_currentControlAction == InspectionControlAction.Stopping)
        {
            _logger.LogInformation("[停止请求][忽略] 当前停止流程正在执行，来源={Source}", source);
            AddLog($"[停止请求][忽略] 当前停止流程正在执行，来源={source}");
            return;
        }

        // AwaitingReset/Resetting/Finishing 中 Stop → 忽略
        if (UiState is TestUIState.AwaitingReset or TestUIState.Resetting || _isResetting)
        {
            _logger.LogWarning("[停止请求][吸收] 当前状态 {UiState} 无需停止，来源={Source}", UiState, source);
            return;
        }

        // Starting 中 Stop → 取消 Starting 后再执行
        if (_currentControlAction == InspectionControlAction.Starting)
        {
            _logger.LogWarning("[停止请求][抢占] Starting 中，取消后执行停止");
            if (_startingCts != null)
                await _startingCts.CancelAsync().ConfigureAwait(false);

            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(10).ConfigureAwait(false);
                if (_currentControlAction == InspectionControlAction.None)
                    break;
            }
        }

        if (!await TryEnterControlActionAsync(InspectionControlAction.Stopping))
        {
            _logger.LogInformation("[停止请求][忽略] 当前已有控制动作正在执行，来源={Source}", source);
            return;
        }

        // 暂停低优先级 UI 轮询，减少请求并发
        SuspendPlcPolling();

        try
        {
            _ignoreInspectionCallbacksUntilNextStart = true;
            Interlocked.Increment(ref _inspectionRunVersion);
            SetUiState(TestUIState.Paused);
            _logger.LogWarning("[停止流程][审计] DT122 停止信号，来源={Source}，执行停止收口", source);
            AddLog($"[停止流程] 收到停止信号(DT122)，来源={source}，正在停止检测...");

            if (_inspectionEngine!.IsRunning)
            {
                var stopResult = await _inspectionEngine.StopAndWaitAsync(TimeSpan.FromSeconds(2), InspectionStopReason.PlcStop, CancellationToken.None);
                if (stopResult == InspectionStopWaitResult.Timeout)
                {
                    string engineStage = _inspectionEngine.CurrentExecutionStage;
                    bool engineIsRunning = _inspectionEngine.IsRunning;
                    _logger.LogError(
                        "[停止流程][停止超时] UiState={UiState}, ControlAction={Action}, EngineState={EngineState}, EngineStage={Stage}, EngineIsRunning={IsRunning}",
                        UiState, _currentControlAction, _inspectionEngine.CurrentState, engineStage, engineIsRunning);
                    await CompleteStopSignalHandshakeAsync("StopAndWaitTimeout", CancellationToken.None).ConfigureAwait(false);
                    _inspectionStarted = false;
                    _startupCleared = true;
                    IsPlcStartRequested = false;
                    SetUiState(TestUIState.AwaitingReset);
                    AddLog("停止处理中超时：请执行复位完成设备收口。");
                    return;
                }
            }

            _inspectionEngine.ClearResetState();
            await ClearCurrentRunOutputsAsync(CancellationToken.None).ConfigureAwait(false);
            bool stopSignalReleased = await CompleteStopSignalHandshakeAsync("NormalStopFlow", CancellationToken.None).ConfigureAwait(false);

            _inspectionStarted = false;
            _startupCleared = true;
            IsPlcStartRequested = false;
            SetUiState(TestUIState.AwaitingReset);
            if (stopSignalReleased)
            {
                AddLog("[停止流程] 已停止，必须复位后才能重新从第一项开始检测");
                _logger.LogWarning("[停止流程][审计] 停止收口完成，DT122 已释放，页面进入 AwaitingReset");
            }
            else
            {
                AddLog("[停止流程] 已停止，但 DT122 仍未稳定释放，请执行复位完成收口");
                _logger.LogWarning("[停止流程][DT122] 稳定确认未释放，页面保持 AwaitingReset，等待 ResetFlow 兜底");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[停止流程] 停止过程发生异常");
            SetUiState(TestUIState.Error);
        }
        finally
        {
            ResumePlcPolling();
            ExitControlAction(InspectionControlAction.Stopping);
        }

    }
    #endregion

    #region 急停流程

    /// <summary>
    /// 执行急停流程（DT123 急停）。
    /// 停止检测、清 PLC 输出、清断点。
    /// 重复急停请求幂等忽略。
    /// ★ 不在这里弹窗，统一由 PLC 轮询触发急停弹窗。
    /// </summary>
    private async Task ExecuteEmergencyStopFlowAsync(InspectionActionSource source)
    {
        // ── 重复急停幂等忽略 ──
        if (_currentControlAction == InspectionControlAction.EmergencyStopping)
        {
            _logger.LogInformation("[急停请求][忽略] 当前急停流程正在执行，来源={Source}", source);
            return;
        }

        // 急停最高优先级：取消当前 Starting（如有），等待锁释放
        if (_currentControlAction != InspectionControlAction.None)
        {
            _logger.LogWarning("[急停流程][抢占] 当前有 {CurrentAction}，将取消后执行急停", _currentControlAction);
            if (_currentControlAction == InspectionControlAction.Starting && _startingCts != null)
                await _startingCts.CancelAsync().ConfigureAwait(false);

            // 等待当前动作退让锁
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(10).ConfigureAwait(false);
                if (_currentControlAction == InspectionControlAction.None)
                    break;
            }
        }

        // 无论引擎是否正在运行，强制发中止
        if (_inspectionEngine?.IsRunning == true)
            _inspectionEngine.StopForEmergencyStop();

        if (!await TryEnterControlActionAsync(InspectionControlAction.EmergencyStopping))
        {
            _logger.LogWarning("[急停流程][失败] 无法获取控制锁（可能是 Finishing 进行中）");
            return;
        }

        // 暂停低优先级 UI 轮询，减少请求并发
        SuspendPlcPolling();

        _logger.LogWarning("[急停流程][审计] 急停信号 DT123，来源={Source}，执行急停收口", source);
        AddLog($"[急停流程] 急停信号，来源={source}，正在停止检测...");

        try
        {
            _ignoreInspectionCallbacksUntilNextStart = true;
            Interlocked.Increment(ref _inspectionRunVersion);
            SetUiState(TestUIState.EmergencyStop);

            if (_inspectionEngine!.IsRunning)
                _inspectionEngine.StopForEmergencyStop();

            // 统一调用运行输出清理替换手写序列
            await ClearCurrentRunOutputsAsync(CancellationToken.None);

            _inspectionEngine.ClearResetState();
            _inspectionStarted = false;
            _startupCleared = true;
            IsPlcStartRequested = false;
            SetUiState(TestUIState.EmergencyStop);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[急停流程] 急停过程发生异常");
            SetUiState(TestUIState.EmergencyStop);
            AddLog($"急停收口异常：{ex.Message}，请确认现场安全后执行急停解除/复位");
        }
        finally
        {
            // 急停后恢复轮询，检测后续 DT123 急停解除信号
            ResumePlcPolling();
            ExitControlAction(InspectionControlAction.EmergencyStopping);
        }
    }
    #endregion

    /// <summary>
    /// 弹出急停模态弹窗。
    /// 弹窗只接收操作员解除请求；真正的清信号、读回确认和状态切换统一由 CompleteEmergencyStopReleaseAsync 完成。
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
            releaseEmergencyStop: async () =>
            {
                bool released = await CompleteEmergencyStopReleaseAsync(CancellationToken.None);
                if (!released)
                {
                    return false;
                }

                dialog.AllowClose();
                dialog.DialogResult = true;
                dialog.Close();
                _isShowingEmergencyDialog = false;
                _emergencyDialogAcknowledged = true;
                _emergencyStopDialogVM?.StopPolling();
                _emergencyStopDialogVM = null;
                return true;
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
    /// 急停解除唯一入口：清 DT123/DT303 后读回确认，确认 DT123=0 才允许进入 AwaitingReset。
    /// </summary>
    private async Task<bool> CompleteEmergencyStopReleaseAsync(CancellationToken ct)
    {
        const int maxRetries = 3;
        const int stableDelayMs = 100;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogWarning("[急停解除][开始] 尝试清 DT123/DT303，第 {Attempt}/{MaxRetries} 次", attempt, maxRetries);
                AddLog("急停解除中：正在清除 DT123/DT303 并读回确认...");

                PlcOperationResult? emergencyResult;
                using (new PlcCallerScope(_logger, "EmergencyStopFlow"))
                    emergencyResult = await _plcDevice.ClearEmergencyStopRequestAsync(ct).ConfigureAwait(false);
                if (emergencyResult == null || !emergencyResult.IsSuccess)
                {
                    _logger.LogWarning("[急停解除][失败] 清 DT123 失败，第 {Attempt}/{MaxRetries} 次：{Message}",
                        attempt, maxRetries, emergencyResult.Message);
                }

                var alarmResult = await _plcDevice.ClearAlarmReleasedAsync(ct).ConfigureAwait(false);
                if (!alarmResult.IsSuccess)
                {
                    _logger.LogWarning("[急停解除][失败] 清 DT303 失败，第 {Attempt}/{MaxRetries} 次：{Message}",
                        attempt, maxRetries, alarmResult.Message);
                }

                await Task.Delay(stableDelayMs, ct).ConfigureAwait(false);

                var readBack = await _plcDevice.ReadControlSignalsAsync(ct).ConfigureAwait(false);
                if (!readBack.IsSuccess || readBack.Value == null)
                {
                    _logger.LogWarning("[急停解除][失败] 读回 PLC 输入失败，第 {Attempt}/{MaxRetries} 次：{Message}",
                        attempt, maxRetries, readBack.Message);
                }
                else if (!readBack.Value.IsEmergencyStop)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _lastPlcInputs = readBack.Value;
                        SetUiState(TestUIState.AwaitingReset);
                        AddLog("急停信号已确认释放，请复位后重新启动");
                    });

                    _logger.LogWarning("[急停解除][确认成功] DT123=0，状态切换 AwaitingReset");
                    return true;
                }
                else
                {
                    _logger.LogWarning("[急停解除][失败] DT123 仍为 1，保持 EmergencyStop，第 {Attempt}/{MaxRetries} 次",
                        attempt, maxRetries);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[急停解除][失败] 清除或读回异常，第 {Attempt}/{MaxRetries} 次", attempt, maxRetries);
            }
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SetUiState(TestUIState.EmergencyStop);
            AddLog("急停解除失败：DT123 未确认释放，请检查 PLC/急停按钮状态后重试");
            _ = _notificationService.ShowWarningAsync(
                "急停信号未确认释放，请检查 PLC/急停按钮状态后再次解除。",
                "急停解除失败");
        });

        _logger.LogWarning("[急停解除][失败] DT123 未确认释放，保持 EmergencyStop");
        return false;
    }

    /// <summary>启动前复核，委托给 InspectionStartValidator</summary>
    private string? ValidateStartConditions()
    {
        var request = new InspectionStartValidationRequest
        {
            UiState = UiState,
            ModelName = ModelName,
            SerialNumber = SerialNumber,
            SchemeName = SchemeName,
            OperatorName = OperatorName,
            IsSchemeNameInvalid = IsSchemeNameInvalid,
            IsPlcConnected = IsPlcConnected,
            IsDmmConnected = IsDmmConnected,
            PlcInputs = _lastPlcInputs,
            Config = _inspectionEngine?.Config ?? new InspectionConfig(),
            UiItemCount = TestItems.Count
        };

        var result = InspectionStartValidator.Validate(request);
        if (!result.IsValid)
        {
            _logger.LogWarning("[启动复核][拒绝] {Message}", result.ErrorMessage);
        }
        return result.ErrorMessage;
    }

    #region 统一调试命令（Fake 和半实物共用）

    /// <summary>
    /// 调试启动：写 DT120=1，走 PLC 轮询启动复核链路，不直接 RunInspectionAsync。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugStartAsync()
    {
        if (_inspectionEngine == null) return;

        // ── 重复启动快速忽略（不写 DT120）──
        if (UiState == TestUIState.Testing || _inspectionStarted || _currentControlAction == InspectionControlAction.Starting)
        {
            _logger.LogInformation("[调试面板][启动请求][忽略] 当前已在检测中，来源=DebugPanel");
            return;
        }

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

        _logger.LogWarning("[调试按钮][启动][请求] 上位机写入 DT120=1，等待 PLC 轮询统一消费");

        var result = await _plcDevice.RequestStartAsync(CancellationToken.None).ConfigureAwait(false);
        if (!HandleControlSignalWriteResult(result, "调试面板", "DT120", "启动"))
        {
            return;
        }
    }

    /// <summary>
    /// 调试停止：写 DT122=1，成功后执行停止收口流程。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugStopAsync()
    {
        _logger.LogWarning("[调试按钮][停止][请求] 上位机写入 DT122=1，等待 PLC 轮询统一消费");

        // 暂停 UI 轮询，避免写信号时与 Polling 产生并发 pending
        SuspendPlcPolling();
        try
        {
            var result = await _plcDevice.RequestStopAsync(CancellationToken.None).ConfigureAwait(false);
            if (!HandleControlSignalWriteResult(result, "调试面板", "DT122", "停止"))
            {
                return;
            }
        }
        finally
        {
            ResumePlcPolling();
        }
    }

    /// <summary>
    /// 调试复位：写 DT121=1，写成功后直接执行复位收口（不再读回确认）。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugResetAsync()
    {
        _logger.LogWarning("[调试按钮][复位][请求] 上位机写入 DT121=1，等待 PLC 轮询统一消费");

        SuspendPlcPolling();
        try
        {
            var result = await _plcDevice.RequestResetAsync(CancellationToken.None).ConfigureAwait(false);
            if (!HandleControlSignalWriteResult(result, "调试面板", "DT121", "复位"))
            {
                return;
            }
        }
        finally
        {
            ResumePlcPolling();
        }
    }

    /// <summary>
    /// 调试急停：写 DT123=1，成功后执行急停收口。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugEmergencyStopAsync()
    {
        _logger.LogWarning("[调试按钮][急停][请求] 上位机写入 DT123=1，等待 PLC 轮询统一消费");

        SuspendPlcPolling();
        try
        {
            var result = await _plcDevice.RequestEmergencyStopAsync(CancellationToken.None).ConfigureAwait(false);
            if (!HandleControlSignalWriteResult(result, "调试面板", "DT123", "急停"))
            {
                return;
            }
        }
        finally
        {
            ResumePlcPolling();
        }
    }

    #endregion

    /// <summary>
    /// 半实物/Fake 调试按钮模拟的是本机动作入口，写请求已发出但写响应超时时继续执行对应流程。
    /// 明确 PLC 未连接或发生 Modbus 错误时仍拒绝继续，避免在真实通信异常下制造假流程。
    /// </summary>
    private bool HandleControlSignalWriteResult(PlcOperationResult result, string sourceName, string signalName, string actionName)
    {
        if (result.IsSuccess)
        {
            AddLog($"[{sourceName}] {actionName}请求已发送，等待 PLC 轮询统一处理");
            return true;
        }

        if (!_plcDevice.IsConnected)
        {
            AddLog($"[{sourceName}] 写入 {signalName}=1 失败：PLC 未连接");
            _logger.LogWarning("[{Source}][审计] {Signal}=1 写入失败且 PLC 未连接，允许重试{Action}请求：{Message}",
                sourceName, signalName, actionName, result.Message);
            return false;
        }

        AddLog($"[{sourceName}] 写入 {signalName}=1 失败：{result.Message}");
        _logger.LogWarning("[{Source}][审计] {Signal}=1 写入失败，允许重试{Action}请求：{Message}",
            sourceName, signalName, actionName, result.Message);
        return false;
    }

    /// <summary>
    /// 半实物/真实模式复位按钮（写 DT121=1，写后等待 PLC 轮询统一消费）。
    /// 真实模式下工人按实体按钮时由 PLC 轮询触发 ExecuteResetFlowAsync，
    /// 上位机按钮作为备用入口。
    /// </summary>
    [RelayCommand]
    private async Task TriggerPlcResetAsync()
    {
        var result = await _plcDevice.RequestResetAsync(CancellationToken.None).ConfigureAwait(false);

        if (!HandleControlSignalWriteResult(result, "复位", "DT121", "复位"))
        {
            return;
        }

        _logger.LogWarning("[复位按钮][请求] 已写入 DT121=1，等待 PLC 轮询统一消费");
    }

    /// <summary>
    /// 在后台线程执行检测（异步非阻塞 UI）。
    /// </summary>
    private async Task RunInspectionAsync(string barcode, string modelName, string operatorName)
    {
        if (_inspectionEngine == null) return;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            SetUiState(TestUIState.Testing);
        });

        int runVersion = Interlocked.Increment(ref _inspectionRunVersion);

        var result = await Task.Run(async () =>
        {
            return await _inspectionEngine.RunInspectionAsync(
                barcode, modelName, operatorName, CancellationToken.None);
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
            if (IsExpectedControlStopReason(result.StopReason))
            {
                _logger.LogWarning("[运行页][审计] 检测因 {StopReason} 收口，中止提示交由对应流程处理", result.StopReason);
                return;
            }

            if (result.StopReason == InspectionStopReason.SingleItemNg)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AddLog($"❎ {result.ErrorMessage}");
                });
                _logger.LogWarning("[运行页][审计] 单项 NG 停止: {ErrorMessage}", result.ErrorMessage);
                return;
            }

            // 非控制类异常（PLC写失败/安全清理失败/RelayTimeout/DMM异常/未分类异常）:
            // 先切 UI 为 Error 状态，再弹窗告知操作员，避免弹窗时顶部仍显示"测试中"
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SetUiState(TestUIState.Error);
                AddLog($"❌ {result.ErrorMessage}");
            });

            string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "检测已中止，请查看运行日志。"
                : $"检测中止：{result.ErrorMessage}";

            _logger.LogWarning("[运行页][审计] {Message}", message);
            await _notificationService.ShowWarningAsync(message, "检测中止").ConfigureAwait(false);
        }
        else
        {
            // 正常完成 → 处理保存弹窗
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var msg = result.IsAllPassed
                    ? $"✅ 检测完成: 良品 (耗时{result.Duration.TotalSeconds:F1}s)"
                    : $"❌ 检测完成: 不良 (耗时{result.Duration.TotalSeconds:F1}s)";
                AddLog(msg);

                await OnAllPinsTestedAsync();
            });
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
            _inspectionEngine.StepStarted += OnStepStarted;
            _inspectionEngine.StepCompleted += OnStepCompleted;
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
            _inspectionEngine.StepStarted -= OnStepStarted;
            _inspectionEngine.StepCompleted -= OnStepCompleted;
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

    /// <summary>检测引擎状态变更 → 映射为 UI 状态。</summary>
    // StateChanged 事件已删除。UI 状态由控制流（Start/Stop/Reset/EmergencyStop）
    // 和 HandleInspectionResultAsync 统一决定。

    /// <summary>
    /// 单步检测开始回调。
    /// </summary>
    private void OnStepStarted(object? sender, StepStartedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (ShouldIgnoreInspectionCallback())
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
            if (ShouldIgnoreInspectionCallback())
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

            return InspectionMeasurementEvaluator.ResolveContinuityState(value, testPoint.ContinuityThresholdOhm);
        }

        return $"{InputValidationHelper.FormatResistanceValue(value)} Ω";
    }

    // InspectionCompleted 事件已删除。
    // 检测结果处理统一在 RunInspectionAsync 返回后的 HandleInspectionResultAsync 中处理。

    /// <summary>
    /// 检测引擎日志订阅。
    /// 复位或忽略回调期间不输出旧检测日志，避免旧任务晚到的日志出现在已清空的日志区。
    /// </summary>
    private void OnInspectionLogMessage(object? sender, string message)
    {
        if (ShouldIgnoreInspectionCallback())
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
        _controlActionLock.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}
