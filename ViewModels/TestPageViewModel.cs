using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Fakes;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
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
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using System.Diagnostics;

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
/// CompletedPass:  检测完成-良品（OK）— 等待操作员复位
/// CompletedFail:  检测完成-不良（NG）— 等待操作员复位
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
    private readonly IDialogCoordinator _dialogCoordinator;
    private readonly ILogger<TestPageViewModel> _logger;
    private readonly IPlanStorageService _planStorageService;
    private readonly IReferenceSelectionStateService _referenceSelectionStateService;
    private readonly IOperatorStateService _operatorStateService;
    private readonly IDeviceSettingsService _settingsService;
    private readonly ITestRecordStorage _testRecordStorage;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;

    // 硬件服务

    private readonly IDeviceConnectionManager _deviceManager;
    private readonly IMultimeterDevice _multimeterDevice;
    private readonly IPlcDevice _plcDevice;
    private readonly InspectionEngine? _inspectionEngine;

    #endregion

    #region 字段

    private DispatcherTimer? _clockTimer;

    /// <summary>运行页测试耗时刷新定时器，独立于顶部系统时钟。</summary>
    private DispatcherTimer? _elapsedTimeTimer;

    /// <summary>界面显示用的本轮检测耗时计时器。</summary>
    private readonly Stopwatch _displayInspectionStopwatch = new();

    /// <summary>PLC 状态轮询定时器（取代旧传感器模拟）</summary>
    private DispatcherTimer? _plcPollingTimer;

    /// <summary>PLC 轮询非阻塞门禁。上一轮未结束时跳过本轮，避免控制信号快照排队。</summary>
    private int _plcPollingInProgress;

    /// <summary>PLC 轮询嵌套暂停计数。只有计数回到 0 后才允许恢复低优先级轮询。</summary>
    private int _plcPollingSuspendCount;

    /// <summary>当前轮询操作的取消令牌源。供控制动作取消正在进行的 Poll 请求。</summary>
    private CancellationTokenSource? _plcPollingOperationCts;

    /// <summary>Start/Stop/Reset/EmergencyStop/Finish 统一动作门禁。</summary>
    private readonly SemaphoreSlim _controlActionLock = new(1, 1);

    /// <summary>控制动作请求编号，用于把同一动作的请求、等待、进入和退出日志串起来。</summary>
    private long _controlActionRequestSequence;

    /// <summary>当前控制动作的请求编号，供统一退出日志关联进入日志。</summary>
    private long _activeControlActionRequestId;

    /// <summary>控制锁租约是否已被当前动作持有，独立于动作分类状态，防止重复释放信号量。</summary>
    private int _controlActionHeld;

    /// <summary>急停请求锁存位。急停正在等待普通动作结束时，后续轮询不得再次丢弃该请求。</summary>
    private int _emergencyStopPending;

    /// <summary>停止流程期间收到的复位请求，停止收口后只补执行一次。</summary>
    private bool _pendingResetAfterStop;

    /// <summary>当前正在执行的控制动作。它不是 UI 状态，只用于跨动作互斥和日志诊断。</summary>
    private InspectionControlAction _currentControlAction = InspectionControlAction.None;

    /// <summary>轮询中上一次 PLC 输入快照，用于检测信号变化</summary>
    private PlcControlSignals? _lastPlcInputs;

    /// <summary>轮询中上一次 DT309/DT310 安装拒绝快照，用于诊断和防重复处理。</summary>
    private WorkstationInstallRejectSignals? _lastInstallRejectSignals;

    /// <summary>当前安装拒绝弹窗是否正在显示。</summary>
    private int _installRejectDialogInProgress;

    /// <summary>已经处理过的安装拒绝键，避免同一非零值持续轮询重复弹窗。</summary>
    private string? _handledInstallRejectKey;

    /// <summary>DT309/DT310 读取失败诊断日志限频时间。</summary>
    private DateTime _lastInstallRejectReadFailureLogUtc = DateTime.MinValue;

    /// <summary>是否已向引擎发起检测（防止 DT120=1 重复触发多次 RunInspectionAsync）</summary>
    private bool _inspectionStarted;

    /// <summary>是否正在处理检测完成收口（防止重复保存、重复清 PLC）。</summary>
    private bool _inspectionCompletionInProgress;

    /// <summary>重复测试检查防抖取消源。</summary>
    private CancellationTokenSource? _duplicateCheckCts;

    /// <summary>机种和序列号输入完成通知的防抖取消源。</summary>
    private CancellationTokenSource? _productIdentityNotifyCts;

    /// <summary>最近一次已通知 PLC 的机种和序列号组合键。</summary>
    private string? _lastNotifiedProductIdentityKey;

    /// <summary>扫码批量赋值期间抑制属性回调触发手动输入防抖通知。</summary>
    private bool _isApplyingScannerBarcode;

    /// <summary>当前机种和序列号组合的本轮重复记录确认状态。</summary>
    private enum CurrentSerialVerificationState
    {
        NotConfirmed,
        Checking,
        AwaitingDuplicateDecision,
        Confirmed,
        Failed
    }

    /// <summary>当前序列号确认状态，只有 Confirmed 才允许进入正式启动复核。</summary>
    private CurrentSerialVerificationState _currentSerialVerificationState;

    /// <summary>已完成确认的机种和序列号组合键，防止确认结果被其他输入复用。</summary>
    private string? _confirmedSerialKey;

    /// <summary>最近一次重复记录查询失败原因，仅用于诊断日志，不直接展示内部状态名。</summary>
    private string? _duplicateCheckFailureMessage;

    /// <summary>已提示并允许继续的机种和序列号组合，避免同一组合反复弹窗。</summary>
    private string? _lastDuplicateCheckKey;

    /// <summary>程序主动清空机种/SN 时抑制重复测试检查。</summary>
    private bool _suppressDuplicateCheck;

    /// <summary>是否正在显示急停对话框（防止轮询重复弹窗）</summary>
    private bool _isShowingEmergencyDialog;

    /// <summary>本轮 DT123 急停弹窗是否已由操作员解除确认。DT123 恢复 0 后重置。</summary>
    private bool _emergencyDialogAcknowledged;

    /// <summary>DT122 停止信号是否已处理，防止轮询期间重复刷日志。</summary>
    private bool _stopSignalHandled;

    /// <summary>
    /// 当前一次 DT121 复位请求是否已被接收处理。
    /// 从复位开始到弹窗后最终清除 DT121 完成期间保持 true，
    /// 用于吸收用户快速反复按下实体复位按钮产生的重复请求。
    /// </summary>
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

    /// <summary>设备断线恢复收口门禁，防止 PLC/DMM/扫描枪重复事件重复停止本轮检测。</summary>
    private int _deviceDisconnectHandling;

    /// <summary>选择作业员或参照机种弹窗打开期间，运行页暂时忽略产品条码事件。</summary>
    private bool _suspendRunPageBarcodeHandling;

    /// <summary>正常 OK 结果的延时清除任务取消源。</summary>
    private CancellationTokenSource? _finalResultAutoClearCts;

    /// <summary>正常 OK 结果的延时清除任务，便于控制动作安全等待其退出。</summary>
    private Task? _finalResultAutoClearTask;

    /// <summary>串行化最终结果延时任务的替换和取消，避免竞态覆盖任务引用。</summary>
    private readonly SemaphoreSlim _finalResultAutoClearLock = new(1, 1);

    /// <summary>
    /// 复位后等待 DT120 启动请求释放。
    /// 复位流程会主动清 DT120，但真实 PLC 或按钮链路可能需要一个轮询周期才读回 0。
    /// 该标志为 true 时，即使读到 DT120=1 也不允许重新启动检测。
    /// </summary>
    private bool _waitDt120ReleaseAfterReset;

    /// <summary>当前已加载方案的业务版本，保存检测记录时写入 CSV。</summary>
    private int _currentPlanVersion = 1;

    /// <summary>方案加载版本号。机种或方案变更后递增，用于丢弃晚到的异步加载结果。</summary>
    private int _planLoadVersion;

    /// <summary>仅由扫码设置的待自动选方案机种，手动输入不触发唯一方案自动选择。</summary>
    private string? _pendingBarcodePlanAutoSelectMachine;

    /// <summary>最近一次已提示的参照机种不一致组合，避免 Enter 和失焦重复弹窗。</summary>
    private string? _lastReferenceMismatchPromptKey;

    /// <summary>最近一次已提示的异常扫码事件，使用时间戳区分内容相同的后续扫码。</summary>
    private string? _lastInvalidBarcodePromptKey;

    /// <summary>当前方案列表或检测项目仍在加载，加载期间不能显示为可启动。</summary>
    private bool _isPlanLoading;

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
    private int _scannerFrameWarningShowing;

    /// <summary>
    /// Reset Timeout 错误弹窗是否已显示（0=未显示，1=已显示）。
    /// 使用 Interlocked.Exchange 防重，DT121=0 后重置为 0。
    /// </summary>
    private int _resetFailureNotificationShown;

    /// <summary>复位失败后，旧 DT121 尚未确认回到 0，实体复位入口暂不重新武装。</summary>
    private bool _resetRearmPending;

    /// <summary>后台清理旧 DT121 的任务门禁，防止同一时间重复写入。</summary>
    private int _resetRearmInProgress;

    /// <summary>上一次清理旧 DT121 的时间，用于限制轮询重试频率。</summary>
    private DateTime _lastResetRearmAttemptUtc = DateTime.MinValue;

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

    /// <summary>启动拒绝的业务分类，用于统一操作员提示和文件日志诊断。</summary>
    private enum StartRejectReason
    {
        None,
        MissingModel,
        MissingSerialNumber,
        InvalidPlan,
        NoTestItems,
        MissingOperator,
        PlcOffline,
        DmmOffline,
        ResetNotReleased,
        StopNotReleased,
        EmergencyStopActive,
        Busy,
        AwaitingReset,
        DuplicateStart,
        PlanLoading,
        SerialNotConfirmed,
        DuplicateCheckInProgress,
        DuplicateDecisionPending,
        DuplicateCheckFailed,
        Unknown
    }

    /// <summary>同一次启动请求共用的拒绝结果：操作员文本不含技术地址，诊断文本保留完整快照。</summary>
    private sealed record InspectionStartValidationResult(
        bool CanStart,
        string OperatorMessage,
        string DiagnosticMessage,
        StartRejectReason Reason);

    #endregion

    #region 构造函数

    public TestPageViewModel(
        INavigationService navigationService,
        INotificationService notificationService,
        IDialogCoordinator dialogCoordinator,
        IOperatorStateService operatorStateService,
        ILogger<TestPageViewModel> logger,

        IDeviceSettingsService settingsService,
        IPlanStorageService planStorageService,
        IReferenceSelectionStateService referenceSelectionStateService,
        IDeviceConnectionManager deviceManager,
        IScannerBarcodeService scannerBarcodeService,
        ITestRecordStorage testRecordStorage,
        IPlcDevice plcDevice,
        IMultimeterDevice multimeterDevice,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        InspectionEngine? inspectionEngine = null)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _dialogCoordinator = dialogCoordinator ?? throw new ArgumentNullException(nameof(dialogCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _operatorStateService = operatorStateService ?? throw new ArgumentNullException(nameof(operatorStateService));
        _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
        _referenceSelectionStateService = referenceSelectionStateService ?? throw new ArgumentNullException(nameof(referenceSelectionStateService));

        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _testRecordStorage = testRecordStorage ?? throw new ArgumentNullException(nameof(testRecordStorage));
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _multimeterDevice = multimeterDevice ?? throw new ArgumentNullException(nameof(multimeterDevice));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _inspectionEngine = inspectionEngine;

        // 初始化 UiState 为待机
        UiState = TestUIState.Ready;

        InitializeClock();
        _referenceSelectionStateService.SelectionChanged += OnReferenceSelectionChanged;
        ApplyReferenceSelection();
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

    [ObservableProperty]
    private string _referenceSeries = string.Empty;

    [ObservableProperty]
    private string _referenceMachineType = string.Empty;

    [ObservableProperty]
    private string _referenceWorkstation = string.Empty;

    [ObservableProperty]
    private string _referenceDisplayText = "未选择参照信息";

    [ObservableProperty]
    private bool _isReferenceMachineMismatch;

    partial void OnSchemeNameChanged(string value)
    {
        int loadVersion = Interlocked.Increment(ref _planLoadVersion);

        if (string.IsNullOrWhiteSpace(value))
        {
            SelectedPlanName = null;
            IsSchemeNameInvalid = false;
            TestItems.Clear();
            AddLog("方案名称已清空，检测项目列表已清空");
            _isPlanLoading = false;
            RefreshReadyOrCanStartState();
            return;
        }

        string? matched = PlanNameOptions.FirstOrDefault(
            planName => string.Equals(planName, value, StringComparison.OrdinalIgnoreCase));

        if (matched != null)
        {
            SelectedPlanName = matched;
            IsSchemeNameInvalid = false;
            _isPlanLoading = true;
            RefreshReadyOrCanStartState();
            _ = LoadPlanItemsAsync(loadVersion, ModelName, value);
        }
        else
        {
            SelectedPlanName = null;
            ValidateCurrentSchemeName();
            TestItems.Clear();
            AddLog($"⚠️ 方案 [{value}] 不在当前机种 [{ModelName}] 的方案列表中，检测列表已清空");
            _isPlanLoading = false;
            RefreshReadyOrCanStartState();
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

    /// <summary>PLC 三态连接状态（供枚举绑定）</summary>
    [ObservableProperty]
    private DeviceConnectionStatus _plcConnectionStatus = DeviceConnectionStatus.Disconnected;

    /// <summary>万用表三态连接状态（供枚举绑定）</summary>
    [ObservableProperty]
    private DeviceConnectionStatus _dmmConnectionStatus = DeviceConnectionStatus.Disconnected;

    /// <summary>扫描枪三态连接状态（供枚举绑定）</summary>
    [ObservableProperty]
    private DeviceConnectionStatus _scannerConnectionStatus = DeviceConnectionStatus.Disconnected;

    /// <summary>PLC 启动请求状态（DT120 值），供 UI 显示"等待 PLC 启动"</summary>
    [ObservableProperty]
    private bool _isPlcStartRequested = false;

    /// <summary>UI 状态文本（测试状态大面板显示）</summary>
    [ObservableProperty]
    private string _sensorStatusText = "待机中";

    [ObservableProperty]
    private string _elapsedSecondsText = "0.0";

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

        _elapsedTimeTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _elapsedTimeTimer.Tick += OnElapsedTimeTimerTick;
    }

    /// <summary>每 100ms 刷新运行页耗时，使用一位小数并保持单位独立显示。</summary>
    private void OnElapsedTimeTimerTick(object? sender, EventArgs e)
    {
        if (_displayInspectionStopwatch.IsRunning)
        {
            ElapsedSecondsText = _displayInspectionStopwatch.Elapsed.TotalSeconds
                .ToString("F1", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>在真正开始调用检测引擎前启动本轮界面计时。</summary>
    private void StartElapsedTimeTimer()
    {
        _displayInspectionStopwatch.Restart();
        ElapsedSecondsText = "0.0";
        _elapsedTimeTimer?.Start();
    }

    /// <summary>检测返回后停止界面计时，并以检测引擎统一耗时口径定格。</summary>
    private void StopElapsedTimeTimer(TimeSpan? finalDuration = null)
    {
        _displayInspectionStopwatch.Stop();
        if (finalDuration.HasValue)
        {
            ElapsedSecondsText = finalDuration.Value.TotalSeconds
                .ToString("F1", CultureInfo.InvariantCulture);
        }
        _elapsedTimeTimer?.Stop();
    }

    /// <summary>完整复位或新一轮有效启动准备完成后归零耗时。</summary>
    private void ResetElapsedTime()
    {
        StopElapsedTimeTimer();
        _displayInspectionStopwatch.Reset();
        ElapsedSecondsText = "0.0";
    }

    [RelayCommand(CanExecute = nameof(CanReconnectElectricalDevice))]
    private async Task ReconnectPlcAsync()
    {
        if (!CanReconnectElectricalDevice())
        {
            _logger.LogWarning("[设备重连][PLC][拒绝] 检测正在启动或运行中，不允许手动重连");
            return;
        }

        await _deviceManager.ReconnectDeviceAsync("PLC");
    }

    [RelayCommand]
    private Task ReconnectScannerAsync()
    {
        if (_suspendRunPageBarcodeHandling)
            return Task.CompletedTask;

        _suspendRunPageBarcodeHandling = true;
        BarcodeParsedEventArgs? confirmedBarcode = null;
        AddLog("🔄 正在打开扫描枪连接恢复窗口...");
        try
        {
            var dialog = _serviceProvider.GetRequiredService<ScannerRecoveryDialog>();
            dialog.Owner = Application.Current?.MainWindow;
            if (dialog.ShowDialog() == true)
                confirmedBarcode = dialog.VerifiedProductBarcode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫描仪重连异常");
            AddLog($"❌ 扫描仪重连异常: {ex.Message}");
        }
        finally
        {
            _suspendRunPageBarcodeHandling = false;
        }

        if (confirmedBarcode is not null)
        {
            _logger.LogWarning(
                "[扫描枪恢复][审计] 用户确认使用恢复弹窗条码: Model={Model}, Serial={Serial}",
                confirmedBarcode.ModelName,
                confirmedBarcode.SerialPart);
            AddLog("✅ 已确认使用恢复弹窗中的当前产品条码。");
            RouteScannerBarcodeParsed(confirmedBarcode);
        }
        else
        {
            AddLog("扫描枪连接恢复窗口已关闭，未接续条码；请重新扫描当前产品条码。");
        }

        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanReconnectElectricalDevice))]
    private async Task ReconnectDmmAsync()
    {
        if (!CanReconnectElectricalDevice())
        {
            _logger.LogWarning("[设备重连][DMM][拒绝] 检测正在启动或运行中，不允许手动重连");
            return;
        }

        await _deviceManager.ReconnectDeviceAsync("DMM");
    }

    /// <summary>
    /// 判断 PLC 和万用表是否允许手动重连。
    /// 检测中禁止先断开再连接，避免旧检测继续使用不确定的设备连接。
    /// </summary>
    private bool CanReconnectElectricalDevice()
        => !HasActiveInspectionContext();

    /// <summary>
    /// 判断当前是否存在需要设备断线收口的检测上下文。
    /// 手动重连会产生 Connecting/Disconnected 短暂状态，不能再用旧的 wasConnected 单独判定异常。
    /// </summary>
    private bool HasActiveInspectionContext()
    {
        return _currentControlAction == InspectionControlAction.Starting
            || _inspectionEngine?.IsRunning == true
            || UiState is TestUIState.Testing or TestUIState.Paused;
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
    /// <summary>Fake 和半实物联调保留技术地址；真实模式只显示操作员可理解的文字。</summary>
    private bool ShowTechnicalDetails => IsFakeMode || IsSemiPhysicalDebugMode;
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
        return InspectionStartValidator.Validate(BuildStartValidationRequest()).IsValid;
    }

    /// <summary>
    /// 建立运行页唯一的启动条件快照，状态灯与正式启动复核均使用该快照，避免显示和实际校验不一致。
    /// </summary>
    private InspectionStartValidationRequest BuildStartValidationRequest(bool allowCurrentStartingAction = false)
    {
        return new InspectionStartValidationRequest
        {
            UiState = UiState,
            ModelName = ModelName,
            SerialNumber = SerialNumber,
            SchemeName = SchemeName,
            OperatorName = OperatorName,
            IsSchemeNameInvalid = IsSchemeNameInvalid,
            IsPlcConnected = IsPlcConnected,
            IsDmmConnected = IsDmmConnected,
            IsPlanLoading = _isPlanLoading,
            IsCurrentSerialConfirmed = IsCurrentSerialVerified(),
            IsDuplicateCheckInProgress = _currentSerialVerificationState == CurrentSerialVerificationState.Checking,
            IsDuplicateDecisionPending = _currentSerialVerificationState == CurrentSerialVerificationState.AwaitingDuplicateDecision,
            IsDuplicateCheckFailed = _currentSerialVerificationState == CurrentSerialVerificationState.Failed,
            // 正式启动复核在取得 Starting 门禁后执行；该门禁本身不应反向拒绝本次启动。
            IsControlActionInProgress = _currentControlAction != InspectionControlAction.None
                                       && !(allowCurrentStartingAction
                                            && _currentControlAction == InspectionControlAction.Starting),
            IsInspectionEngineRunning = _inspectionEngine?.IsRunning == true,
            PlcInputs = _lastPlcInputs,
            Config = _inspectionEngine?.Config ?? new InspectionConfig(),
            UiItemCount = TestItems.Count
        };
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
            TestUIState.Paused => "已停止，请复位",
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

        NotifyChangeOperatorCanExecuteChanged();
        NotifyChangeReferenceSelectionCanExecuteChanged();
        NotifyReconnectCommandsCanExecuteChanged();
    }

    /// <summary>
    /// 刷新作业员切换命令状态。命令绑定属于 WPF UI 对象，必须回到 UI Dispatcher 线程通知。
    /// </summary>
    private void NotifyChangeOperatorCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            _logger.LogDebug("[作业员命令] Dispatcher 不可用，跳过 CanExecute 刷新");
            return;
        }

        if (dispatcher.CheckAccess())
        {
            ChangeOperatorCommand.NotifyCanExecuteChanged();
            return;
        }

        _ = dispatcher.BeginInvoke(
            new Action(ChangeOperatorCommand.NotifyCanExecuteChanged),
            DispatcherPriority.Normal);
    }

    /// <summary>刷新运行页重新选择参照命令的可执行状态。</summary>
    private void NotifyChangeReferenceSelectionCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        if (dispatcher.CheckAccess())
            ChangeReferenceSelectionCommand.NotifyCanExecuteChanged();
        else
            _ = dispatcher.BeginInvoke(
                new Action(ChangeReferenceSelectionCommand.NotifyCanExecuteChanged),
                DispatcherPriority.Normal);
    }

    /// <summary>
    /// 刷新 PLC/DMM 手动重连命令状态，确保 Starting 一进入就禁用按钮。
    /// </summary>
    private void NotifyReconnectCommandsCanExecuteChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        void Notify()
        {
            ReconnectPlcCommand.NotifyCanExecuteChanged();
            ReconnectDmmCommand.NotifyCanExecuteChanged();
        }

        if (dispatcher.CheckAccess())
            Notify();
        else
            _ = dispatcher.BeginInvoke(new Action(Notify), DispatcherPriority.Normal);
    }

    /// <summary>
    /// 进入运行控制动作门禁。
    /// Start 使用非排队尝试，Stop/Reset/EmergencyStop/Finish 在取消或锁存请求后直接等待信号量。
    /// </summary>
    private async Task<bool> TryEnterControlActionAsync(
        InspectionControlAction action,
        bool waitForLock = false,
        CancellationToken ct = default)
    {
        long actionRequestId = Interlocked.Increment(ref _controlActionRequestSequence);
        var waitStopwatch = Stopwatch.StartNew();
        _logger.LogWarning(
            "[运行控制][请求] Action={Action}, ActionRequestId={ActionRequestId}, CurrentAction={CurrentAction}, UiState={UiState}",
            action, actionRequestId, _currentControlAction, UiState);

        if (action == InspectionControlAction.Starting
            && Volatile.Read(ref _emergencyStopPending) != 0)
        {
            _logger.LogWarning(
                "[运行控制][拒绝] 急停请求已锁存，禁止新的 Starting 进入，ActionRequestId={ActionRequestId}",
                actionRequestId);
            return false;
        }

        bool entered;
        if (waitForLock)
        {
            _logger.LogWarning(
                "[运行控制][等待] Action={Action}, ActionRequestId={ActionRequestId}, CurrentAction={CurrentAction}",
                action, actionRequestId, _currentControlAction);
            await _controlActionLock.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
        }
        else
        {
            entered = await _controlActionLock.WaitAsync(0).ConfigureAwait(false);
            if (!entered)
            {
                _logger.LogWarning(
                    "[运行控制][拒绝] Action={Action}, ActionRequestId={ActionRequestId}, CurrentAction={CurrentAction}, UiState={UiState}, LockWaitElapsedMs={ElapsedMs}",
                    action, actionRequestId, _currentControlAction, UiState, waitStopwatch.ElapsedMilliseconds);
                return false;
            }
        }

        Interlocked.Exchange(ref _controlActionHeld, 1);
        Volatile.Write(ref _activeControlActionRequestId, actionRequestId);
        _currentControlAction = action;
        NotifyChangeOperatorCanExecuteChanged();
        NotifyChangeReferenceSelectionCanExecuteChanged();
        NotifyReconnectCommandsCanExecuteChanged();
        _logger.LogWarning(
            "[运行控制][进入] Action={Action}, ActionRequestId={ActionRequestId}, UiState={UiState}, InspectionRunVersion={InspectionRunVersion}, EngineIsRunning={EngineIsRunning}, ExecutionStage={ExecutionStage}, LockWaitElapsedMs={ElapsedMs}",
            action, actionRequestId, UiState, Volatile.Read(ref _inspectionRunVersion), _inspectionEngine?.IsRunning ?? false,
            _inspectionEngine?.CurrentExecutionStage ?? "Unavailable", waitStopwatch.ElapsedMilliseconds);
        return true;
    }

    /// <summary>
    /// 离开运行控制动作门禁。是否持有信号量由独立租约标志保护，不能依据 CurrentAction 推断。
    /// </summary>
    private void ExitControlAction(InspectionControlAction action)
    {
        if (Interlocked.Exchange(ref _controlActionHeld, 0) == 0)
        {
            _logger.LogError(
                "[运行控制][退出][防御] Action={Action} 重复退出，未再次释放控制锁，CurrentAction={CurrentAction}",
                action, _currentControlAction);
            return;
        }

        _logger.LogWarning("[运行控制][退出] Action={Action}, UiState={UiState}", action, UiState);
        _logger.LogWarning(
            "[运行控制][退出] Action={Action}, ActionRequestId={ActionRequestId}, InspectionRunVersion={InspectionRunVersion}, EngineIsRunning={EngineIsRunning}, ExecutionStage={ExecutionStage}",
            action, Volatile.Read(ref _activeControlActionRequestId), Volatile.Read(ref _inspectionRunVersion),
            _inspectionEngine?.IsRunning ?? false, _inspectionEngine?.CurrentExecutionStage ?? "Unavailable");
        Volatile.Write(ref _activeControlActionRequestId, 0);
        _currentControlAction = InspectionControlAction.None;
        NotifyChangeOperatorCanExecuteChanged();
        NotifyReconnectCommandsCanExecuteChanged();
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
            or InspectionStopReason.Terminate;
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

    }

    /// <summary>
    /// 构建启动复核结果。该方法只读取当前快照，不弹窗、不写 PLC。
    /// </summary>
    private InspectionStartValidationResult BuildStartValidationResult(bool allowCurrentStartingAction = false)
    {
        var validation = InspectionStartValidator.Validate(
            BuildStartValidationRequest(allowCurrentStartingAction));
        if (validation.IsValid)
            return new(true, string.Empty, "启动条件校验通过", StartRejectReason.None);

        var (reason, operatorMessage) = GetStartRejectMessage(allowCurrentStartingAction);
        string diagnosticMessage =
            $"Reason={reason}, ValidatorMessage={validation.ErrorMessage}, UiState={UiState}, " +
            $"SerialVerificationState={_currentSerialVerificationState}, " +
            $"ConfirmedSerialKey={_confirmedSerialKey}, CurrentSerialKey={BuildCurrentSerialKey()}, " +
            $"DuplicateCheckFailureMessage={_duplicateCheckFailureMessage}, " +
            $"ControlAction={_currentControlAction}, PlcConnected={IsPlcConnected}, " +
            $"DmmConnected={IsDmmConnected}, PlanLoading={_isPlanLoading}, " +
            $"EngineRunning={_inspectionEngine?.IsRunning == true}, PlcInputs={_lastPlcInputs}";
        return new(false, operatorMessage, diagnosticMessage, reason);
    }

    /// <summary>为启动过程中的通信失败构造统一拒绝结果，操作员提示不暴露 PLC 地址。</summary>
    private static InspectionStartValidationResult CreateStartFailureResult(string operatorMessage, string diagnosticMessage)
        => new(false, operatorMessage, diagnosticMessage, StartRejectReason.Unknown);

    /// <summary>将当前快照映射为操作员可执行的启动拒绝提示。</summary>
    private (StartRejectReason Reason, string OperatorMessage) GetStartRejectMessage(
        bool allowCurrentStartingAction = false)
    {
        if (UiState is TestUIState.Testing
            or TestUIState.Paused
            or TestUIState.AwaitingReset
            or TestUIState.Resetting
            or TestUIState.Error)
            return (StartRejectReason.AwaitingReset, "当前状态需要先复位，无法启动检测。");
        if (UiState == TestUIState.EmergencyStop)
            return (StartRejectReason.EmergencyStopActive, "设备处于急停状态，请先复位后再启动检测。");
        if ((_currentControlAction != InspectionControlAction.None || UiState == TestUIState.Resetting)
            && !(allowCurrentStartingAction && _currentControlAction == InspectionControlAction.Starting))
            return (StartRejectReason.Busy, "系统正在处理其他动作，请等待当前操作完成。");
        if (_inspectionEngine?.IsRunning == true || UiState == TestUIState.Testing)
            return (StartRejectReason.DuplicateStart, "检测已在运行中，无需重复启动。");
        if (string.IsNullOrWhiteSpace(ModelName)) return (StartRejectReason.MissingModel, "请扫码或手动输入机种名称。");
        if (string.IsNullOrWhiteSpace(SerialNumber)
            || !InputValidationHelper.IsValidSerialNumber(SerialNumber))
            return (StartRejectReason.SerialNotConfirmed, "本次基板序列号尚未确认。请先扫码或手动输入序列号，再重新启动。");
        if (_currentSerialVerificationState == CurrentSerialVerificationState.Checking)
            return (StartRejectReason.DuplicateCheckInProgress, "正在检查该序列号的历史测试记录，请稍后重新启动。");
        if (_currentSerialVerificationState == CurrentSerialVerificationState.AwaitingDuplicateDecision)
            return (StartRejectReason.DuplicateDecisionPending, "请先处理当前的重复测试提醒，再重新启动。");
        if (_currentSerialVerificationState == CurrentSerialVerificationState.Failed)
            return (StartRejectReason.DuplicateCheckFailed, "序列号历史记录检查失败，请重新输入序列号后再试。");
        if (!IsCurrentSerialVerified())
            return (StartRejectReason.SerialNotConfirmed, "本次基板序列号尚未确认。请先扫码或手动输入序列号，再重新启动。");
        if (_isPlanLoading) return (StartRejectReason.PlanLoading, "当前方案仍在加载，请等待加载完成后再启动。");
        if (string.IsNullOrWhiteSpace(SchemeName) || IsSchemeNameInvalid) return (StartRejectReason.InvalidPlan, "请选择当前机种的有效检测方案。");
        if (TestItems.Count == 0) return (StartRejectReason.NoTestItems, "当前方案没有检测项目，请检查方案设置。");
        if (string.IsNullOrWhiteSpace(OperatorName)) return (StartRejectReason.MissingOperator, "请重新选择作业员。");
        if (!IsPlcConnected) return (StartRejectReason.PlcOffline, "PLC 未连接，请检查 PLC 电源、网线和系统设置。");
        if (!IsDmmConnected) return (StartRejectReason.DmmOffline, "万用表未连接，请检查万用表电源、网线和系统设置。");
        if (_lastPlcInputs?.IsResetRequested == true) return (StartRejectReason.ResetNotReleased, "请松开或检查复位按钮。");
        if (_lastPlcInputs?.IsStopRequested == true) return (StartRejectReason.StopNotReleased, "请松开停止按钮或先执行复位。");
        if (_lastPlcInputs?.IsEmergencyStop == true) return (StartRejectReason.EmergencyStopActive, "请解除急停并完成复位后再启动。");
        return (StartRejectReason.Unknown, "当前条件不满足，无法启动检测，请检查运行状态。");
    }

    /// <summary>唯一的启动拒绝出口：日志、弹窗和按场景需要清除的启动请求在此处一致执行。</summary>
    private async Task RejectStartAsync(
        InspectionStartValidationResult result,
        InspectionActionSource source,
        bool clearStartRequest = true)
    {
        _logger.LogWarning(
            "[启动复核][拒绝] Source={Source}, Reason={Reason}, ClearStartRequest={ClearStartRequest}, DiagnosticMessage={DiagnosticMessage}",
            source,
            result.Reason,
            clearStartRequest,
            result.DiagnosticMessage);
        AddLog($"启动拒绝：{result.OperatorMessage}");
        await _notificationService.ShowWarningAsync(result.OperatorMessage, "启动拒绝");
        if (clearStartRequest)
        {
            var clearResult = await ClearStartRequestWithSingleRetryAsync();
            if (clearResult.IsSuccess)
            {
                _logger.LogWarning("[启动复核][拒绝] 已安全清除启动请求，来源={Source}", source);
            }
            else
            {
                _logger.LogError(
                    "[启动复核][拒绝] DT120 清除失败，来源={Source}, Message={Message}",
                    source,
                    clearResult.Message);
                AddLog("DT120 清除失败，请检查 PLC");
            }
        }
    }

    /// <summary>清除启动请求，通信瞬态失败时只允许进行一次有限重试。</summary>
    private async Task<PlcOperationResult> ClearStartRequestWithSingleRetryAsync()
    {
        var firstResult = await _plcDevice.ClearStartRequestAsync(CancellationToken.None);
        if (firstResult.IsSuccess)
            return firstResult;

        _logger.LogWarning("[启动复核][拒绝] 第一次清除 DT120 失败，100ms 后重试：{Message}", firstResult.Message);
        await Task.Delay(100);

        var retryResult = await _plcDevice.ClearStartRequestAsync(CancellationToken.None);
        if (!retryResult.IsSuccess)
        {
            _logger.LogWarning("[启动复核][拒绝] 第二次清除 DT120 仍失败：{Message}", retryResult.Message);
        }

        return retryResult;
    }

    #endregion

    #region 中部 - 信息录入区

    [ObservableProperty]
    private string _modelName = string.Empty;

    /// <summary>清除运行页机种名称，后续方案和检测项目联动由 OnModelNameChanged 统一处理。</summary>
    [RelayCommand]
    private void ClearModelName()
    {
        ModelName = string.Empty;
    }

    [ObservableProperty]
    private string _serialNumber = string.Empty;

    [ObservableProperty]
    private string _operatorName = string.Empty;

    [ObservableProperty]
    private bool _isOperatorEditable = true;

    [RelayCommand(CanExecute = nameof(CanChangeOperator))]
    private async Task ChangeOperatorAsync()
    {
        var dialog = _serviceProvider.GetRequiredService<OperatorSelectionDialog>();
        dialog.Owner = Application.Current.MainWindow;
        _suspendRunPageBarcodeHandling = true;
        try
        {
            var confirmed = dialog.ShowDialog() == true;
            if (!confirmed || !_operatorStateService.HasOperator)
                return;

            OperatorName = _operatorStateService.CurrentOperatorName;
            AddLog($"作业员已切换为：{OperatorName}");
            _logger.LogWarning("[作业员][审计] 运行页检测前切换作业员: {Operator}", OperatorName);
            RefreshReadyOrCanStartState();
            await Task.CompletedTask;
        }
        finally
        {
            _suspendRunPageBarcodeHandling = false;
        }
    }

    /// <summary>运行页重新选择参照系列、机种和工位，仅允许在非运行状态执行。</summary>
    [RelayCommand(CanExecute = nameof(CanChangeReferenceSelection))]
    private async Task ChangeReferenceSelectionAsync()
    {
        if (!CanChangeReferenceSelection())
        {
            _logger.LogWarning("[参照选择][拒绝] 当前状态不允许重新选择：UiState={UiState}, Action={Action}",
                UiState, _currentControlAction);
            return;
        }

        var dialog = _serviceProvider.GetRequiredService<SeriesMachineSelectionDialog>();
        dialog.Owner = Application.Current.MainWindow;
        _suspendRunPageBarcodeHandling = true;
        try
        {
            if (dialog.ShowDialog() == true)
            {
                AddLog($"参照信息已更新：{ReferenceDisplayText}");
                _logger.LogWarning("[参照选择][审计] 运行页重新选择完成：{ReferenceDisplayText}", ReferenceDisplayText);
            }

            await Task.CompletedTask;
        }
        finally
        {
            _suspendRunPageBarcodeHandling = false;
        }
    }

    private bool CanChangeReferenceSelection()
    {
        return _currentControlAction == InspectionControlAction.None
               && (UiState is TestUIState.Ready
                   or TestUIState.CanStart
                   or TestUIState.CompletedPass
                   or TestUIState.CompletedFail
                   or TestUIState.SingleItemNgStopped);
    }

    private bool CanChangeOperator()
    {
        return _currentControlAction == InspectionControlAction.None
               && (UiState is TestUIState.Ready
                   or TestUIState.CanStart
                   or TestUIState.CompletedPass
                   or TestUIState.CompletedFail
                   or TestUIState.SingleItemNgStopped);
    }

    partial void OnModelNameChanged(string value)
    {
        if (!string.Equals(_pendingBarcodePlanAutoSelectMachine, value.Trim(), StringComparison.OrdinalIgnoreCase))
            _pendingBarcodePlanAutoSelectMachine = null;

        InvalidateCurrentSerialVerification();
        UpdateReferenceMachineMismatch();
        if (!IsReferenceMachineMismatch)
            _lastReferenceMismatchPromptKey = null;

        int loadVersion = Interlocked.Increment(ref _planLoadVersion);
        _isPlanLoading = true;
        TestItems.Clear();
        RefreshReadyOrCanStartState();
        _ = HandleModelNameChangedAsync(value, loadVersion);
        ScheduleDuplicateRecordCheck();
        if (!_isApplyingScannerBarcode)
            ScheduleProductIdentityNotification();
    }

    /// <summary>参照机种与实际输入机种只做可见提示，不参与启动校验。</summary>
    private void UpdateReferenceMachineMismatch()
    {
        IsReferenceMachineMismatch = !string.IsNullOrWhiteSpace(ReferenceMachineType)
            && !string.IsNullOrWhiteSpace(ModelName)
            && !string.Equals(
                ReferenceMachineType.Trim(),
                ModelName.Trim(),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从参照状态服务同步运行页展示字段。</summary>
    private void ApplyReferenceSelection()
    {
        var selection = _referenceSelectionStateService.CurrentSelection;
        ReferenceSeries = selection.SeriesName?.Trim() ?? string.Empty;
        ReferenceMachineType = selection.ReferenceMachineType?.Trim() ?? string.Empty;
        ReferenceWorkstation = selection.Workstation?.Trim() ?? string.Empty;
        ReferenceDisplayText = string.IsNullOrWhiteSpace(ReferenceSeries)
            || string.IsNullOrWhiteSpace(ReferenceMachineType)
            || string.IsNullOrWhiteSpace(ReferenceWorkstation)
            ? "未选择参照信息"
            : $"{ReferenceSeries} / {ReferenceMachineType} / {ReferenceWorkstation}";
        _lastReferenceMismatchPromptKey = null;
        UpdateReferenceMachineMismatch();
        ChangeReferenceSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 在手动机种输入完成时检查参照机种不一致，只在 Enter 或失焦时调用。
    /// </summary>
    public void NotifyManualModelNameCommitted()
    {
        ShowReferenceMachineMismatchIfNeeded(isScannerInput: false);
    }

    /// <summary>
    /// 复用现有参照机种比较结果显示提醒，不回滚用户已经输入的机种。
    /// </summary>
    private void ShowReferenceMachineMismatchIfNeeded(bool isScannerInput)
    {
        UpdateReferenceMachineMismatch();
        if (!IsReferenceMachineMismatch)
        {
            _lastReferenceMismatchPromptKey = null;
            return;
        }

        string source = isScannerInput ? "扫码" : "输入";
        string key = $"{source}|{ReferenceMachineType.Trim()}|{ModelName.Trim()}";
        if (string.Equals(_lastReferenceMismatchPromptKey, key, StringComparison.OrdinalIgnoreCase))
            return;

        _lastReferenceMismatchPromptKey = key;
        string message = isScannerInput
            ? $"扫码机种与参照机种不一致。\n\n参照机种：{ReferenceMachineType}\n扫码机种：{ModelName}\n\n请确认当前基板或条码是否正确。"
            : $"输入机种与参照机种不一致。\n\n参照机种：{ReferenceMachineType}\n输入机种：{ModelName}\n\n请确认输入是否正确。";

        _logger.LogWarning(
            "[机种校验][不一致] 来源={Source}, 参照机种={ReferenceMachineType}, 实际机种={ModelName}",
            source,
            ReferenceMachineType,
            ModelName);
        _ = ShowWarningSafelyAsync(message, "机种不一致");
    }

    /// <summary>统一观察非阻塞提示任务，避免弹窗异常成为未观察任务。</summary>
    private async Task ShowWarningSafelyAsync(string message, string title)
    {
        try
        {
            await _notificationService.ShowWarningAsync(message, title).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[普通弹窗][失败] 标题={Title}", title);
        }
    }

    private void OnReferenceSelectionChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        if (dispatcher.CheckAccess())
            ApplyReferenceSelection();
        else
            _ = dispatcher.BeginInvoke(new Action(ApplyReferenceSelection), DispatcherPriority.Normal);
    }

    partial void OnSerialNumberChanged(string value)
    {
        InvalidateCurrentSerialVerification();
        ScheduleDuplicateRecordCheck();
        RefreshReadyOrCanStartState();
        if (!_isApplyingScannerBarcode)
            ScheduleProductIdentityNotification();
    }

    /// <summary>
    /// 为手动输入安排稳定性确认，避免每输入一个字符都向 PLC 写入 DT312。
    /// </summary>
    private void ScheduleProductIdentityNotification()
    {
        CancelProductIdentityNotification();
        _lastNotifiedProductIdentityKey = null;

        if (!IsInputEnabled
            || string.IsNullOrWhiteSpace(ModelName)
            || string.IsNullOrWhiteSpace(SerialNumber)
            || !InputValidationHelper.IsValidSerialNumber(SerialNumber))
        {
            return;
        }

        string modelName = ModelName.Trim();
        string serialNumber = SerialNumber.Trim();
        var cts = new CancellationTokenSource();
        _productIdentityNotifyCts = cts;
        _ = NotifyProductIdentityAfterDelayAsync(modelName, serialNumber, cts);
    }

    /// <summary>等待手动输入稳定后，确认快照未变化再通知 PLC。</summary>
    private async Task NotifyProductIdentityAfterDelayAsync(
        string modelName,
        string serialNumber,
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(400, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested
                || !ReferenceEquals(_productIdentityNotifyCts, cts)
                || !IsInputEnabled
                || !string.Equals(ModelName.Trim(), modelName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(SerialNumber.Trim(), serialNumber, StringComparison.OrdinalIgnoreCase)
                || !InputValidationHelper.IsValidSerialNumber(serialNumber))
            {
                return;
            }

            await NotifyProductIdentityAsync(
                "Manual",
                modelName,
                serialNumber,
                allowRepeat: false,
                cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 新输入或页面离开已取消旧的延时通知，不记录为 PLC 故障。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[输入完成通知][异常] Source=Manual, Model={Model}, Serial={Serial}",
                modelName,
                serialNumber);
        }
        finally
        {
            if (ReferenceEquals(_productIdentityNotifyCts, cts))
            {
                _productIdentityNotifyCts = null;
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// 写入输入完成通知。扫码允许同内容重复通知，手动输入按组合键防重复。
    /// </summary>
    private async Task NotifyProductIdentityAsync(
        string source,
        string modelName,
        string serialNumber,
        bool allowRepeat,
        CancellationToken ct)
    {
        if (!IsInputEnabled
            || !_hardwareEventsSubscribed
            || string.IsNullOrWhiteSpace(modelName)
            || string.IsNullOrWhiteSpace(serialNumber)
            || !InputValidationHelper.IsValidSerialNumber(serialNumber))
        {
            return;
        }

        string key = $"{modelName.Trim()}|{serialNumber.Trim()}";
        if (!allowRepeat && string.Equals(
                _lastNotifiedProductIdentityKey,
                key,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PlcOperationResult result;
        try
        {
            result = await _plcDevice.NotifyProductIdentityEnteredAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[输入完成通知][失败] Source={Source}, Model={Model}, Serial={Serial}",
                source,
                modelName,
                serialNumber);
            return;
        }

        if (result.IsSuccess)
        {
            _lastNotifiedProductIdentityKey = key;
            _logger.LogInformation(
                "[输入完成通知] Source={Source}, DT312=1, Model={Model}, Serial={Serial}",
                source,
                modelName,
                serialNumber);
            return;
        }

        _logger.LogWarning(
            "[输入完成通知][失败] Source={Source}, Model={Model}, Serial={Serial}, Message={Message}",
            source,
            modelName,
            serialNumber,
            result.Message);
    }

    /// <summary>取消尚未执行的机种和序列号输入完成通知。</summary>
    private void CancelProductIdentityNotification()
    {
        var cts = _productIdentityNotifyCts;
        _productIdentityNotifyCts = null;
        if (cts == null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private async Task HandleModelNameChangedAsync(string newMachineType, int loadVersion)
    {
        bool isCurrent = await RefreshPlanNameOptionsAsync(newMachineType, loadVersion);
        if (!isCurrent)
            return;

        if (string.Equals(_pendingBarcodePlanAutoSelectMachine, newMachineType.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            _pendingBarcodePlanAutoSelectMachine = null;
            if (PlanNameOptions.Count == 1)
            {
                // 复用现有 OnSchemeNameChanged，继续由统一逻辑加载检测项目和更新引擎配置。
                // 即使新旧机种的唯一方案同名，也先清空一次以确保重新加载检测项目。
                SchemeName = string.Empty;
                SchemeName = PlanNameOptions[0];
                _logger.LogInformation("[扫码联动][运行页] 已自动选择唯一方案：机种={MachineType}, 方案={PlanName}",
                    newMachineType, SchemeName);
                return;
            }

            if (!string.IsNullOrWhiteSpace(SchemeName))
            {
                // 扫码切换到零方案或多方案机种时，清除上一机种遗留方案，避免误用。
                SchemeName = string.Empty;
            }

            if (PlanNameOptions.Count > 1)
            {
                AddLog($"⚠️ 机种 [{newMachineType}] 存在多个检测方案，请手动选择方案");
                _logger.LogWarning("[扫码联动][运行页] 机种存在多个方案，未自动选择：机种={MachineType}, 数量={Count}",
                    newMachineType, PlanNameOptions.Count);
            }
            else
            {
                AddLog($"⚠️ 机种 [{newMachineType}] 没有可用检测方案");
                _logger.LogWarning("[扫码联动][运行页] 机种没有方案：机种={MachineType}", newMachineType);
            }
        }

        ValidateCurrentSchemeName();

        if (IsSchemeNameInvalid)
        {
            TestItems.Clear();
            AddLog($"⚠️ 机种已切换为 [{newMachineType}]，方案 [{SchemeName}] 不属于该机种，检测列表已清空，请重新选择方案");
        }

        RefreshReadyOrCanStartState();
    }

    private void ScheduleDuplicateRecordCheck()
    {
        // 必须先取消旧查询，再判断当前输入是否完整，避免清空序列号后旧任务晚到弹窗。
        CancelDuplicateRecordCheck();

        if (_suppressDuplicateCheck)
            return;

        if (string.IsNullOrWhiteSpace(ModelName) || string.IsNullOrWhiteSpace(SerialNumber))
            return;

        _duplicateCheckCts = new CancellationTokenSource();
        var token = _duplicateCheckCts.Token;
        var machineType = ModelName.Trim();
        var serialNumber = SerialNumber.Trim();
        _currentSerialVerificationState = CurrentSerialVerificationState.Checking;
        _duplicateCheckFailureMessage = null;

        _ = CheckDuplicateRecordAfterDelayAsync(machineType, serialNumber, token);
    }

    private async Task CheckDuplicateRecordAfterDelayAsync(string machineType, string serialNumber, CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token).ConfigureAwait(false);

            var inputStillCurrent = await Application.Current.Dispatcher.InvokeAsync(() =>
                !token.IsCancellationRequested
                && !_suppressDuplicateCheck
                && string.Equals(ModelName.Trim(), machineType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(SerialNumber.Trim(), serialNumber, StringComparison.OrdinalIgnoreCase));

            if (!inputStillCurrent)
                return;

            var key = $"{machineType}|{serialNumber}";
            if (string.Equals(_lastDuplicateCheckKey, key, StringComparison.OrdinalIgnoreCase))
                return;

            var startTime = DateTime.Today.AddMonths(-1);
            var endTime = DateTime.Now;
            var exists = await _testRecordStorage.ExistsRecentTestRecordAsync(
                machineType, serialNumber, startTime, endTime).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return;

            var currentResult = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested
                    || _suppressDuplicateCheck
                    || !string.Equals(ModelName.Trim(), machineType, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(SerialNumber.Trim(), serialNumber, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!exists)
                {
                    _currentSerialVerificationState = CurrentSerialVerificationState.Confirmed;
                    _confirmedSerialKey = key;
                    _duplicateCheckFailureMessage = null;
                    _logger.LogInformation("[重复测试][确认] 最近一个月无重复记录：机种={MachineType}, SN={SerialNumber}",
                        machineType, serialNumber);
                    RefreshReadyOrCanStartState();
                }
                else
                {
                    // 在排队显示弹窗前先进入等待决策态，避免这段极短窗口误放行启动。
                    _currentSerialVerificationState = CurrentSerialVerificationState.AwaitingDuplicateDecision;
                    _confirmedSerialKey = null;
                }

                return true;
            });

            if (!currentResult || !exists || token.IsCancellationRequested)
                return;

            var showPromptOperation = Application.Current.Dispatcher.InvokeAsync(
                () => ShowDuplicateRecordPromptAsync(machineType, serialNumber, key, token));
            await showPromptOperation.Task.Unwrap().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 输入继续变化或页面离开时取消，属于正常路径。
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested)
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (string.Equals(BuildCurrentSerialKey(), $"{machineType}|{serialNumber}", StringComparison.OrdinalIgnoreCase))
                {
                    _currentSerialVerificationState = CurrentSerialVerificationState.Failed;
                    _confirmedSerialKey = null;
                    _duplicateCheckFailureMessage = ex.Message;
                    RefreshReadyOrCanStartState();
                }
            });
            _logger.LogWarning(ex, "[重复测试] 检查最近记录失败：机种={MachineType}, SN={SerialNumber}",
                machineType, serialNumber);
        }
    }

    private async Task ShowDuplicateRecordPromptAsync(
        string machineType,
        string serialNumber,
        string key,
        CancellationToken token)
    {
        if (token.IsCancellationRequested
            || _suppressDuplicateCheck
            || !string.Equals(ModelName.Trim(), machineType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(SerialNumber.Trim(), serialNumber, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentSerialVerificationState = CurrentSerialVerificationState.AwaitingDuplicateDecision;
        _confirmedSerialKey = null;

        AddLog($"检测到最近一个月重复测试记录：机种={machineType}, 序列号={serialNumber}");
        _logger.LogWarning("[重复测试][审计] 最近一个月已有记录：机种={MachineType}, SN={SerialNumber}",
            machineType, serialNumber);

        var confirmed = await _notificationService.ConfirmAsync(
            $"该基板在最近一个月内已有测试记录。\n\n机种名称：{machineType}\n序列号：{serialNumber}\n\n是否继续测试？",
            "重复测试提醒").ConfigureAwait(true);

        if (confirmed)
        {
            _lastDuplicateCheckKey = key;
            _confirmedSerialKey = key;
            _duplicateCheckFailureMessage = null;
            _currentSerialVerificationState = CurrentSerialVerificationState.Confirmed;
            RefreshReadyOrCanStartState();
            AddLog("操作员确认继续重复测试");
            return;
        }

        _suppressDuplicateCheck = true;
        try
        {
            InvalidateCurrentSerialVerification();
            SerialNumber = string.Empty;
        }
        finally
        {
            _suppressDuplicateCheck = false;
        }

        AddLog("操作员取消重复测试，已清空序列号并保留机种和方案");
    }

    /// <summary>取消当前重复记录查询并释放旧取消源。</summary>
    private void CancelDuplicateRecordCheck()
    {
        var cts = _duplicateCheckCts;
        _duplicateCheckCts = null;
        if (cts == null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>使当前机种和序列号的确认结果失效，防止旧查询结果污染新输入。</summary>
    private void InvalidateCurrentSerialVerification()
    {
        _currentSerialVerificationState = CurrentSerialVerificationState.NotConfirmed;
        _confirmedSerialKey = null;
        _duplicateCheckFailureMessage = null;
        _lastDuplicateCheckKey = null;
        CancelDuplicateRecordCheck();
    }

    /// <summary>构造当前输入对应的确认键。</summary>
    private string BuildCurrentSerialKey()
        => $"{ModelName.Trim()}|{SerialNumber.Trim()}";

    /// <summary>确认状态、确认键和当前输入完全一致时，才允许启动。</summary>
    private bool IsCurrentSerialVerified()
    {
        return _currentSerialVerificationState == CurrentSerialVerificationState.Confirmed
            && !string.IsNullOrWhiteSpace(_confirmedSerialKey)
            && !string.IsNullOrWhiteSpace(SerialNumber)
            && string.Equals(_confirmedSerialKey, BuildCurrentSerialKey(), StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region 下部 - 测试项目列表

    public ObservableCollection<TestItemModel> TestItems { get; } = new();

    private async Task LoadPlanItemsAsync(
        int? expectedLoadVersion = null,
        string? expectedMachineType = null,
        string? expectedSchemeName = null)
    {
        TestItems.Clear();

        if (string.IsNullOrWhiteSpace(ModelName) || string.IsNullOrWhiteSpace(SchemeName))
        {
            AddLog("⚠️ 未指定机种或方案，检测列表为空");
            CompletePlanLoading(expectedLoadVersion);
            return;
        }

        List<PlanModel> allPlans;
        try
        {
            allPlans = await _planStorageService.LoadAllPlansAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载检测方案失败");
            if (IsCurrentPlanLoad(expectedLoadVersion, expectedMachineType, expectedSchemeName))
            {
                AddLog("⚠️ 检测方案加载失败，请检查方案设置后重试");
                CompletePlanLoading(expectedLoadVersion);
            }
            return;
        }

        if (!IsCurrentPlanLoad(expectedLoadVersion, expectedMachineType, expectedSchemeName))
            return;

        // 精确匹配机种和方案名（替换旧 allPlans.FirstOrDefault() 错误用法）
        var currentPlan = allPlans.FirstOrDefault(p =>
            string.Equals(p.MachineType, ModelName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.PlanName, SchemeName, StringComparison.OrdinalIgnoreCase));

        if (currentPlan == null)
        {
            AddLog($"⚠️ 未找到匹配方案: 机种={ModelName}, 方案={SchemeName}，检测列表为空");
            CompletePlanLoading(expectedLoadVersion);
            return;
        }

        if (currentPlan.Items.Count == 0)
        {
            AddLog($"⚠️ 方案 [{currentPlan.PlanName}] 无检测项目，检测列表为空");
            CompletePlanLoading(expectedLoadVersion);
            return;
        }

        _currentPlanVersion = Math.Max(1, currentPlan.Version);
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
                RecordResult = string.Empty,
                Judgment = string.Empty
            });
        }

        CompletePlanLoading(expectedLoadVersion);
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

    #region 检测完成态 —— 自动保存与 PLC 收口

    /// <summary>
    /// 全部 Pin 检测完成后触发。
     /// 使用当前 ModelName + SchemeName 精确匹配方案，不再 allPlans.FirstOrDefault()。
    /// </summary>
    private async Task OnAllPinsTestedAsync(bool isSingleItemNgStopped = false)
    {
        if (_inspectionCompletionInProgress)
        {
            _logger.LogWarning("[UI流程] 检测完成收口正在执行，跳过重复完成处理");
            return;
        }

        _inspectionCompletionInProgress = true;
        try
        {
            var finalResult = TestItems.All(i => i.Judgment == "OK") ? "OK" : "NG";
            AddLog(isSingleItemNgStopped
                ? $"本轮因单项 NG 提前结束，正在处理最终结果，综合判定：{finalResult}"
                : $"所有检查项目已执行完成，正在处理检测结果，综合判定：{finalResult}");

            TotalCount++;
            if (finalResult == "OK") PassCount++;
            else FailCount++;

            // ★ 使用当前 ModelName + SchemeName 精确匹配，不再取 allPlans.FirstOrDefault()
            var machineType = string.IsNullOrWhiteSpace(ModelName) ? "Unknown" : ModelName;
            var planName = string.IsNullOrWhiteSpace(SchemeName) ? "Unknown" : SchemeName;

            var settings = _settingsService.LoadSettings();
            var shouldSave = finalResult == "OK"
                || (settings.ContinueTestingAfterNg && settings.SaveNgInspectionResult);

            if (shouldSave)
            {
                AddLog(finalResult == "OK"
                    ? "正在保存检测记录..."
                    : "当前设置允许保存 NG 检测记录，正在保存...");

                var saved = await SaveInspectionRecordAsync(machineType, planName, finalResult).ConfigureAwait(false);
                if (!saved)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        SetUiState(TestUIState.Error);
                        AddLog("检测已完成，但检测记录保存失败；本轮结果已保留，请处理保存异常后复位。");
                    });
                    return;
                }
            }
            else
            {
                _logger.LogWarning(
                    "[检测完成][审计] 最终结果为 NG，设置为不保存 NG 检测记录：ContinueTestingAfterNg={ContinueTestingAfterNg}, SaveNgInspectionResult={SaveNgInspectionResult}",
                    settings.ContinueTestingAfterNg, settings.SaveNgInspectionResult);
                AddLog("当前设置为不保存 NG 检测记录，本轮跳过 CSV 保存");
            }

            AddLog("正在执行 PLC 检测完成收口...");
            bool cleanupSucceeded = await CompleteNormalInspectionHandshakeAsync().ConfigureAwait(false);
            if (!cleanupSucceeded)
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                FinalJudgment = finalResult;
                if (UiState != TestUIState.Error)
                {
                    SetUiState(finalResult == "OK" ? TestUIState.CompletedPass : TestUIState.CompletedFail);
                    AddLog($"本轮检测完成：{finalResult}");
                    ClearSerialForNextBoardAfterCompletion();
                }
                else
                {
                    AddLog($"本轮检测结果为 {finalResult}，但最终结果清理异常，保持异常状态并等待复位。");
                }
            });
        }
        finally
        {
            _inspectionCompletionInProgress = false;
        }

        // 本轮检测完成后保持当前测量模式，不强制恢复电阻模式
        // 下一件开始时会根据第一个检测项自动切换正确模式
        _logger.LogInformation("[万用表收尾] 本轮检测完成，保持当前测量模式，等待下一轮检测");
    }

    /// <summary>
    /// 保存检测记录到 CSV。
    /// ★ 使用当前 ModelName + SchemeName 精确匹配，不再 allPlans.FirstOrDefault()。
    /// </summary>
    private async Task<bool> SaveInspectionRecordAsync(string machineType, string planName, string finalResult)
    {
        try
        {
            // ★ 直接使用当前界面上的 ModelName 和 SchemeName
            var record = new LogRecord
            {
                Timestamp = DateTime.Now,
                Series = ReferenceSeries,
                MachineType = ModelName,
                SerialNumber = SerialNumber,
                PlanName = planName,
                PlanVersion = _currentPlanVersion,
                Operator = OperatorName,
                FinalResult = finalResult,
                PinResults = TestItems.Select(item => new PinResult
                {
                    PinName = item.ItemName,
                    // CSV 使用独立记录值，避免把界面单位“Ω”或“超量程”写入正式记录。
                    Result = string.IsNullOrEmpty(item.RecordResult)
                        ? item.CheckResult
                        : item.RecordResult
                }).ToList()
            };

            await _testRecordStorage.SaveRecordAsync(record);
            AddLog($"检测记录已保存 - SN:{SerialNumber}, 结果:{finalResult}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存检测记录失败");
            AddLog($"保存失败: {ex.Message}");
            await _notificationService.ShowErrorAsync($"保存失败：{ex.Message}", "错误");
            return false;
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
            item.RecordResult = string.Empty;
            item.Judgment = string.Empty;
        }
    }

    /// <summary>
    /// 正常完成态启动下一轮前清理上一轮结果。
    /// 该方法只在完整启动校验、万用表检查和 DT234 写入成功后调用，失败时保留上一轮界面结果。
    /// </summary>
    private async Task<bool> PrepareCompletedRunForNextStartAsync(CancellationToken ct)
    {
        if (UiState is not (TestUIState.CompletedPass or TestUIState.CompletedFail or TestUIState.SingleItemNgStopped))
            return true;

        await CancelFinalResultAutoClearAsync("下一轮启动准备").ConfigureAwait(false);

        var finalResultClear = await ClearFinalResultWithAuditAsync("下一轮启动准备", ct).ConfigureAwait(false);
        if (!finalResultClear.IsSuccess)
        {
            _logger.LogWarning("[下一轮启动][拒绝] DT304/DT305 清除失败，保留上一轮结果：{Message}", finalResultClear.Message);
            AddLog("下一轮启动失败：上一轮检测结果信号清除失败，请执行复位或检查 PLC。");
            await _notificationService.ShowWarningAsync(
                "上一轮检测结果信号清除失败，请执行复位或检查 PLC 后再重试。",
                "启动拒绝").ConfigureAwait(false);
            return false;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ClearTestItemsForRestart();
            FinalJudgment = null;
            _inspectionCompletionInProgress = false;
            _inspectionStarted = false;
            _ignoreInspectionCallbacksUntilNextStart = false;
            Interlocked.Increment(ref _inspectionRunVersion);
            ResetElapsedTime();
        });

        _logger.LogInformation("[下一轮启动][准备完成] 上一轮结果、项目状态和耗时已清理");
        return true;
    }

    /// <summary>
    /// 正常完成收口后只清除本轮序列号及其确认状态，保留机种、方案和上一轮结果显示。
    /// </summary>
    private void ClearSerialForNextBoardAfterCompletion()
    {
        if (UiState is not (TestUIState.CompletedPass or TestUIState.CompletedFail or TestUIState.SingleItemNgStopped))
            return;

        _suppressDuplicateCheck = true;
        try
        {
            InvalidateCurrentSerialVerification();
            SerialNumber = string.Empty;
        }
        finally
        {
            _suppressDuplicateCheck = false;
        }

        AddLog("本轮检测完成，已清空序列号；机种、方案和上一轮结果保持不变");
        _logger.LogInformation("[检测完成][下一块基板] 已清空序列号，保留机种={MachineType}, 方案={PlanName}, 最终结果={FinalJudgment}",
            ModelName, SchemeName, FinalJudgment);
    }

    /// <summary>
    /// 回到准备态，使用强制收口自动计算待机/可启动。
    /// </summary>
    private void ResetToReadyState()
    {
        InvalidateCurrentSerialVerification();
        SerialNumber = string.Empty;
        ClearTestItemsForRestart();
        FinalJudgment = null;
        ResetElapsedTime();
        IsPlcStartRequested = false;
        _inspectionStarted = false;
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
                _logger.LogInformation("[复位流程][DT122] 最终确认成功，第 {Attempt} 次后 DT122=0", attempt);
                return true;
            }

            _logger.LogWarning("[复位流程][DT122] 第 {Attempt} 次确认仍为 1", attempt);
        }

        _logger.LogWarning("[复位流程][DT122] 最终确认失败，DT122 仍未释放");
        return false;
    }

    /// <summary>
    /// 复位收口最终验证。DT121 的写入结果由 ClearResetRequestAsync 的协议响应确认，
    /// 本方法只验证其余控制信号和 PLC 快照读取是否满足进入 Ready/CanStart 的条件。
    /// </summary>
    private async Task<bool> ClearResetRequestWithSingleRetryAsync(string stage, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var clearResult = await _plcDevice.ClearResetRequestAsync(ct).ConfigureAwait(false);
                if (clearResult.IsSuccess)
                {
                    _logger.LogInformation(
                        "[复位流程][DT121] {Stage}清除成功，第 {Attempt} 次写入收到正常协议应答",
                        stage,
                        attempt);
                    return true;
                }

                _logger.LogWarning(
                    "[复位流程][DT121] {Stage}清除失败，第 {Attempt} 次：{Message}",
                    stage,
                    attempt,
                    clearResult.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[复位流程][DT121] {Stage}清除异常，第 {Attempt} 次",
                    stage,
                    attempt);
            }

            if (attempt < 2)
            {
                _logger.LogWarning("[复位流程][DT121] {Stage}清除将进行最后一次重试", stage);
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        _logger.LogWarning("[复位流程][DT121] {Stage}清除重试仍失败", stage);
        return false;
    }

    /// <summary>
    /// 复位收口最终验证。DT121 后续再次为 1 代表新的或重复请求，不属于本次复位失败。
    /// </summary>
    private async Task<ResetCompletionValidationResult> ValidateResetCompletionAsync(CancellationToken ct)
    {
        try
        {
            PlcOperationResult<PlcControlSignals>? snapshot;
            snapshot = await _plcDevice.ReadControlSignalsAsync(ct).ConfigureAwait(false);
            if (snapshot == null || !snapshot.IsSuccess || snapshot.Value == null)
            {
                _logger.LogWarning("[复位流程][验证] 读取 PLC 控制信号快照失败：{Message}", snapshot?.Message ?? "返回结果为 null");
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
            ResetCompletionValidationResult.StopSignalStillActive => "停止信号 DT122 无法清除",
            ResetCompletionValidationResult.EmergencyStopStillActive => "急停信号 DT123 仍有效",
            ResetCompletionValidationResult.PlcReadFailed => "PLC 输入快照读取失败",
            _ => "未知控制信号未释放"
        };
    }

    /// <summary>
    /// 正常完成后的 PLC 握手收口。
    /// 必须在保存完成或确认跳过保存之后执行，不能在启动复核通过或检测刚完成时提前清 DT120。
    /// </summary>
    private async Task<bool> CompleteNormalInspectionHandshakeAsync()
    {
        _logger.LogInformation("[PLC动作][审计] 检测完成且保存策略已处理，开始清理本轮启动握手信号");

        var cleanupResult = await ClearTransientRunOutputsAsync(CancellationToken.None).ConfigureAwait(false);

        if (!cleanupResult.AllSucceeded)
        {
            _logger.LogError("[PLC收口][审计] 正常完成收口失败：Start={Start}, PcReady={PcReady}, Relay={Relay}, Pins={Pins}",
                cleanupResult.StartCleared, cleanupResult.PcReadyCleared,
                cleanupResult.RelayCleared, cleanupResult.PinsCleared);

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
            IsPlcStartRequested = false;
        });

        _logger.LogInformation("[PLC动作][审计] 本轮正常完成收口结束：DT120/DT234/DT302/DT130~DT185 已清除，最终产品结果按 OK/NG 保持规则独立处理");
        AddLog("PLC 检测完成收口完成");
        return true;
    }

    #endregion

    #region 统一 PLC 输出清理

    /// <summary>
    /// 清普通运行输出：DT120、DT234、DT302、DT130~185。
    /// 停止、单项 NG、正常完成收口、紧急停止后调用。
    /// 返回逐项清理结果。
    /// </summary>
    private async Task<RunOutputCleanupResult> ClearTransientRunOutputsAsync(CancellationToken ct = default)
    {
        var start = await _plcDevice.ClearStartRequestAsync(ct).ConfigureAwait(false);
        var pcReady = await _plcDevice.ClearPcReadyAsync(ct).ConfigureAwait(false);
        var relay = await _plcDevice.ClearRelayActionCompletedAsync(ct).ConfigureAwait(false);
        var pins = await _plcDevice.ClearPinOutputsAsync(ct).ConfigureAwait(false);

        var result = new RunOutputCleanupResult(
            start.IsSuccess,
            pcReady.IsSuccess,
            relay.IsSuccess,
            pins.IsSuccess);

        if (result.AllSucceeded)
        {
            _logger.LogInformation("[PLC清理][成功] DT120={Start}, DT234={PcReady}, DT302={Relay}, DT130~185={Pins}",
                start.IsSuccess, pcReady.IsSuccess, relay.IsSuccess, pins.IsSuccess);
        }
        else
        {
            _logger.LogError("[PLC清理][失败] DT120={Start}, DT234={PcReady}, DT302={Relay}, DT130~185={Pins}",
                start.IsSuccess, pcReady.IsSuccess, relay.IsSuccess, pins.IsSuccess);
        }

        return result;
    }

    /// <summary>
    /// 单独清理 DT304/DT305，并保留调用场景和失败原因，避免普通运行输出清理误清最终结果。
    /// </summary>
    private async Task<PlcOperationResult> ClearFinalResultWithAuditAsync(
        string reason,
        CancellationToken ct = default)
    {
        var result = await _plcDevice.ClearFinalResultAsync(ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation("[PLC最终结果清理][成功] Reason={Reason}, Message={Message}", reason, result.Message);
        }
        else
        {
            _logger.LogError(
                "[PLC最终结果清理][失败] Reason={Reason}, Message={Message}",
                reason,
                result.Message);
        }

        return result;
    }

    /// <summary>
    /// 取消并等待上一轮 OK 自动清除任务，确保复位或页面退出后旧任务不再操作 PLC。
    /// </summary>
    private async Task CancelFinalResultAutoClearAsync(string reason)
    {
        await _finalResultAutoClearLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CancelFinalResultAutoClearCoreAsync(reason).ConfigureAwait(false);
        }
        finally
        {
            _finalResultAutoClearLock.Release();
        }
    }

    private async Task CancelFinalResultAutoClearCoreAsync(string reason)
    {
        var cts = _finalResultAutoClearCts;
        var task = _finalResultAutoClearTask;
        _finalResultAutoClearCts = null;
        _finalResultAutoClearTask = null;

        if (cts is null && task is null)
            return;

        cts?.Cancel();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消是 Reset、Stop、Finish 和页面生命周期的预期结果。
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PLC最终结果清理][任务取消异常] Reason={Reason}", reason);
            }
        }

        cts?.Dispose();
        _logger.LogInformation("[PLC最终结果清理][延时任务已取消] Reason={Reason}", reason);
    }

    /// <summary>
    /// 在最终结果成功写入后启动独立的 OK 延时清除任务，不阻塞 CSV 保存和完成界面更新。
    /// </summary>
    private async Task ScheduleOkFinalResultAutoClearAsync(int runVersion)
    {
        await _finalResultAutoClearLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CancelFinalResultAutoClearCoreAsync("替换上一轮 OK 延时任务").ConfigureAwait(false);

            var cts = new CancellationTokenSource();
            _finalResultAutoClearCts = cts;
            _finalResultAutoClearTask = RunOkFinalResultAutoClearAsync(runVersion, cts.Token);
            _logger.LogInformation(
                "[PLC最终结果][OK保持] 已启动延时清除任务，RunVersion={RunVersion}",
                runVersion);
        }
        finally
        {
            _finalResultAutoClearLock.Release();
        }
    }

    private async Task RunOkFinalResultAutoClearAsync(int runVersion, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (runVersion != Volatile.Read(ref _inspectionRunVersion))
            {
                _logger.LogInformation(
                    "[PLC最终结果][OK保持] 任务版本已过期，跳过清除，RunVersion={RunVersion}, CurrentRunVersion={CurrentRunVersion}",
                    runVersion,
                    _inspectionRunVersion);
                return;
            }

            var clearResult = await ClearFinalResultWithAuditAsync("OK延时清除-第一次", token).ConfigureAwait(false);
            if (!clearResult.IsSuccess)
            {
                _logger.LogWarning(
                    "[PLC最终结果][OK清除重试] 第一次清除失败，200ms 后进行最后一次尝试，Message={Message}",
                    clearResult.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (runVersion != Volatile.Read(ref _inspectionRunVersion))
                    return;

                clearResult = await ClearFinalResultWithAuditAsync("OK延时清除-第二次", token).ConfigureAwait(false);
            }

            if (!clearResult.IsSuccess)
            {
                _logger.LogError(
                    "[FinalResultAutoClearFailed][PLC最终结果] OK 信号清除失败，Message={Message}",
                    clearResult.Message);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (runVersion != _inspectionRunVersion)
                        return;

                    SetUiState(TestUIState.Error);
                    AddLog("PLC OK 结果信号清除失败，请执行复位后再继续操作。");
                });
            }
            else
            {
                _logger.LogInformation("[PLC最终结果][OK保持] 已完成约 1 秒延时清除");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug("[PLC最终结果][OK保持] 延时清除任务已取消，RunVersion={RunVersion}", runVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FinalResultAutoClearFailed][PLC最终结果] OK 延时清除任务异常，RunVersion={RunVersion}", runVersion);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (runVersion != _inspectionRunVersion)
                    return;

                SetUiState(TestUIState.Error);
                AddLog("PLC OK 结果延时清除异常，请执行复位后再继续操作。");
            });
        }
    }

    /// <summary>
    /// 清所有运行信号：DT120/DT121/DT122/DT123/DT234/DT302/DT303/
    /// DT304/DT305/DT306/DT307/DT130~DT185。
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
        await _plcDevice.ClearInspectionEndedAsync(ct).ConfigureAwait(false);

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
            TestUIState.CompletedPass or TestUIState.CompletedFail => "当前检测结果正在等待复位，返回将丢失本轮界面结果，确定继续吗？",
            TestUIState.EmergencyStop => "急停中返回主菜单将丢失当前数据，确定继续吗？",
            _ => "确定要返回主菜单吗？"
        };

        var confirmed = await _notificationService.ConfirmAsync(confirmMsg, "确认返回");
        if (!confirmed) return;

        if (_currentControlAction == InspectionControlAction.Starting && _startingCts != null)
        {
            _logger.LogWarning("[终止请求][抢占] Starting 中，取消启动后等待终止动作进入");
            await _startingCts.CancelAsync().ConfigureAwait(false);
        }

        if (!await TryEnterControlActionAsync(InspectionControlAction.Finishing, waitForLock: true))
        {
            AddLog("终止请求已拒绝：当前已有运行控制动作正在执行。");
            return;
        }

        _isFinishing = true;

        try
        {
            await CancelFinalResultAutoClearAsync("Finish开始").ConfigureAwait(false);
            _logger.LogWarning("[终止按钮][审计] 终止按钮触发，写 DT306=1");
            await _plcDevice.RequestTerminateAsync(CancellationToken.None);
            AddLog("终止信号(DT306=1)已写入");

            if (UiState == TestUIState.Testing && _inspectionEngine != null)
            {
                var stopResult = await _inspectionEngine.StopAndWaitAsync(TimeSpan.FromSeconds(2), InspectionStopReason.Terminate, CancellationToken.None);
                if (stopResult == InspectionStopWaitResult.Timeout)
                {
                    SetUiState(TestUIState.Error);
                    AddLog("终止失败：检测任务未能在超时时间内安全停止，请检查设备状态后重试。");
                    return;
                }

                AddLog("操作员终止了当前测试");
            }

            StopPlcPolling();
            await ClearAllRunSignalsAsync(CancellationToken.None);
            await ReleaseDmmToLocalBestEffortAsync("终止按钮");

            ResetToReadyState();
            await _navigationService.NavigateToAsync<MainMenuView>();

            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await _plcDevice.ClearTerminateRequestAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("[终止按钮][审计] 延时 1 秒后已清除 DT306=0");
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
            LogMessages.Add($"[{timestamp}] {ToOperatorText(message, ShowTechnicalDetails)}");

            while (LogMessages.Count > 500)
                LogMessages.RemoveAt(0);
        });
    }

    /// <summary>真实模式隐藏 PLC 内部地址；调试模式保留原始文字，便于联调定位。</summary>
    private static string ToOperatorText(string message, bool showTechnicalDetails)
    {
        if (showTechnicalDetails || string.IsNullOrWhiteSpace(message))
            return message;

        return Regex.Replace(message, @"DT\d+", match => match.Value switch
        {
            "DT120" => "启动请求信号",
            "DT121" => "复位请求信号",
            "DT122" => "停止请求信号",
            "DT123" => "急停信号",
            "DT234" => "启动允许信号",
            "DT302" => "继电器动作完成信号",
            "DT303" => "报警解除信号",
            "DT304" or "DT305" => "检测结果信号",
            "DT306" => "终止信号",
            "DT307" => "检测流程结束信号",
            _ when int.TryParse(match.Value.AsSpan(2), out int address)
                   && address is >= 130 and <= 185 => "引脚控制输出",
            _ => "设备内部信号"
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
    /// 使用计数保证嵌套控制动作不会由内层动作提前恢复轮询。
    /// </summary>
    private void SuspendPlcPolling()
    {
        int suspendCount = Interlocked.Increment(ref _plcPollingSuspendCount);
        _plcPollingOperationCts?.Cancel();
        _plcPollingOperationCts?.Dispose();
        _plcPollingOperationCts = null;
        _logger.LogDebug("[PLC轮询] 已暂停，SuspendCount={SuspendCount}", suspendCount);
    }

    /// <summary>
    /// 恢复低优先级 UI 轮询。计数回到 0 后才真正恢复。
    /// </summary>
    private void ResumePlcPolling()
    {
        int suspendCount = Interlocked.Decrement(ref _plcPollingSuspendCount);
        if (suspendCount < 0)
        {
            _logger.LogError("[PLC轮询][防御] 暂停计数异常为负数：{SuspendCount}，已重置为 0", suspendCount);
            Interlocked.Exchange(ref _plcPollingSuspendCount, 0);
            suspendCount = 0;
        }

        _logger.LogDebug("[PLC轮询] 收到恢复请求，SuspendCount={SuspendCount}", suspendCount);
    }

    /// <summary>
    /// PLC 轮询主体：读取控制信号 → 更新 UI 状态 → 检测 DT120 启动请求。
    /// </summary>
    private async Task PollPlcInputsAsync()
    {
        // 控制动作执行期间暂停低优先级轮询
        if (Volatile.Read(ref _plcPollingSuspendCount) > 0)
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
            pollResult = await _plcDevice.ReadControlSignalsAsync(pollingCt).ConfigureAwait(false);
            if (pollResult == null
                || !pollResult.IsSuccess
                || pollResult.Value == null)
            {
                if (pollResult?.IsCancelled == true)
                {
                    _logger.LogInformation("[PLC轮询] 本轮读取因控制动作切换已取消");
                }
                else
                {
                    _logger.LogWarning("[PLC轮询][诊断] 读取控制信号失败：{Message}", pollResult?.Message ?? "返回结果或 Value 为 null");
                }
                return;
            }

            var previousInputs = _lastPlcInputs;
            var inputs = pollResult.Value;

            // DT309/DT310 独立读取，失败时不能阻断前面的急停、复位和停止处理。
            var installRejectResult = await _plcDevice
                .ReadWorkstationInstallRejectSignalsAsync(pollingCt)
                .ConfigureAwait(false);
            bool installRejectReadFailed = installRejectResult == null
                || !installRejectResult.IsSuccess
                || installRejectResult.Value == null;
            var installRejectSignals = installRejectResult?.Value;
            if (installRejectReadFailed)
            {
                if (installRejectResult?.IsCancelled == true)
                {
                    _logger.LogDebug("[PLC轮询] DT309/DT310 读取因控制动作切换已取消");
                }
                else if (DateTime.UtcNow - _lastInstallRejectReadFailureLogUtc >= TimeSpan.FromSeconds(1))
                {
                    _lastInstallRejectReadFailureLogUtc = DateTime.UtcNow;
                    _logger.LogWarning("[PLC轮询][诊断] 读取 DT309/DT310 失败：{Message}",
                        installRejectResult?.Message ?? "返回结果或 Value 为 null");
                }
            }
            else
            {
                _lastInstallRejectSignals = installRejectSignals;
            }

            // 先保存本轮最新快照，再执行 UI 状态和启动条件判断，避免后续动作继续使用上一轮 DT121 状态。
            _lastPlcInputs = inputs;

            LogSemiPhysicalSignalChanges(previousInputs, inputs);
            IsPlcStartRequested = inputs.IsStartRequested;

            var operation = Application.Current.Dispatcher.InvokeAsync(
                () => UpdateUiStateFromPlcInputsAsync(
                    previousInputs,
                    inputs,
                    installRejectSignals,
                    installRejectReadFailed));
            await operation.Task.Unwrap().ConfigureAwait(false);
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
    private async Task UpdateUiStateFromPlcInputsAsync(
        PlcControlSignals? previousInputs,
        PlcControlSignals inputs,
        WorkstationInstallRejectSignals? installRejectSignals,
        bool installRejectReadFailed)
    {
        // ResetFailed 下先处理旧 DT121 的重新武装；DT121=0 只开放下一次入口，
        // 不代表上一次完整复位成功，也不能自动恢复 Ready/CanStart。
        if (UiState == TestUIState.ResetFailed
            && _resetRearmPending
            && !_isResetting
            && _currentControlAction == InspectionControlAction.None)
        {
            if (!inputs.IsResetRequested)
            {
                _resetRearmPending = false;
                _resetSignalHandled = false;
                _isResetting = false;
                _lastResetRearmAttemptUtc = DateTime.MinValue;
                _logger.LogWarning("[复位重新武装] 已确认 DT121=0，复位入口重新开放");
                AddLog("复位入口已重新开放，请再次执行复位");
            }
            else
            {
                await TryRearmResetAfterFailureAsync();
            }
        }
        // 普通状态下 DT121=0 才重置旧请求门禁。ResetFailed 的重新武装由上方分支负责。
        else if (!inputs.IsResetRequested
            && !_isResetting
            && _currentControlAction != InspectionControlAction.Resetting)
        {
            // DT121=0 只负责释放旧请求门禁，稳定轮询不重复输出运行页日志。
            _resetSignalHandled = false;
            _isResetting = false;
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

            await ShowEmergencyStopDialogAsync();
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
        if (inputs.IsResetRequested && !_resetSignalHandled)
        {
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

        if (inputs.IsResetRequested && _resetSignalHandled)
        {
            // 当前复位期间的重复请求只吸收，不重复停止引擎、清理输出或弹窗。
            _logger.LogDebug("[复位请求][吸收] 当前复位尚未收口，忽略重复 DT121 请求");
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

        // 安装拒绝是启动前输入，优先于 DT120 启动处理；不改变检测状态，也不要求复位。
        if (installRejectReadFailed && inputs.IsStartRequested)
        {
            await RejectStartWhenInstallRejectReadFailedAsync();
            return;
        }

        if (installRejectSignals != null)
        {
            if (installRejectSignals.LeftCode == 0 && installRejectSignals.RightCode == 0)
            {
                _handledInstallRejectKey = null;
            }
            else
            {
                await HandleWorkstationInstallRejectAsync(installRejectSignals, inputs.IsStartRequested);
                // 安装异常处理完成后立即结束本轮快照处理，避免弹窗关闭后继续使用旧 DT120=1 启动检测。
                return;
            }
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
    /// <summary>
    /// DT309/DT310 读取失败且同时存在启动请求时，禁止在无法确认安装状态的情况下启动。
    /// 该路径只清除启动请求，不把页面置为 Error 或 AwaitingReset。
    /// </summary>
    private async Task RejectStartWhenInstallRejectReadFailedAsync()
    {
        _logger.LogWarning("[启动复核][拒绝] DT309/DT310 读取失败，禁止启动并清除 DT120");
        AddLog("启动拒绝：基板安装状态读取失败，请检查 PLC 通信后重试。");
        await _notificationService.ShowWarningAsync(
            "基板安装状态读取失败，请检查 PLC 通信后重试。",
            "启动拒绝");

        var clearStartResult = await _plcDevice.ClearStartRequestAsync(CancellationToken.None);
        if (!clearStartResult.IsSuccess)
        {
            _logger.LogWarning("[启动复核][拒绝] DT309/DT310 读取失败后的 DT120 清除失败：{Message}",
                clearStartResult.Message);
        }
    }

    /// <summary>
    /// 显示工位安装拒绝提示，并在弹窗关闭后完成 DT120、DT311 和 DT309/DT310 握手。
    /// </summary>
    private async Task HandleWorkstationInstallRejectAsync(
        WorkstationInstallRejectSignals signals,
        bool startRequested)
    {
        string workstation;
        ushort currentCode;
        string key;
        string message;

        if (string.Equals(ReferenceWorkstation, WorkstationConstants.Left, StringComparison.OrdinalIgnoreCase))
        {
            workstation = WorkstationConstants.Left;
            currentCode = signals.LeftCode;
            key = $"{workstation}|{currentCode}";
            message = BuildInstallRejectMessage(workstation, currentCode);
        }
        else if (string.Equals(ReferenceWorkstation, WorkstationConstants.Right, StringComparison.OrdinalIgnoreCase))
        {
            workstation = WorkstationConstants.Right;
            currentCode = signals.RightCode;
            key = $"{workstation}|{currentCode}";
            message = BuildInstallRejectMessage(workstation, currentCode);
        }
        else
        {
            currentCode = signals.LeftCode != 0 ? signals.LeftCode : signals.RightCode;
            if (currentCode == 0)
                return;

            workstation = "当前工位";
            key = $"Unknown|{signals.LeftCode}|{signals.RightCode}";
            message = "基板安装状态异常，但当前工位信息无效，请重新选择系列、机种和工位后再启动。";
        }

        if (currentCode == 0 || string.Equals(_handledInstallRejectKey, key, StringComparison.Ordinal))
            return;
        if (Interlocked.CompareExchange(ref _installRejectDialogInProgress, 1, 0) != 0)
            return;

        _handledInstallRejectKey = key;
        bool isDuringInspection = _inspectionEngine?.IsRunning == true
            || UiState == TestUIState.Testing;
        string logCategory = isDuringInspection ? "检测异常" : "启动复核";
        string dialogTitle = isDuringInspection ? "基板安装异常" : "启动拒绝";
        if (isDuringInspection)
        {
            message += "\n本轮检测已中止，请重新安装并执行复位。";
        }

        _logger.LogWarning(
            "[{LogCategory}][安装拒绝] Workstation={Workstation}, Address={Address}, Code={Code}, DT120={StartRequested}, UiState={UiState}, InspectionEngineIsRunning={InspectionEngineIsRunning}, ExecutionStage={ExecutionStage}",
            logCategory,
            workstation,
            string.Equals(workstation, WorkstationConstants.Left, StringComparison.OrdinalIgnoreCase) ? 309 : 310,
            currentCode,
            startRequested ? 1 : 0,
            UiState,
            _inspectionEngine?.IsRunning == true,
            _inspectionEngine?.CurrentExecutionStage ?? "Unavailable");
        try
        {
            if (isDuringInspection)
            {
                await AbortInspectionForInstallRejectAsync(
                    workstation,
                    currentCode,
                    startRequested,
                    _inspectionEngine?.CurrentExecutionStage ?? "Unavailable");
            }

            AddLog($"{logCategory}：{message.Replace("\n", " ", StringComparison.Ordinal)}");
            await _notificationService.ShowWarningAsync(message, dialogTitle);

            if (startRequested)
            {
                var clearStartResult = await _plcDevice.ClearStartRequestAsync(CancellationToken.None);
                if (!clearStartResult.IsSuccess)
                {
                    _logger.LogWarning("[启动复核][安装拒绝] 清除 DT120 失败：{Message}", clearStartResult.Message);
                }
            }

            // DT309/DT310 仍保留原始拒绝代码时先发送 DT311，PLC 才能将确认归属到本次拒绝。
            var acknowledgementResult = await _plcDevice
                .PulseInstallRejectAcknowledgementAsync(CancellationToken.None);
            if (!acknowledgementResult.IsSuccess)
            {
                _logger.LogWarning("[{LogCategory}][安装拒绝] DT311 确认握手失败，但继续清除 DT309/DT310：{Message}",
                    logCategory,
                    acknowledgementResult.Message);
                AddLog("安装拒绝确认握手失败，请检查 PLC 通信日志。");
            }

            var clearResult = await _plcDevice.ClearWorkstationInstallRejectSignalsAsync(CancellationToken.None);
            if (!clearResult.IsSuccess)
            {
                _logger.LogWarning("[{LogCategory}][安装拒绝] 第一次清除 DT309/DT310 失败：{Message}",
                    logCategory,
                    clearResult.Message);
                await Task.Delay(200);
                clearResult = await _plcDevice.ClearWorkstationInstallRejectSignalsAsync(CancellationToken.None);
            }

            if (clearResult.IsSuccess)
            {
                _handledInstallRejectKey = null;
                _lastInstallRejectSignals = new WorkstationInstallRejectSignals();
                _logger.LogWarning("[{LogCategory}][安装拒绝] 清除成功 DT309=0, DT310=0，入口已重新武装",
                    logCategory);
                AddLog(isDuringInspection
                    ? "基板安装异常信号已清除，保持异常状态，请复位后重新启动。"
                    : "基板安装异常信号已清除，可重新启动。");
            }
            else
            {
                _logger.LogWarning("[{LogCategory}][安装拒绝] 第二次清除 DT309/DT310 仍失败：{Message}",
                    logCategory,
                    clearResult.Message);
                await _notificationService.ShowWarningAsync(
                    "基板安装异常信号清除失败，请检查 PLC 通信后重试。",
                    dialogTitle);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _installRejectDialogInProgress, 0);
        }
    }

    /// <summary>
    /// 测试中出现基板安装异常时，中止当前检测并锁定异常状态，等待操作员复位。
    /// </summary>
    private async Task AbortInspectionForInstallRejectAsync(
        string workstation,
        ushort rejectCode,
        bool startRequested,
        string executionStage)
    {
        bool engineWasRunning = _inspectionEngine?.IsRunning == true;
        TestUIState stateBeforeAbort = UiState;

        // 先屏蔽旧回调并递增运行版本，再请求停止，避免晚到结果覆盖 Error 状态。
        _ignoreInspectionCallbacksUntilNextStart = true;
        int runVersion = Interlocked.Increment(ref _inspectionRunVersion);
        _inspectionStarted = false;
        SetUiState(TestUIState.Error);

        InspectionStopWaitResult stopResult = InspectionStopWaitResult.AlreadyStopped;
        if (_inspectionEngine != null)
        {
            stopResult = await _inspectionEngine.StopAndWaitAsync(
                TimeSpan.FromSeconds(2),
                InspectionStopReason.Error,
                CancellationToken.None);
        }

        // 停止等待返回后再次锁定 Error，防止同一轮异步收口刷新成普通状态。
        SetUiState(TestUIState.Error);
        string finalStage = _inspectionEngine?.CurrentExecutionStage ?? executionStage;
        if (stopResult == InspectionStopWaitResult.Timeout)
        {
            _logger.LogError(
                "[检测异常][安装异常][停止超时] Workstation={Workstation}, Address={Address}, Code={Code}, DT120={StartRequested}, StateBefore={StateBefore}, EngineWasRunning={EngineWasRunning}, ExecutionStage={ExecutionStage}, FinalStage={FinalStage}, StopResult={StopResult}, RunVersion={RunVersion}",
                workstation,
                string.Equals(workstation, WorkstationConstants.Left, StringComparison.OrdinalIgnoreCase) ? 309 : 310,
                rejectCode,
                startRequested ? 1 : 0,
                stateBeforeAbort,
                engineWasRunning,
                executionStage,
                finalStage,
                stopResult,
                runVersion);
            AddLog("基板安装异常导致检测中止超时，保持异常状态，请处理设备后复位。");
            return;
        }

        _logger.LogWarning(
            "[检测异常][安装异常][已中止] Workstation={Workstation}, Address={Address}, Code={Code}, DT120={StartRequested}, StateBefore={StateBefore}, EngineWasRunning={EngineWasRunning}, ExecutionStage={ExecutionStage}, FinalStage={FinalStage}, StopResult={StopResult}, RunVersion={RunVersion}",
            workstation,
            string.Equals(workstation, WorkstationConstants.Left, StringComparison.OrdinalIgnoreCase) ? 309 : 310,
            rejectCode,
            startRequested ? 1 : 0,
            stateBeforeAbort,
            engineWasRunning,
            executionStage,
            finalStage,
            stopResult,
            runVersion);
    }

    /// <summary>按拒绝代码生成不暴露 DT 地址的操作员提示。</summary>
    private static string BuildInstallRejectMessage(string workstation, ushort code)
    {
        string sensorMessage = code switch
        {
            1 => "接近传感器未检测到基板，请重新安装后再启动。",
            2 => "物检传感器未检测到基板，请重新安装后再启动。",
            _ => "请检查基板和传感器后重新启动。"
        };

        return code is 1 or 2
            ? $"{workstation}基板安装不到位。\n{sensorMessage}"
            : $"{workstation}基板安装状态异常，请检查基板和传感器后重新启动。";
    }

    private static bool IsEmergencyStopTriggered(PlcControlSignals? previousInputs, PlcControlSignals currentInputs)
    {
        return previousInputs == null
            ? currentInputs.IsEmergencyStop
            : !previousInputs.IsEmergencyStop && currentInputs.IsEmergencyStop;
    }

    /// <summary>
    /// 半实物模式只记录 PLC 控制信号的变化沿，避免 200ms 轮询重复刷屏。
    /// </summary>
    private void LogSemiPhysicalSignalChanges(PlcControlSignals? previousInputs, PlcControlSignals currentInputs)
    {
        if (!IsSemiPhysicalDebugMode || previousInputs == null)
            return;

        LogSemiPhysicalSignalChange("DT120", previousInputs.IsStartRequested, currentInputs.IsStartRequested, "Start");
        LogSemiPhysicalSignalChange("DT121", previousInputs.IsResetRequested, currentInputs.IsResetRequested, "Reset");
        LogSemiPhysicalSignalChange("DT122", previousInputs.IsStopRequested, currentInputs.IsStopRequested, "Stop");
        LogSemiPhysicalSignalChange("DT123", previousInputs.IsEmergencyStop, currentInputs.IsEmergencyStop, "EmergencyStop");
    }

    private void LogSemiPhysicalSignalChange(string signal, bool previousValue, bool currentValue, string action)
    {
        if (previousValue == currentValue)
            return;

        _logger.LogInformation(
            "[半实物][PLC信号变化] {Signal}: {Previous} -> {Current}, Action={Action}",
            signal,
            previousValue ? 1 : 0,
            currentValue ? 1 : 0,
            action);
    }

    /// <summary>
    /// 半实物调试面板的主动动作统一使用专用前缀；Fake/真实模式不输出该前缀。
    /// </summary>
    private void LogSemiPhysicalDebugAction(string action, string signal, ushort value)
    {
        if (!IsSemiPhysicalDebugMode)
            return;

        _logger.LogInformation(
            "[半实物][调试动作] Action={Action}, Signal={Signal}, Value={Value}",
            action,
            signal,
            value);
    }

    /// <summary>
    /// 半实物主动测试场景中的预期失败标识。真实业务异常仍由原有 Error/Warning 日志记录。
    /// </summary>
    private void LogSemiPhysicalExpectedFailure(string scenario, string? message)
    {
        if (!IsSemiPhysicalDebugMode)
            return;

        _logger.LogWarning(
            "[半实物][异常注入] Scenario={Scenario}, ExpectedFailure=true, Message={Message}",
            scenario,
            message);
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

        // Error → RejectNeedReset；正常完成态允许下一轮启动，单项 NG 正常收口也属于完成态。
        if (UiState == TestUIState.Error)
        {
            _logger.LogWarning("[启动请求][拒绝] 当前状态需要先复位，来源={Source}", source);
            return StartRequestDecision.RejectNeedReset;
        }

        // Ready / CanStart / CompletedPass / CompletedFail / SingleItemNgStopped → 继续完整校验
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
                await RejectStartAsync(BuildStartValidationResult(), source);
                return;

            case StartRequestDecision.RejectEmergencyStop:
                await RejectStartAsync(BuildStartValidationResult(), source);
                return;

            case StartRequestDecision.RejectBusy:
                await RejectStartAsync(BuildStartValidationResult(), source);
                return;

            case StartRequestDecision.ContinueValidation:
                break;
        }

        // ── 继续完整启动校验 ──
        if (!await TryEnterControlActionAsync(InspectionControlAction.Starting))
        {
            await RejectStartAsync(BuildStartValidationResult(), source);
            return;
        }

        try
        {
            _startingCts = new CancellationTokenSource();
            var startingToken = _startingCts.Token;

            // 启动复核
            var validationResult = BuildStartValidationResult(allowCurrentStartingAction: true);
            if (!validationResult.CanStart)
            {
                await RejectStartAsync(validationResult, source);
                return;
            }

            startingToken.ThrowIfCancellationRequested();

            bool dmmPingOk = await _multimeterDevice.PingAsync(startingToken).ConfigureAwait(false);
            if (!dmmPingOk)
            {
                await RejectStartAsync(CreateStartFailureResult("万用表无法通信，请检查网络连接后重试。",
                    "万用表通信验证失败。"), source);
                return;
            }

            startingToken.ThrowIfCancellationRequested();

            bool isCompletedRun = UiState is TestUIState.CompletedPass
                or TestUIState.CompletedFail
                or TestUIState.SingleItemNgStopped;

            if (isCompletedRun)
            {
                // 完成态旧结果必须在 DT234 写成功后才清除；清理失败时仍保留上一轮显示。
                var pcReadyResult = await _plcDevice.WritePcReadyAsync(startingToken).ConfigureAwait(false);
                if (!pcReadyResult.IsSuccess)
                {
                    await RejectStartAsync(CreateStartFailureResult("启动允许信号发送失败，请检查 PLC 通信状态后重试。",
                        $"写 DT234=1 失败：{pcReadyResult.Message}"), source);
                    return;
                }

                startingToken.ThrowIfCancellationRequested();
                if (!await PrepareCompletedRunForNextStartAsync(startingToken).ConfigureAwait(false))
                {
                    await _plcDevice.ClearPcReadyAsync(CancellationToken.None).ConfigureAwait(false);
                    var clearStartResult = await ClearStartRequestWithSingleRetryAsync();
                    if (!clearStartResult.IsSuccess)
                        _logger.LogWarning("[下一轮启动][拒绝] 下一轮准备失败后清除 DT120 仍失败：{Message}", clearStartResult.Message);
                    return;
                }
            }
            else
            {
                var pcReadyResult = await _plcDevice.WritePcReadyAsync(startingToken).ConfigureAwait(false);
                if (!pcReadyResult.IsSuccess)
                {
                    await RejectStartAsync(CreateStartFailureResult("启动允许信号发送失败，请检查 PLC 通信状态后重试。",
                        $"写 DT234=1 失败：{pcReadyResult.Message}"), source);
                    return;
                }

                startingToken.ThrowIfCancellationRequested();
                // 新一轮已通过全部启动复核，之前的耗时现在才允许归零。
                await Application.Current.Dispatcher.InvokeAsync(ResetElapsedTime);
            }

            _logger.LogInformation("[启动复核][通过] 条件满足，万用表通信正常，DT234=1 已写入，开始检测");
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
            // DT234 可能已写入，由新 Flow（如 ResetFlow）的 ClearTransientRunOutputsAsync 清理
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
            _pendingResetAfterStop = true;
            _logger.LogWarning("[复位请求][保留] 当前正在停止，停止收口后补执行复位，来源={Source}", source);
            AddLog($"[复位请求][保留] 当前正在停止，完成停止后自动执行复位，来源={source}");
            return;
        }

        // Starting 中 Reset → 取消 Starting 后执行复位
        if (_currentControlAction == InspectionControlAction.Starting)
        {
            _logger.LogWarning("[复位请求][抢占] Starting 中，取消后执行复位");
            if (_startingCts != null)
                await _startingCts.CancelAsync().ConfigureAwait(false);

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

        if (!await TryEnterControlActionAsync(
                InspectionControlAction.Resetting,
                waitForLock: true,
                ct: CancellationToken.None))
        {
            _logger.LogError("[复位流程][失败] 等待控制锁后仍未能进入复位动作");
            return;
        }

        // 暂停低优先级 UI 轮询，减少请求并发，避免 Polling 与 Engine/复位清理交叉
        SuspendPlcPolling();

        // 复位开始即锁门，旧检测任务和旧回调只能到这里为止。
        _ignoreInspectionCallbacksUntilNextStart = true;
        _isResetting = true;
        _waitDt120ReleaseAfterReset = true;

        // 已取得复位控制锁，代表一轮新的完整复位正式开始，允许本轮失败提示再次显示。
        Interlocked.Exchange(ref _resetFailureNotificationShown, 0);

        Interlocked.Increment(ref _inspectionRunVersion);
        SetUiState(TestUIState.Resetting);
        _logger.LogInformation("[复位请求][执行] 开始执行复位流程，来源={Source}", source);
        AddLog($"正在执行上位机复位操作...（来源={source}）");

        try
        {
            await CancelFinalResultAutoClearAsync("Reset开始").ConfigureAwait(false);

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

                        await EnterResetFailedAsync(
                            "复位操作超时：检测引擎未能安全停止。\n请确认设备就绪后重新尝试复位。",
                            $"检测引擎硬超时未退出，Stage={hardStage}，IsRunning={hardIsRunning}").ConfigureAwait(false);
                        return;
                    }

                    // 引擎在宽限期内退出，继续正常复位
                    _logger.LogInformation("[复位流程][停止] 引擎在软超时后已退出，继续复位流程");
                }
            }

            // 统一调用运行输出清理替换手写序列
            await ClearTransientRunOutputsAsync(CancellationToken.None);

            var finalResultClear = await ClearFinalResultWithAuditAsync("Reset", CancellationToken.None);
            if (!finalResultClear.IsSuccess)
            {
                await EnterResetFailedAsync(
                    "复位未完成：产品结果信号未能清除。\n请检查 PLC 通信后重新执行复位。",
                    $"产品结果信号清除失败：{finalResultClear.Message}").ConfigureAwait(false);
                return;
            }

            if (_plcDevice is Devices.Fakes.FakeInspectionHardware fake)
            {
                await fake.WriteInputRegisterAsync(PlcAddressMap.StopSignal, 0, CancellationToken.None);
                await fake.WriteInputRegisterAsync(PlcAddressMap.EmergencyStopSignal, 0, CancellationToken.None);
                _logger.LogInformation("[Fake][控制动作] 复位流程已清除 DT122(停止) 和 DT123(急停)");
            }

            ClearTestItemsForRestart();
            _logger.LogInformation("[复位流程] 已清空界面检测项目结果");

            // 让已排队的低优先级回调先被调度一次；这些回调会被 ShouldIgnoreInspectionCallback 丢弃。
            await Application.Current.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background);

            _inspectionStarted = false;
            _startSignalHandled = true;
            _emergencyDialogAcknowledged = false;
            _emergencyStopDialogVM?.StopPolling();
            _emergencyStopDialogVM = null;
            IsPlcStartRequested = false;
            _inspectionEngine.ClearResetState();
            _logger.LogInformation("[复位流程] 已重置内部状态标志");

            bool stopSignalReleased = await CompleteResetStopSignalFinalizationAsync(CancellationToken.None).ConfigureAwait(false);
            if (!stopSignalReleased)
            {
                await EnterResetFailedAsync(
                    "复位未完成：停止信号 DT122 无法清除，请检查 PLC 通信后重新复位。",
                    "停止信号 DT122 清理或最终确认失败").ConfigureAwait(false);
                return;
            }

            // 第一次清除只确认写入协议应答，不再紧接着读回 DT121 判定成败。
            bool firstResetRequestCleared = await ClearResetRequestWithSingleRetryAsync(
                "第一次",
                CancellationToken.None).ConfigureAwait(false);
            if (!firstResetRequestCleared)
            {
                await EnterResetFailedAsync(
                    "复位未完成：PLC 复位信号清除失败，请检查 PLC 通信后重新执行复位。",
                    "第一次清除 DT121 重试仍失败").ConfigureAwait(false);
                return;
            }

            var validationResult = await ValidateResetCompletionAsync(CancellationToken.None).ConfigureAwait(false);
            if (validationResult != ResetCompletionValidationResult.Success)
            {
                string failure = FormatResetValidationFailure(validationResult);
                await EnterResetFailedAsync(
                    $"复位未完成：{failure}，请检查 PLC 通信后重新复位。",
                    $"复位完成快照验证失败：{validationResult}").ConfigureAwait(false);
                return;
            }

            AddLog("复位完成，已清空所有检测结果，可重新开始测试");
            await _notificationService.ShowInfoAsync(
                "复位完成，已清空所有检测结果，可重新开始测试。",
                "复位完成");

            // 成功弹窗关闭后再次清除，吸收复位主体和弹窗期间积累的重复实体复位请求。
            bool finalResetRequestCleared = await ClearResetRequestWithSingleRetryAsync(
                "弹窗后最终",
                CancellationToken.None).ConfigureAwait(false);
            if (!finalResetRequestCleared)
            {
                await EnterResetFailedAsync(
                    "复位收口未完成：PLC 复位信号清除失败，请检查 PLC 通信后重新执行复位。",
                    "成功提示后的最终 DT121 清除重试仍失败").ConfigureAwait(false);
                return;
            }

            // 写入 DT121=0 已成功，清除缓存中的旧复位高电平后再开放下一次复位和启动。
            var cachedInputs = _lastPlcInputs;
            _lastPlcInputs = new PlcControlSignals
            {
                IsStartRequested = cachedInputs?.IsStartRequested == true,
                IsResetRequested = false,
                IsStopRequested = cachedInputs?.IsStopRequested == true,
                IsEmergencyStop = cachedInputs?.IsEmergencyStop == true
            };
            _resetSignalHandled = false;
            _isResetting = false;
            ResetElapsedTime();
            _logger.LogInformation("[复位流程][完成] 重复复位请求已吸收，复位门禁已重新开放");
            ForceRefreshReadyOrCanStartState();
        }
        catch (Exception ex)
        {
            await EnterResetFailedAsync(
                "复位过程发生异常，请检查设备和 PLC 通信后重新执行复位。",
                "复位流程出现未预期异常",
                ex).ConfigureAwait(false);
        }
        finally
        {
            ResumePlcPolling();
            ExitControlAction(InspectionControlAction.Resetting);
        }

    }

    /// <summary>
    /// 统一收口所有可恢复的复位失败，保持 ResetFailed 并等待旧 DT121 完成低电平收口。
    /// 该方法不得递归启动新的完整复位流程。
    /// </summary>
    private async Task EnterResetFailedAsync(
        string operatorMessage,
        string diagnosticReason,
        Exception? exception = null)
    {
        bool isEnteringResetFailed = UiState != TestUIState.ResetFailed;

        if (exception == null)
        {
            _logger.LogWarning("[复位失败收口] {Reason}", diagnosticReason);
        }
        else
        {
            _logger.LogError(exception, "[复位失败收口] {Reason}", diagnosticReason);
        }

        SetUiState(TestUIState.ResetFailed);
        _isResetting = false;
        _resetSignalHandled = true;
        _resetRearmPending = true;
        _lastResetRearmAttemptUtc = DateTime.UtcNow;
        if (isEnteringResetFailed)
        {
            AddLog($"[复位失败收口] {operatorMessage.Replace('\n', ' ')}");
        }

        // 先尽力清除旧请求，但必须等待后续轮询确认 DT121=0 才能重新开放实体复位。
        try
        {
            var clearResult = await _plcDevice.ClearResetRequestAsync(CancellationToken.None).ConfigureAwait(false);
            if (clearResult.IsSuccess)
            {
                _logger.LogWarning("[复位失败收口] 已尽力写入 DT121=0，等待轮询确认入口重新武装");
            }
            else
            {
                _logger.LogWarning("[复位失败收口] 清理旧 DT121 失败，等待通信恢复后重试：{Message}", clearResult.Message);
                AddLog("复位入口等待重新武装：PLC 复位信号暂未清除");
            }
        }
        catch (Exception clearException)
        {
            _logger.LogWarning(clearException, "[复位失败收口] 清理旧 DT121 异常，等待通信恢复后重试");
            AddLog("复位入口等待重新武装：PLC 通信暂不可用");
        }

        if (Interlocked.Exchange(ref _resetFailureNotificationShown, 1) == 0)
        {
            try
            {
                await _notificationService.ShowWarningAsync(operatorMessage, "复位失败").ConfigureAwait(false);
            }
            catch (Exception notificationException)
            {
                _logger.LogWarning(notificationException, "[复位失败收口] 显示复位失败提示异常");
            }
        }
        else
        {
            _logger.LogWarning("[复位失败收口] 本轮复位失败提示已显示，跳过重复提示");
        }
    }

    /// <summary>
    /// ResetFailed 下只清理旧 DT121，不自动执行完整复位；成功写 0 后等待下一轮快照确认。
    /// </summary>
    private async Task TryRearmResetAfterFailureAsync()
    {
        if (UiState != TestUIState.ResetFailed
            || !_resetRearmPending
            || _currentControlAction != InspectionControlAction.None
            || _isResetting
            || !_plcDevice.IsConnected)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (_lastResetRearmAttemptUtc != DateTime.MinValue
            && now - _lastResetRearmAttemptUtc < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _resetRearmInProgress, 1, 0) != 0)
        {
            return;
        }

        _lastResetRearmAttemptUtc = now;
        try
        {
            _logger.LogWarning("[复位重新武装] 开始清理旧 DT121");
            var clearResult = await _plcDevice.ClearResetRequestAsync(CancellationToken.None).ConfigureAwait(false);
            if (clearResult.IsSuccess)
            {
                _logger.LogWarning("[复位重新武装] 已写入 DT121=0，等待下一轮读取确认");
                AddLog("复位重新武装：已写入 DT121=0，等待 PLC 确认");
            }
            else
            {
                _logger.LogWarning("[复位重新武装] 清理旧 DT121 失败：{Message}", clearResult.Message);
                AddLog("复位入口等待重新武装：PLC 复位信号清除失败");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[复位重新武装] 清理旧 DT121 异常");
            AddLog("复位入口等待重新武装：PLC 通信异常");
        }
        finally
        {
            Interlocked.Exchange(ref _resetRearmInProgress, 0);
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
        if (UiState == TestUIState.ResetFailed)
        {
            // ResetFailed 下停止请求不产生新的业务状态，只保留低级别诊断。
            _logger.LogDebug("[动作仲裁][吸收] ResetFailed 状态下忽略停止请求，来源={Source}", source);
            return;
        }

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

        }

        if (!await TryEnterControlActionAsync(
                InspectionControlAction.Stopping,
                waitForLock: true,
                ct: CancellationToken.None))
        {
            _logger.LogError("[停止流程][失败] 等待控制锁后仍未能进入停止动作");
            return;
        }

        // 暂停低优先级 UI 轮询，减少请求并发
        SuspendPlcPolling();

        try
        {
            _ignoreInspectionCallbacksUntilNextStart = true;
            Interlocked.Increment(ref _inspectionRunVersion);
            await CancelFinalResultAutoClearAsync("Stop开始").ConfigureAwait(false);
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
                    var timeoutFinalResult = await ClearFinalResultWithAuditAsync("Stop-EngineTimeout", CancellationToken.None).ConfigureAwait(false);
                    if (!timeoutFinalResult.IsSuccess)
                        AddLog("停止收口时产品结果信号清除失败，请执行复位完成清理。");
                    await CompleteStopSignalHandshakeAsync("StopAndWaitTimeout", CancellationToken.None).ConfigureAwait(false);
                    _inspectionStarted = false;
                    IsPlcStartRequested = false;
                    SetUiState(TestUIState.Paused);
                    AddLog("停止处理中超时：请执行复位完成设备收口。");
                    return;
                }
            }

            _inspectionEngine.ClearResetState();
            await ClearTransientRunOutputsAsync(CancellationToken.None).ConfigureAwait(false);
            var finalResult = await ClearFinalResultWithAuditAsync("Stop", CancellationToken.None).ConfigureAwait(false);
            bool stopSignalReleased = await CompleteStopSignalHandshakeAsync("NormalStopFlow", CancellationToken.None).ConfigureAwait(false);

            _inspectionStarted = false;
            IsPlcStartRequested = false;
            SetUiState(TestUIState.Paused);
            if (stopSignalReleased)
            {
                AddLog("[停止流程] 已停止，必须复位后才能重新从第一项开始检测");
                _logger.LogInformation("[停止流程][审计] 停止收口完成，DT122 已释放，页面保持 Paused");
            }
            else
            {
                AddLog("[停止流程] 已停止，但 DT122 仍未稳定释放，请执行复位完成收口");
                _logger.LogWarning("[停止流程][DT122] 稳定确认未释放，页面保持 Paused，等待 ResetFlow 兜底");
            }

            if (!finalResult.IsSuccess)
                AddLog("[停止流程] 产品结果信号清除失败，保持等待复位，不允许继续启动");
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

            if (_pendingResetAfterStop)
            {
                if (Volatile.Read(ref _emergencyStopPending) != 0)
                {
                    _pendingResetAfterStop = false;
                    _logger.LogWarning("[复位请求][让路] 急停已锁存，停止后的待处理复位让路给急停，待急停解除后由操作员复位");
                }
                else
                {
                    _pendingResetAfterStop = false;
                    _logger.LogWarning("[复位请求][补执行] 停止流程已释放控制锁，开始执行待处理复位");
                    await ExecuteResetFlowAsync(InspectionActionSource.PendingAfterStop).ConfigureAwait(false);
                }
            }
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

        // 急停最高优先级：先锁存请求，再等待控制锁，不能因当前动作占锁而丢弃。
        if (Interlocked.Exchange(ref _emergencyStopPending, 1) == 1)
        {
            _logger.LogWarning("[急停流程][锁存] 已有急停流程正在等待或执行，合并本次请求，来源={Source}", source);
            return;
        }

        try
        {
            if (_currentControlAction == InspectionControlAction.Starting && _startingCts != null)
            {
                _logger.LogWarning("[急停流程][抢占] Starting 中，取消启动后等待控制锁");
                await _startingCts.CancelAsync().ConfigureAwait(false);
            }

            // 无论引擎是否正在运行，先发出急停中止请求，避免等待控制锁期间继续测量。
            if (_inspectionEngine?.IsRunning == true)
                _inspectionEngine.StopForEmergencyStop();

            if (!await TryEnterControlActionAsync(
                    InspectionControlAction.EmergencyStopping,
                    waitForLock: true,
                    ct: CancellationToken.None))
            {
                _logger.LogError("[急停流程][失败] 等待控制锁后仍未能进入急停动作");
                return;
            }

            // 暂停低优先级 UI 轮询，减少请求并发
            SuspendPlcPolling();

            _logger.LogWarning("[急停流程][进入] 急停信号 DT123，来源={Source}，执行急停收口", source);
            AddLog($"[急停流程] 急停信号，来源={source}，正在停止检测...");

            try
            {
                _ignoreInspectionCallbacksUntilNextStart = true;
                Interlocked.Increment(ref _inspectionRunVersion);
                await CancelFinalResultAutoClearAsync("EmergencyStop开始").ConfigureAwait(false);
                SetUiState(TestUIState.EmergencyStop);

                if (_inspectionEngine!.IsRunning)
                    _inspectionEngine.StopForEmergencyStop();

                // 统一调用运行输出清理替换手写序列
                await ClearTransientRunOutputsAsync(CancellationToken.None);
                var finalResult = await ClearFinalResultWithAuditAsync("EmergencyStop", CancellationToken.None);
                if (!finalResult.IsSuccess)
                    _logger.LogError("[急停流程][审计] 产品结果信号清除失败，保持急停状态并等待复位");

                _inspectionEngine.ClearResetState();
                _inspectionStarted = false;
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
        finally
        {
            Volatile.Write(ref _emergencyStopPending, 0);
        }
    }
    #endregion

    /// <summary>
    /// 弹出急停模态弹窗。
    /// 弹窗只接收操作员解除请求；真正的清信号、读回确认和状态切换统一由 CompleteEmergencyStopReleaseAsync 完成。
    /// </summary>
    private async Task ShowEmergencyStopDialogAsync()
    {
        _logger.LogWarning("[急停弹窗][显示请求]");

        if (_isShowingEmergencyDialog)
        {
            _logger.LogDebug("[急停弹窗][跳过] 弹窗已经显示或正在等待显示");
            return;
        }

        _isShowingEmergencyDialog = true;
        try
        {
            await using var dialogLease = await _dialogCoordinator.AcquireEmergencyDialogAsync();

            bool isFake = _plcDevice is Devices.Fakes.FakeInspectionHardware;
            bool canSimulateAlarmRelease = isFake || IsSemiPhysicalDebugMode;

            // 先创建 dialog，确保 closeDialog 回调能捕获 dialog 引用。
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
                    _emergencyDialogAcknowledged = true;
                    return true;
                },
                canSimulateAlarmRelease: canSimulateAlarmRelease,
                isSemiPhysicalDebugMode: IsSemiPhysicalDebugMode,
                logger: _serviceProvider.GetService<ILogger<EmergencyStopDialogViewModel>>());

            dialog.DataContext = vm;
            _emergencyStopDialogVM = vm;

            // 启动 DT303 后台轮询。
            vm.StartPolling();

            _logger.LogWarning("[急停弹窗][显示开始]");
            // 模态显示弹窗（阻塞，直到用户点击“解除”）。安全动作已经在前序流程完成。
            dialog.ShowDialog();
        }
        finally
        {
            _emergencyStopDialogVM?.StopPolling();
            _emergencyStopDialogVM = null;
            _isShowingEmergencyDialog = false;
            _logger.LogWarning("[急停弹窗][显示结束]");
        }
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
                emergencyResult = await _plcDevice.ClearEmergencyStopRequestAsync(ct).ConfigureAwait(false);
                if (emergencyResult == null || !emergencyResult.IsSuccess)
                {
                    _logger.LogWarning("[急停解除][失败] 清 DT123 失败，第 {Attempt}/{MaxRetries} 次：{Message}",
                        attempt, maxRetries, emergencyResult?.Message ?? "返回结果为 null");
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

                    _logger.LogInformation("[急停解除][确认成功] DT123=0，状态切换 AwaitingReset");
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
        });

        _logger.LogWarning("[急停解除][失败] DT123 未确认释放，保持 EmergencyStop");
        return false;
    }

    #region 统一调试命令（Fake 和半实物共用）

    /// <summary>
    /// 调试启动：写 DT120=1，走 PLC 轮询启动复核链路，不直接 RunInspectionAsync。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugStartAsync()
    {
        LogSemiPhysicalDebugAction("Start", "DT120", 1);

        if (_inspectionEngine == null) return;

        // ── 重复启动快速忽略（不写 DT120）──
        if (UiState == TestUIState.Testing || _inspectionStarted || _currentControlAction == InspectionControlAction.Starting)
        {
            _logger.LogInformation("[调试面板][启动请求][忽略] 当前已在检测中，来源=DebugPanel");
            return;
        }

        if (UiState == TestUIState.EmergencyStop)
        {
            await RejectStartAsync(BuildStartValidationResult(), InspectionActionSource.DebugPanel, clearStartRequest: false);
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelName) || string.IsNullOrWhiteSpace(SerialNumber)
            || string.IsNullOrWhiteSpace(SchemeName) || IsSchemeNameInvalid
            || string.IsNullOrWhiteSpace(OperatorName))
        {
            await RejectStartAsync(BuildStartValidationResult(), InspectionActionSource.DebugPanel, clearStartRequest: false);
            return;
        }

        if (TestItems.Count == 0)
            await LoadPlanItemsAsync();

        if (TestItems.Count == 0)
        {
            await RejectStartAsync(BuildStartValidationResult(), InspectionActionSource.DebugPanel, clearStartRequest: false);
            return;
        }

        // 写入 DT120 前先复用统一启动校验；已知 PLC 离线时不产生无意义写入。
        var validationResult = BuildStartValidationResult();
        if (!validationResult.CanStart)
        {
            await RejectStartAsync(validationResult, InspectionActionSource.DebugPanel, clearStartRequest: false);
            return;
        }

        _logger.LogInformation("[调试按钮][启动][请求] 上位机写入 DT120=1，等待 PLC 轮询统一消费");

        var result = await _plcDevice.RequestStartAsync(CancellationToken.None).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var operatorMessage = !_plcDevice.IsConnected
                ? "PLC 未连接，请检查 PLC 电源、网线和系统设置后重试。"
                : "启动请求发送失败，请检查 PLC 通信状态后重试。";
            await RejectStartAsync(CreateStartFailureResult(operatorMessage,
                $"写 DT120=1 失败：{result.Message}"), InspectionActionSource.DebugPanel, clearStartRequest: false);
            return;
        }

        AddLog("[调试面板] 启动请求已发送，等待 PLC 轮询统一处理");
    }

    /// <summary>
    /// 调试停止：写 DT122=1，成功后执行停止收口流程。
    /// </summary>
    [RelayCommand]
    private async Task TriggerDebugStopAsync()
    {
        LogSemiPhysicalDebugAction("Stop", "DT122", 1);

        if (IsStopRequestInProgress())
        {
            _logger.LogWarning("[动作仲裁][忽略] 当前正在停止或等待复位，重复停止请求未写入 DT122，UiState={UiState}, Action={Action}",
                UiState, _currentControlAction);
            return;
        }

        _logger.LogInformation("[调试按钮][停止][请求] 上位机写入 DT122=1，等待 PLC 轮询统一消费");

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
        LogSemiPhysicalDebugAction("Reset", "DT121", 1);

        bool isResetFailedRetry =
            UiState == TestUIState.ResetFailed
            && _currentControlAction == InspectionControlAction.None
            && !_isResetting;

        if (!isResetFailedRetry && IsResetRequestInProgress())
        {
            _logger.LogWarning("[动作仲裁][忽略] 复位正在执行，重复复位请求未写入 DT121，UiState={UiState}, Action={Action}",
                UiState, _currentControlAction);
            return;
        }

        _logger.LogInformation("[调试按钮][复位][请求] 上位机写入 DT121=1，等待 PLC 轮询统一消费");

        SuspendPlcPolling();
        // 写请求尚未被 PLC 轮询消费前先占住复位入口，避免连续点击重复写入 DT121=1。
        _isResetting = true;
        Interlocked.Exchange(ref _resetFailureNotificationShown, 0);
        try
        {
            if (isResetFailedRetry)
            {
                _logger.LogWarning("[复位重试] 收到新的调试面板复位请求，先处理旧 DT121");
                var clearOldRequestResult = await _plcDevice.ClearResetRequestAsync(CancellationToken.None).ConfigureAwait(false);
                if (!clearOldRequestResult.IsSuccess)
                {
                    await EnterResetFailedAsync(
                        "复位入口暂时无法重新武装，请检查 PLC 通信后重试。",
                        $"调试面板复位前清理旧 DT121 失败：{clearOldRequestResult.Message}").ConfigureAwait(false);
                    return;
                }

                _resetRearmPending = false;
                _resetSignalHandled = false;
                _lastResetRearmAttemptUtc = DateTime.MinValue;
            }

            var result = await _plcDevice.RequestResetAsync(CancellationToken.None).ConfigureAwait(false);
            if (!HandleControlSignalWriteResult(result, "调试面板", "DT121", "复位"))
            {
                await EnterResetFailedAsync(
                    "复位请求发送失败，请检查 PLC 通信后重试。",
                    $"调试面板写入 DT121=1 失败：{result.Message}").ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            await EnterResetFailedAsync(
                "复位请求发送异常，请检查 PLC 通信后重试。",
                "调试面板复位请求写入过程发生异常",
                ex).ConfigureAwait(false);
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
        LogSemiPhysicalDebugAction("EmergencyStop", "DT123", 1);

        _logger.LogInformation("[调试按钮][急停][请求] 上位机写入 DT123=1，等待 PLC 轮询统一消费");

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

        LogSemiPhysicalExpectedFailure($"{actionName}WriteFailure", result.Message);
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
    /// 判断复位流程是否已经占用控制入口，避免调试按钮再次写入 DT121=1。
    /// </summary>
    private bool IsResetRequestInProgress()
    {
        return _currentControlAction == InspectionControlAction.Resetting
               || _isResetting
               || UiState == TestUIState.Resetting
               || (UiState != TestUIState.ResetFailed && _resetSignalHandled);
    }

    /// <summary>
    /// 判断停止请求是否应被当前停止、复位或待复位状态吸收，避免再次写入 DT122=1。
    /// </summary>
    private bool IsStopRequestInProgress()
    {
        return _currentControlAction is InspectionControlAction.Stopping or InspectionControlAction.Resetting
               || _isResetting
               || UiState is TestUIState.Resetting or TestUIState.ResetFailed or TestUIState.AwaitingReset or TestUIState.Paused;
    }

    /// <summary>
    /// 半实物/真实模式复位按钮（写 DT121=1，写后等待 PLC 轮询统一消费）。
    /// 真实模式下工人按实体按钮时由 PLC 轮询触发 ExecuteResetFlowAsync，
    /// 上位机按钮作为备用入口。
    /// </summary>
    [RelayCommand]
    private async Task TriggerPlcResetAsync()
    {
        bool isResetFailedRetry =
            UiState == TestUIState.ResetFailed
            && _currentControlAction == InspectionControlAction.None
            && !_isResetting;

        // ResetFailed 且控制动作已退出时属于人工新请求，不能被旧 DT121 门禁拦截。
        if (!isResetFailedRetry && IsResetRequestInProgress())
        {
            _logger.LogWarning("[动作仲裁][忽略] 复位正在执行，重复复位请求未写入 DT121，UiState={UiState}, Action={Action}",
                UiState, _currentControlAction);
            return;
        }

        SuspendPlcPolling();
        // 写请求尚未被 PLC 轮询消费前先占住复位入口，避免连续点击重复写入 DT121=1。
        _isResetting = true;
        Interlocked.Exchange(ref _resetFailureNotificationShown, 0);
        try
        {
            if (isResetFailedRetry)
            {
                _logger.LogWarning("[复位重试] 收到新的鼠标复位请求，先处理旧 DT121");
                var clearOldRequestResult = await _plcDevice.ClearResetRequestAsync(CancellationToken.None).ConfigureAwait(false);
                if (!clearOldRequestResult.IsSuccess)
                {
                    await EnterResetFailedAsync(
                        "复位入口暂时无法重新武装，请检查 PLC 通信后重试。",
                        $"鼠标复位前清理旧 DT121 失败：{clearOldRequestResult.Message}").ConfigureAwait(false);
                    return;
                }

                _resetRearmPending = false;
                _resetSignalHandled = false;
                _lastResetRearmAttemptUtc = DateTime.MinValue;
            }

            var result = await _plcDevice.RequestResetAsync(CancellationToken.None).ConfigureAwait(false);

            if (!HandleControlSignalWriteResult(result, "复位", "DT121", "复位"))
            {
                await EnterResetFailedAsync(
                    "复位请求发送失败，请检查 PLC 通信后重试。",
                    $"写入 DT121=1 失败：{result.Message}").ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("[复位按钮][请求] 已写入 DT121=1，等待 PLC 轮询统一消费");
        }
        catch (Exception ex)
        {
            await EnterResetFailedAsync(
                "复位请求发送异常，请检查 PLC 通信后重试。",
                "鼠标复位请求写入过程发生异常",
                ex).ConfigureAwait(false);
        }
        finally
        {
            ResumePlcPolling();
        }
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

        await Application.Current.Dispatcher.InvokeAsync(StartElapsedTimeTimer);

        var result = await Task.Run(async () =>
        {
            return await _inspectionEngine.RunInspectionAsync(
                barcode, modelName, operatorName, CancellationToken.None);
        }).ConfigureAwait(false);

        await Application.Current.Dispatcher.InvokeAsync(() => StopElapsedTimeTimer(result.Duration));

        // 复位后返回的旧检测任务直接丢弃，不再处理结果和弹提示
        if (runVersion != _inspectionRunVersion || _ignoreInspectionCallbacksUntilNextStart)
        {
            _logger.LogWarning("[复位流程][审计] 旧检测任务已返回但被丢弃，RunVersion={RunVersion}, CurrentRunVersion={CurrentVersion}",
                runVersion, _inspectionRunVersion);
            return;
        }

        bool isSingleItemNgNormalCompletion =
            result.StopReason == InspectionStopReason.SingleItemNg
            && !result.IsAborted
            && !result.IsAllPassed;

        if (isSingleItemNgNormalCompletion)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                AddLog($"❎ {result.ErrorMessage}");
            });
            _logger.LogWarning("[运行页][审计] 单项 NG 后按设置结束本轮，进入最终 NG 收口: {ErrorMessage}", result.ErrorMessage);
            await CompleteInspectionResultOnUiAsync(result, isSingleItemNgStopped: true).ConfigureAwait(false);
        }
        else if (result.IsAborted || !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            if (IsExpectedControlStopReason(result.StopReason))
            {
                _logger.LogWarning("[运行页][审计] 检测因 {StopReason} 收口，中止提示交由对应流程处理", result.StopReason);
                return;
            }

            // 其他收口流程若已先切到 AwaitingReset，检测任务返回时不得覆盖既有状态。
            if (UiState == TestUIState.AwaitingReset)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    AddLog($"⚠️ {result.ErrorMessage ?? "本轮检测因设备通信中断停止"}，请复位后重新开始。");
                });
                _logger.LogWarning("[运行页][审计] 页面已进入 AwaitingReset，忽略检测任务异常结果，不自动续跑");
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
                : result.ErrorMessage;

            // 文件日志保留“检测中止”分类，弹窗标题已经是“检测中止”，正文不重复添加前缀。
            _logger.LogWarning("[运行页][审计] 检测中止：{Message}", message);
            await _notificationService
                .ShowWarningAsync(message, "检测中止")
                .ConfigureAwait(false);
        }
        else
        {
            if (result.IsAllPassed)
            {
                // 最终结果写入已在 InspectionEngine 中确认成功，此处立即开始计时，不能等待 CSV 保存。
                await ScheduleOkFinalResultAutoClearAsync(runVersion).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("[PLC最终结果][NG保持] 最终 NG 结果保持到完整复位，RunVersion={RunVersion}", runVersion);
                await Application.Current.Dispatcher.InvokeAsync(() => AddLog("最终结果为 NG，保持至完整复位后清除。"));
            }

            // 正常完成 → 自动保存、PLC 收口，最后再显示 OK/NG
            await CompleteInspectionResultOnUiAsync(result, isSingleItemNgStopped: false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 切回 UI 线程执行检测完成收口，并完整等待内部异步任务结束。
    /// </summary>
    private async Task CompleteInspectionResultOnUiAsync(
        InspectionResult result,
        bool isSingleItemNgStopped)
    {
        var dispatcherOperation = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            AddLog(isSingleItemNgStopped
                ? $"本轮因单项 NG 提前结束，正在处理最终结果... (耗时{result.Duration.TotalSeconds:F1}s)"
                : $"所有检查项目已执行完成，正在处理结果... (耗时{result.Duration.TotalSeconds:F1}s)");

            await OnAllPinsTestedAsync(isSingleItemNgStopped);
        });

        await dispatcherOperation.Task.Unwrap().ConfigureAwait(false);
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
        _deviceManager.ScannerFrameRejected += OnScannerFrameRejected;

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
        _deviceManager.ScannerFrameRejected -= OnScannerFrameRejected;

        if (_inspectionEngine != null)
        {
            _inspectionEngine.StepStarted -= OnStepStarted;
            _inspectionEngine.StepCompleted -= OnStepCompleted;
            _inspectionEngine.LogMessage -= OnInspectionLogMessage;
        }

        _hardwareEventsSubscribed = false;
        _logger.LogInformation("[运行页收尾] 已退订硬件和检测引擎事件，避免旧页面重复响应完成事件");
    }

    /// <summary>
    /// 设备断线后的统一恢复提示。设备恢复连接只恢复通信能力，不自动续跑旧项目，必须由操作员复位。
    /// </summary>
    private static string BuildDeviceRecoveryMessage(string deviceType)
        => deviceType switch
        {
            "PLC" => "PLC 通信已中断，本轮检测已停止。\n请检查 PLC 电源和网线，等待自动重连或点击“PLC重连”。\nPLC 恢复连接后，请点击“复位”重新准备检测。",
            "DMM" => "万用表通信已中断，本轮检测已停止。\n请检查万用表电源和网线，等待自动重连或点击“万用表重连”。\n设备恢复连接后，请点击“复位”重新准备检测。",
            _ => "设备通信已中断，本轮检测已停止。\n请检查设备连接，恢复后点击“复位”重新准备检测。"
        };

    private void HandleDeviceDisconnected(string deviceType)
    {
        if (Interlocked.CompareExchange(ref _deviceDisconnectHandling, 1, 0) != 0)
            return;

        _ = HandleDeviceDisconnectedAsync(deviceType);
    }

    private async Task HandleDeviceDisconnectedAsync(string deviceType)
    {
        bool emergencyDialogPendingOrActive = IsEmergencyDialogPendingOrActive();

        try
        {
            if (deviceType == "DMM")
            {
                TestItems.FirstOrDefault(item => item.CheckResult == "测试中")
                    ?.MarkMeasurementFailed("通信超时");
            }

            // 设备恢复后不自动续跑旧项目，先屏蔽旧检测回调并保持 Error，等待操作员复位。
            _ignoreInspectionCallbacksUntilNextStart = true;
            Interlocked.Increment(ref _inspectionRunVersion);
            _inspectionStarted = false;
            if (!emergencyDialogPendingOrActive && UiState != TestUIState.EmergencyStop)
                SetUiState(TestUIState.Error);
            _logger.LogWarning(
                "[设备异常][{DeviceType}] 通信中断，本轮检测已中止，最终状态为 Error；设备恢复后不自动续跑旧项目，等待复位",
                deviceType);

            if (_inspectionEngine?.IsRunning == true)
            {
                var stopResult = await _inspectionEngine.StopAndWaitAsync(
                    TimeSpan.FromSeconds(2),
                    InspectionStopReason.DeviceDisconnected,
                    CancellationToken.None);
                if (stopResult == InspectionStopWaitResult.Timeout)
                {
                    _logger.LogWarning(
                        "[设备异常][{DeviceType}] 检测引擎停止未完全确认，仍保持 Error 状态；Reason={Reason}",
                        deviceType,
                        stopResult);
                }
            }

            if (emergencyDialogPendingOrActive
                || UiState == TestUIState.EmergencyStop
                || _dialogCoordinator.IsEmergencyDialogPendingOrActive)
            {
                _logger.LogWarning(
                    "[设备恢复][{DeviceType}][提示抑制] 急停对话框正在等待或显示，已抑制普通设备异常弹窗",
                    deviceType);
                return;
            }

            await _notificationService.ShowWarningAsync(
                BuildDeviceRecoveryMessage(deviceType),
                "设备通信中断");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[设备异常][{DeviceType}] 断线收口异常，保持 Error 状态，处理设备连接后复位", deviceType);
            if (!emergencyDialogPendingOrActive && UiState != TestUIState.EmergencyStop)
                SetUiState(TestUIState.Error);
        }
        finally
        {
            Volatile.Write(ref _deviceDisconnectHandling, 0);
        }
    }

    private bool IsEmergencyDialogPendingOrActive()
    {
        return _isShowingEmergencyDialog
            || UiState == TestUIState.EmergencyStop
            || _dialogCoordinator.IsEmergencyDialogPendingOrActive;
    }

    private void OnPlcConnectionStateChanged(
        object? sender,
        DeviceConnectionStateChangedEventArgs? e)
    {
        if (e is null)
        {
            _logger.LogError(
                "[设备状态][PLC][防御] 收到空连接状态事件参数，已忽略");
            return;
        }

        void ApplyState()
        {
            IsPlcConnected = e.IsConnected;
            PlcStatusText = e.StatusText;
            PlcConnectionStatus = e.Status;
            if (e.Status == DeviceConnectionStatus.Disconnected && HasActiveInspectionContext())
                HandleDeviceDisconnected("PLC");
            else if (e.Status == DeviceConnectionStatus.Connecting)
                _logger.LogInformation("[设备状态][PLC] 连接中，仅更新状态，不触发检测断线收口");
            UpdateUIState();
        }

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            _logger.LogWarning(
                "[设备状态][PLC] Dispatcher 不可用，跳过 UI 状态更新");
            return;
        }

        if (dispatcher.CheckAccess())
            ApplyState();
        else
            dispatcher.BeginInvoke((Action)ApplyState);
    }

    private void OnDmmConnectionStateChanged(
        object? sender,
        DeviceConnectionStateChangedEventArgs? e)
    {
        if (e is null)
        {
            _logger.LogError(
                "[设备状态][DMM][防御] 收到空连接状态事件参数，已忽略");
            return;
        }

        void ApplyState()
        {
            IsDmmConnected = e.IsConnected;
            DmmStatusText = e.StatusText;
            DmmConnectionStatus = e.Status;
            if (e.Status == DeviceConnectionStatus.Disconnected && HasActiveInspectionContext())
                HandleDeviceDisconnected("DMM");
            else if (e.Status == DeviceConnectionStatus.Connecting)
                _logger.LogInformation("[设备状态][DMM] 连接中，仅更新状态，不触发检测断线收口");
            UpdateUIState();
        }

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            _logger.LogWarning(
                "[设备状态][DMM] Dispatcher 不可用，跳过 UI 状态更新");
            return;
        }

        if (dispatcher.CheckAccess())
            ApplyState();
        else
            dispatcher.BeginInvoke((Action)ApplyState);
    }

    private void OnScannerConnectionStateChanged(
        object? sender,
        DeviceConnectionStateChangedEventArgs? e)
    {
        if (e is null)
        {
            _logger.LogError(
                "[设备状态][Scanner][防御] 收到空连接状态事件参数，已忽略");
            return;
        }

        void ApplyState()
        {
            IsScannerConnected = e.IsConnected;
            ScannerStatusText = e.StatusText;
            ScannerConnectionStatus = e.Status;
            _logger.LogInformation(
                "[设备状态][Scanner] 状态更新为 {Status}，扫描仪不参与电气检测断线收口",
                e.Status);
            UpdateUIState();
        }

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            _logger.LogWarning(
                "[设备状态][Scanner] Dispatcher 不可用，跳过 UI 状态更新");
            return;
        }

        if (dispatcher.CheckAccess())
            ApplyState();
        else
            dispatcher.BeginInvoke((Action)ApplyState);
    }

    private void OnScannerBarcodeParsed(object? sender, BarcodeParsedEventArgs e)
        => RouteScannerBarcodeParsed(e);

    /// <summary>统一处理硬件扫码和恢复弹窗确认后的产品条码。</summary>
    private void RouteScannerBarcodeParsed(BarcodeParsedEventArgs e)
    {
        if (_suspendRunPageBarcodeHandling)
        {
            _logger.LogDebug("[扫码路由] 选择弹窗打开，运行页忽略本次产品条码");
            return;
        }

        void ApplyBarcode()
        {
            try
            {
                if (!_hardwareEventsSubscribed)
                {
                    _logger.LogDebug("[扫码UI][忽略] 页面已离开，丢弃排队中的旧扫码");
                    return;
                }

                if (_suspendRunPageBarcodeHandling)
                {
                    _logger.LogDebug("[扫码UI][忽略] 选择弹窗打开，运行页忽略本次产品条码");
                    return;
                }

                if (UiState == TestUIState.Testing)
                {
                    _logger.LogInformation("[扫码UI][忽略] 测试中禁止扫码");
                    AddLog("⚠️ 测试中禁止扫码，条码已忽略");
                    return;
                }

                if (e is null
                    || string.IsNullOrWhiteSpace(e.RawBarcode)
                    || !e.IsProductBarcodeValid)
                {
                    if (e is not null)
                        HandleInvalidProductBarcode(e);
                    else
                        _logger.LogWarning("[扫码校验][失败] 收到空的条码解析结果");
                    return;
                }

                // 只有产品条码通过完整性校验后，才允许写入运行页属性和触发后续联动。
                string modelName = e.ModelName.Trim();
                string serialNumber = e.SerialPart?.Trim() ?? string.Empty;
                CancelProductIdentityNotification();
                _lastNotifiedProductIdentityKey = null;
                _pendingBarcodePlanAutoSelectMachine = modelName;
                _isApplyingScannerBarcode = true;
                try
                {
                    ModelName = modelName;
                    SerialNumber = serialNumber;
                }
                finally
                {
                    _isApplyingScannerBarcode = false;
                }

                _ = NotifyProductIdentityAsync(
                    "Scanner",
                    modelName,
                    serialNumber,
                    allowRepeat: true,
                    CancellationToken.None);

                _logger.LogInformation(
                    "[扫码UI] 运行页已应用条码, Model={Model}, Serial={Serial}",
                    modelName,
                    serialNumber);
                AddLog($"📷 扫描到条码: 机种={modelName}, 序列号={serialNumber}");
                ShowReferenceMachineMismatchIfNeeded(isScannerInput: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[扫码UI][异常] 运行页应用条码失败");
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null
            || dispatcher.HasShutdownStarted
            || dispatcher.HasShutdownFinished)
        {
            _logger.LogWarning("[扫码UI][忽略] Dispatcher 不可用");
        }
        else if (dispatcher.CheckAccess())
        {
            ApplyBarcode();
        }
        else
        {
            dispatcher.BeginInvoke((Action)ApplyBarcode, DispatcherPriority.Normal);
        }
    }

    private void OnScannerFrameRejected(object? sender, ScannerFrameRejectedEventArgs e)
    {
        if (_suspendRunPageBarcodeHandling)
        {
            _logger.LogInformation(
                "[扫码UI][忽略] 恢复弹窗打开期间收到异常帧，不再弹第二个异常窗口: TotalBytes={TotalBytes}",
                e.TotalBytes);
            return;
        }

        if (Interlocked.CompareExchange(ref _scannerFrameWarningShowing, 1, 0) != 0)
            return;

        void ShowWarning()
        {
            AddLog("⚠️ 扫码数据异常，已忽略，请重新扫码");
            _ = ShowScannerFrameRejectedWarningAsync(e);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            Interlocked.Exchange(ref _scannerFrameWarningShowing, 0);
            _logger.LogWarning("[扫码UI][忽略] Dispatcher 不可用，无法显示超长帧提示");
            return;
        }

        if (dispatcher.CheckAccess())
            ShowWarning();
        else
            dispatcher.BeginInvoke((Action)ShowWarning);
    }

    private async Task ShowScannerFrameRejectedWarningAsync(ScannerFrameRejectedEventArgs args)
    {
        try
        {
            _logger.LogWarning(
                "[扫码UI][异常帧] 已忽略超长扫码数据: TotalBytes={TotalBytes}, Reason={Reason}, HexPreview={HexPreview}",
                args.TotalBytes,
                args.Reason,
                args.HexPreview);
            await _notificationService.ShowWarningAsync(
                "本次收到的扫码数据长度异常，\n可能包含多次扫码内容或历史残留数据。\n\n本次数据未使用，请重新扫描当前产品条码。",
                "扫码数据异常");
        }
        finally
        {
            Interlocked.Exchange(ref _scannerFrameWarningShowing, 0);
        }
    }

    /// <summary>
    /// 处理已收到但不符合产品条码格式的扫码，不改变当前机种和方案。
    /// </summary>
    private void HandleInvalidProductBarcode(BarcodeParsedEventArgs e)
    {
        string failureReason = string.IsNullOrWhiteSpace(e.ParseFailureReason)
            ? "条码解析发生异常"
            : e.ParseFailureReason!;
        string promptKey = $"{e.Timestamp.Ticks}|{e.RawBarcode}|{failureReason}";
        if (string.Equals(_lastInvalidBarcodePromptKey, promptKey, StringComparison.Ordinal))
            return;

        _lastInvalidBarcodePromptKey = promptKey;
        _logger.LogWarning(
            "[扫码校验][失败] Raw={RawBarcode}, Reason={FailureReason}",
            e.RawBarcode,
            failureReason);

        // 清理动作必须抑制属性回调重新启动重复记录检查，但保留机种和方案。
        _pendingBarcodePlanAutoSelectMachine = null;
        _suppressDuplicateCheck = true;
        try
        {
            InvalidateCurrentSerialVerification();
            SerialNumber = string.Empty;
        }
        finally
        {
            _suppressDuplicateCheck = false;
        }

        RefreshReadyOrCanStartState();
        string displayRawBarcode = FormatRawBarcodeForDisplay(e.RawBarcode);
        string message =
            $"条码内容不完整或格式异常，可能存在污损。\n\n扫码内容：\n{displayRawBarcode}\n\n失败原因：{failureReason}\n\n请检查条码后重新扫码，或手动输入正确的机种名称和序列号。";
        _ = ShowWarningSafelyAsync(message, "扫码失败");
    }

    /// <summary>
    /// 将原始扫码内容格式化为安全、可读且有长度上限的弹窗文本。
    /// 文件日志仍使用未截断的原始值。
    /// </summary>
    private static string FormatRawBarcodeForDisplay(string? rawBarcode)
    {
        if (string.IsNullOrEmpty(rawBarcode))
            return "(空)";

        string display = rawBarcode.Trim()
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        if (display.Length == 0)
            return "(空)";

        return display.Length <= 200
            ? display
            : display[..200] + "…";
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
                item.RecordResult = string.Empty;
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
                item.RecordResult = FormatMeasurementRecordResult(e.Measurement, e.TestPoint);
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
            // 导通模式下超量程已经按 OPEN 判定，显示也必须与业务判定一致。
            if (!string.IsNullOrWhiteSpace(testPoint.ActualContinuityState))
                return testPoint.ActualContinuityState;

            return InspectionMeasurementEvaluator.ResolveContinuityState(value, testPoint.ContinuityThresholdOhm);
        }

        // 电阻模式有限超量程显示万用表原始返回值；CSV 使用独立 RecordResult，不受界面显示影响。
        if (measurement.ValueKind == MeasurementValueKind.PositiveInfinityOrOverRange)
        {
            if (double.IsPositiveInfinity(measurement.Value))
                return "+Infinity";

            string rawValue = measurement.RawValue?.Trim() ?? string.Empty;

            return !string.IsNullOrWhiteSpace(rawValue)
                ? $"{rawValue} Ω"
                : $"{measurement.Value.ToString(
                    "0.00000000E+00",
                    System.Globalization.CultureInfo.InvariantCulture)} Ω";
        }

        // NaN、负无穷等异常继续保留原有异常文本。
        if (!string.IsNullOrWhiteSpace(measurement.DisplayTextOverride))
            return measurement.DisplayTextOverride;

        return $"{InputValidationHelper.FormatResistanceValue(value)} Ω";
    }

    /// <summary>
    /// 格式化正式 CSV 的检测项目值，不携带界面单位或通信原始空白。
    /// </summary>
    private static string FormatMeasurementRecordResult(MeasurementResult measurement, TestPointConfig testPoint)
    {
        if (!measurement.IsValid)
            return measurement.DisplayTextOverride;

        if (testPoint.CheckMode == CheckModeConstants.Continuity)
        {
            if (!string.IsNullOrWhiteSpace(testPoint.ActualContinuityState))
                return testPoint.ActualContinuityState;

            return InspectionMeasurementEvaluator.ResolveContinuityState(
                measurement.Value,
                testPoint.ContinuityThresholdOhm);
        }

        if (measurement.ValueKind == MeasurementValueKind.PositiveInfinityOrOverRange)
        {
            return double.IsPositiveInfinity(measurement.Value)
                ? "Infinity"
                : measurement.Value.ToString("0.00000000E+00", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(measurement.DisplayTextOverride))
            return measurement.DisplayTextOverride;

        return InputValidationHelper.FormatResistanceValue(measurement.Value);
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
        InvalidateCurrentSerialVerification();
        _lastReferenceMismatchPromptKey = null;
        _lastInvalidBarcodePromptKey = null;

        var hasOperator = _operatorStateService?.HasOperator == true;
        var operatorName = hasOperator ? _operatorStateService.CurrentOperatorName : string.Empty;
        OperatorName = operatorName;
        IsOperatorEditable = false;
        IsInputEnabled = true;
        UiState = TestUIState.Ready;
        _resetRearmPending = false;
        Interlocked.Exchange(ref _resetRearmInProgress, 0);
        _lastResetRearmAttemptUtc = DateTime.MinValue;
        _resetSignalHandled = false;
        _isResetting = false;
        Interlocked.Exchange(ref _resetFailureNotificationShown, 0);

        if (hasOperator)
        {
            AddLog($"当前作业员: {operatorName}");
        }
        else
        {
            AddLog("未选择作业员，请返回主菜单重新进入运行界面并选择作业员");
            _logger.LogWarning("[运行页初始化][审计] 当前作业员为空，运行页不允许启动");
        }

        _referenceSelectionStateService.SelectionChanged -= OnReferenceSelectionChanged;
        _referenceSelectionStateService.SelectionChanged += OnReferenceSelectionChanged;
        await _referenceSelectionStateService.LoadAsync();
        ApplyReferenceSelection();

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
            await CancelFinalResultAutoClearAsync("进入运行页初始化").ConfigureAwait(false);
            await ClearAllRunSignalsAsync(CancellationToken.None);
            _logger.LogInformation("[运行页初始化] 已主动清除所有残留 PLC 信号");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[运行页初始化] 清除残留 PLC 信号异常，如后续误触发请检查 PLC 状态");
        }

        // ★ 启动 PLC 轮询替代传感器模拟
        StartPlcPolling();
        ScheduleDuplicateRecordCheck();
        AddLog("正在等待 PLC 启动信号...");
    }

    public async Task OnNavigatedFromAsync()
    {
        _logger.LogInformation("离开运行界面");
        CancelProductIdentityNotification();
        _lastNotifiedProductIdentityKey = null;
        InvalidateCurrentSerialVerification();
        _lastReferenceMismatchPromptKey = null;
        _lastInvalidBarcodePromptKey = null;
        await CancelFinalResultAutoClearAsync("离开运行页").ConfigureAwait(false);
        StopPlcPolling();
        if (Volatile.Read(ref _plcPollingSuspendCount) != 0)
            _logger.LogError("[PLC轮询][生命周期] 离开运行页时暂停计数为 {SuspendCount}，已重置", _plcPollingSuspendCount);
        Interlocked.Exchange(ref _plcPollingSuspendCount, 0);
        _resetRearmPending = false;
        Interlocked.Exchange(ref _resetRearmInProgress, 0);
        _lastResetRearmAttemptUtc = DateTime.MinValue;
        StopElapsedTimeTimer();
        UnsubscribeFromHardwareEvents();
        _referenceSelectionStateService.SelectionChanged -= OnReferenceSelectionChanged;

        // ★ 终了流程已提前完成 PLC 清理，此处只做非 PLC 的页面离开收尾
        if (_isFinishing)
        {
            _logger.LogInformation("[运行页收尾] 终了流程已清理 PLC，跳过重复清理");
            return;
        }

        // 正常离开运行页（非终了场景）：清运行信号
        await ClearAllRunSignalsAsync(CancellationToken.None).ConfigureAwait(false);

        // 离开页面时不允许 DMM 收尾阻塞 PLC 清理或导航。
        await ReleaseDmmToLocalBestEffortAsync("离开运行界面").ConfigureAwait(false);
    }

    /// <summary>
    /// 尽力把万用表切回本地控制；设备离线或命令锁繁忙时只记录告警，不阻塞控制流程。
    /// </summary>
    private async Task ReleaseDmmToLocalBestEffortAsync(string source)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await _multimeterDevice.ReleaseToLocalAsync(timeoutCts.Token).ConfigureAwait(false);
            _logger.LogInformation("[万用表收尾] {Source}：已请求退出远程控制", source);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("[万用表收尾] {Source}：ReleaseToLocal 超时，已忽略并继续控制流程", source);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[万用表收尾] {Source}：ReleaseToLocal 失败，已忽略并继续控制流程", source);
        }
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
    
    /// <summary>
    /// 判断异步方案加载结果是否仍属于当前机种和方案。
    /// 旧任务只允许自行结束，不能覆盖后来输入的机种或方案。
    /// </summary>
    private bool IsCurrentPlanLoad(
        int? expectedLoadVersion,
        string? expectedMachineType,
        string? expectedSchemeName)
    {
        return (!expectedLoadVersion.HasValue || expectedLoadVersion.Value == _planLoadVersion)
               && (expectedMachineType == null
                   || string.Equals(ModelName, expectedMachineType, StringComparison.OrdinalIgnoreCase))
               && (expectedSchemeName == null
                   || string.Equals(SchemeName, expectedSchemeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>仅由当前加载任务结束时清除加载标志，并立即刷新待机或可启动状态。</summary>
    private void CompletePlanLoading(int? expectedLoadVersion)
    {
        if (expectedLoadVersion.HasValue && expectedLoadVersion.Value != _planLoadVersion)
            return;

        _isPlanLoading = false;
        RefreshReadyOrCanStartState();
    }

    private async Task<bool> RefreshPlanNameOptionsAsync(string machineType, int? expectedLoadVersion = null)
    {
        if (string.IsNullOrWhiteSpace(machineType))
        {
            if (!IsCurrentPlanLoad(expectedLoadVersion, machineType, null))
                return false;

            PlanNameOptions.Clear();
            _logger.LogDebug("机种为空，方案下拉选项已清空");
            CompletePlanLoading(expectedLoadVersion);
            return true;
        }

        try
        {
            var planNames = await _planStorageService.GetPlanNamesByMachineTypeAsync(machineType);

            if (!IsCurrentPlanLoad(expectedLoadVersion, machineType, null))
                return false;

            PlanNameOptions.Clear();

            foreach (var name in planNames)
            {
                PlanNameOptions.Add(name);
            }

            _logger.LogDebug("方案下拉选项已刷新，机种={MachineType}，共 {Count} 个方案",
                machineType, PlanNameOptions.Count);
            CompletePlanLoading(expectedLoadVersion);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载机种 {MachineType} 的方案列表失败", machineType);
            if (IsCurrentPlanLoad(expectedLoadVersion, machineType, null))
            {
                PlanNameOptions.Clear();
                CompletePlanLoading(expectedLoadVersion);
            }
            return false;
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
        PlcConnectionStatus = _deviceManager.PlcStatus;
        IsDmmConnected = _deviceManager.IsDmmConnected;
        DmmStatusText = _deviceManager.DmmStatusText;
        DmmConnectionStatus = _deviceManager.DmmStatus;
        IsScannerConnected = _deviceManager.IsScannerConnected;
        ScannerStatusText = _deviceManager.ScannerStatusText;
        ScannerConnectionStatus = _deviceManager.ScannerStatus;
        UpdateUIState();
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _clockTimer?.Stop();
        _clockTimer = null;
        if (_elapsedTimeTimer != null)
        {
            _elapsedTimeTimer.Stop();
            _elapsedTimeTimer.Tick -= OnElapsedTimeTimerTick;
            _elapsedTimeTimer = null;
        }
        _displayInspectionStopwatch.Stop();
        _referenceSelectionStateService.SelectionChanged -= OnReferenceSelectionChanged;
        CancelProductIdentityNotification();
        _lastNotifiedProductIdentityKey = null;
        InvalidateCurrentSerialVerification();
        try
        {
            _finalResultAutoClearCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 页面销毁与延时任务收尾并发时，CTS 已由收口流程释放即可忽略。
        }
        _ = CancelFinalResultAutoClearAsync("Dispose");
        StopPlcPolling();
        if (Volatile.Read(ref _plcPollingSuspendCount) != 0)
            _logger.LogError("[PLC轮询][生命周期] Dispose 时暂停计数为 {SuspendCount}，已重置", _plcPollingSuspendCount);
        Interlocked.Exchange(ref _plcPollingSuspendCount, 0);
        _resetRearmPending = false;
        Interlocked.Exchange(ref _resetRearmInProgress, 0);
        _lastResetRearmAttemptUtc = DateTime.MinValue;
        UnsubscribeFromHardwareEvents();
        _controlActionLock.Dispose();

        GC.SuppressFinalize(this);
    }

    #endregion
}
