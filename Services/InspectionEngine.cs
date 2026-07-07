using System.Diagnostics;
using System.Globalization;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services;

/// <summary>
/// 万用表测量值分类，决定后续判定和中止策略。
/// </summary>
public enum MeasurementValueKind
{
    /// <summary>正常非负有限数值</summary>
    Normal,
    /// <summary>+Infinity 或超量程大数（>ResistanceMaxValue），判定为 NG 但不中止</summary>
    PositiveInfinityOrOverRange,
    /// <summary>NaN，判定为 NG 且中止</summary>
    NaN,
    /// <summary>-Infinity，判定为 NG 且中止</summary>
    NegativeInfinity,
    /// <summary>负电阻值，判定为 NG 且中止</summary>
    NegativeResistance,
    /// <summary>无法解析，中止</summary>
    ParseFailed
}

/// <summary>
/// GM/E78 USB 焊接不良检查流程引擎。
/// 负责最小闭环：写 PLC 每脚独立选择区 DT130~DT185、等待 DT302、读取万用表、判定、维护内存断点。
/// </summary>
public partial class InspectionEngine : IAsyncDisposable, IDisposable
{
    private const double TemporaryOpenThresholdOhm = 1_000_000.0;

    private readonly ILogger<InspectionEngine> _logger;
    private readonly IPlcDevice _plcDevice;
    private readonly IMultimeterDevice _multimeterDevice;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly InspectionCheckpoint _checkpoint = new();

    private CancellationTokenSource? _inspectionCts;
    private volatile bool _isRunning;
    private volatile bool _isDisposed;
    private string? _currentInspectionId;
    private InspectionConfig _config = new();

    public event EventHandler<InspectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<StepStartedEventArgs>? StepStarted;
    public event EventHandler<StepCompletedEventArgs>? StepCompleted;
    public event EventHandler<InspectionCompletedEventArgs>? InspectionCompleted;
    public event EventHandler<string>? LogMessage;

    /// <summary>原子中断原因，在 Cancel 之前设置，供 OperationCanceledException 分支读取</summary>
    private volatile InspectionStopReason _abortReason = InspectionStopReason.None;
    public bool IsRunning => _isRunning;
    public InspectionState CurrentState { get; private set; } = InspectionState.Idle;
    public InspectionConfig Config => _config;
    public int InterruptedPointIndex { get; private set; } = -1;

    public InspectionEngine(
        ILogger<InspectionEngine> logger,
        IPlcDevice plcDevice,
        IMultimeterDevice multimeterDevice)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _multimeterDevice = multimeterDevice ?? throw new ArgumentNullException(nameof(multimeterDevice));
    }

    public void SetConfig(InspectionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        LogInfo($"检测配置已更新：测试点数={config.TestPoints.Count}");
    }

    public async Task<InspectionResult> RunInspectionAsync(
        string barcode,
        string modelName,
        string operatorName,
        int startFromPointIndex = 0,
        CancellationToken ct = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("检测引擎正在运行中");

        await _engineLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _isRunning = true;
            _inspectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            string inspectionId = CreateInspectionId();
            _currentInspectionId = inspectionId;

            var result = new InspectionResult
            {
                InspectionId = inspectionId,
                Barcode = barcode,
                ModelName = modelName,
                OperatorName = operatorName,
                StartTime = DateTime.Now
            };

            int effectiveStartIndex = PrepareCheckpoint(modelName, barcode, operatorName, startFromPointIndex);
            var totalSw = Stopwatch.StartNew();

            try
            {
                SetState(InspectionState.Testing);
                LogInfo($"开始检测 - INS:{inspectionId}, 序列号:{barcode}, 机种:{modelName}, 测试点数:{_config.TestPoints.Count}");

                await InitializeInspectionAsync(_inspectionCts.Token).ConfigureAwait(false);

                // ── 本轮检测开始时清上一轮残留 ──
                // 清 DT130~DT185、DT302，确保引脚输出区和继电器完成标志为初始状态
                await ClearPlcOutputsAndRelayFlagAsync(_inspectionCts.Token).ConfigureAwait(false);

                int passCount = _checkpoint.FinishedResults.Count(r => r.Result == "OK");
                int failCount = _checkpoint.FinishedResults.Count(r => r.Result == "NG");
                int firstNgIndex = _checkpoint.FinishedResults.FindIndex(r => r.Result == "NG");

                for (int i = effectiveStartIndex; i < _config.TestPoints.Count; i++)
                {
                    _inspectionCts.Token.ThrowIfCancellationRequested();
                    UpdateCheckpoint(i, "CheckInterrupts");

                    var interruptResult = await CheckPlcInterruptsAsync(_inspectionCts.Token).ConfigureAwait(false);
                    if (interruptResult != PlcInterruptAction.Continue)
                    {
                        bool canContinue = await HandlePlcInterruptAsync(interruptResult, i, _inspectionCts.Token).ConfigureAwait(false);
                        if (!canContinue)
                        {
                            result.IsAborted = true;
                            result.ErrorMessage = _checkpoint.LastErrorMessage ?? "检测被 PLC 信号中断";
                            result.EndTime = DateTime.Now;
                            // ★ 从 interruptResult 推断停止原因
                            result.StopReason = interruptResult switch
                            {
                                PlcInterruptAction.Stop => InspectionStopReason.PlcStop,
                                PlcInterruptAction.Reset => InspectionStopReason.Reset,
                                PlcInterruptAction.EmergencyStop => InspectionStopReason.EmergencyStop,
                                _ => InspectionStopReason.None
                            };
                            InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
                            return result;
                        }
                    }

                    TestPointConfig testPoint = _config.TestPoints[i];
                    testPoint.ContinuityThresholdOhm = _config.ContinuityThresholdOhm;
                    testPoint.ActualContinuityState = string.Empty;
                    StepStarted?.Invoke(this, new StepStartedEventArgs(i, testPoint));
                    LogInfo($"正在检测 [{i + 1}/{_config.TestPoints.Count}] {testPoint.Name} ({testPoint.CheckMode})");

                    UpdateCheckpoint(i, "WritePinsToPlc");
                    var writeResult = await _plcDevice.WriteCurrentTestPointAsync(
                        testPoint.PinLeft,
                        testPoint.PinRight,
                        testPoint.PinLeftPolarityCode,
                        testPoint.PinRightPolarityCode,
                        _inspectionCts.Token).ConfigureAwait(false);

                    if (!writeResult.IsSuccess)
                    {
                        MarkNg(testPoint, writeResult.Message);
                        failCount++;
                        if (firstNgIndex < 0) firstNgIndex = i;
                        StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint, FailedMeasurement(writeResult.Message)));
                        continue;
                    }

                    _inspectionCts.Token.ThrowIfCancellationRequested();

                    UpdateCheckpoint(i, "WaitDt302");
                    if (_config.SkipDt302Wait)
                    {
                        _logger.LogWarning(
                            "[检测流程][审计][{INS}] 半实物临时旁路 DT302：点位 {Name} 已跳过继电器动作完成等待。正式整机联调必须关闭 Hardware:SkipDt302Wait。",
                            inspectionId, testPoint.Name);
                        LogInfo($"半实物调试：点位 {testPoint.Name} 已跳过 DT302 等待");
                    }
                    else
                    {
                        var relaySw = Stopwatch.StartNew();
                        var relayResult = await _plcDevice.WaitRelaySwitchCompletedAsync(
                            TimeSpan.FromMilliseconds(_config.RelaySwitchTimeoutMs),
                            _inspectionCts.Token).ConfigureAwait(false);
                        relaySw.Stop();
                        LogBeat(inspectionId, $"点位 {testPoint.Name} 等待 DT302", relaySw.ElapsedMilliseconds);

                        if (!relayResult.IsSuccess)
                        {
                            result.StopReason = InspectionStopReason.RelayTimeout;
                            result.ErrorMessage = $"等待 DT302 = 1 超时（点位 {testPoint.Name}）";
                            await AbortCurrentRunAsync(result, result.ErrorMessage, InspectionState.Aborted).ConfigureAwait(false);
                            InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
                            return result;
                        }
                    }

                    _inspectionCts.Token.ThrowIfCancellationRequested();

                    // 等待继电器稳定后、读取万用表前，再检查一次中断信号
                    var postRelayInterrupt = await CheckPlcInterruptsAsync(_inspectionCts.Token).ConfigureAwait(false);
                    if (postRelayInterrupt != PlcInterruptAction.Continue)
                    {
                        bool canContinue = await HandlePlcInterruptAsync(postRelayInterrupt, i, _inspectionCts.Token).ConfigureAwait(false);
                        if (!canContinue)
                        {
                            result.IsAborted = true;
                            result.ErrorMessage = _checkpoint.LastErrorMessage ?? "检测被 PLC 信号中断";
                            result.EndTime = DateTime.Now;
                            result.StopReason = postRelayInterrupt switch
                            {
                                PlcInterruptAction.Stop => InspectionStopReason.PlcStop,
                                PlcInterruptAction.Reset => InspectionStopReason.Reset,
                                PlcInterruptAction.EmergencyStop => InspectionStopReason.EmergencyStop,
                                _ => InspectionStopReason.None
                            };
                            InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
                            return result;
                        }
                    }

                    await Task.Delay(_config.RelaySettleTimeMs, _inspectionCts.Token).ConfigureAwait(false);

                    // ── 按检查方式切换万用表模式 ──
                    UpdateCheckpoint(i, "SetupMultimeterMode");
                    bool modeReady;
                    if (testPoint.CheckMode == CheckModeConstants.Continuity)
                    {
                        modeReady = await _multimeterDevice.InitializeContinuityModeAsync(
                            _config.ContinuityThresholdOhm, _inspectionCts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        modeReady = await _multimeterDevice.InitializeResistanceModeAsync(
                            _inspectionCts.Token).ConfigureAwait(false);
                    }
                    if (!modeReady)
                    {
                        await AbortCurrentRunAsync(result, $"万用表模式切换失败：{testPoint.CheckMode}", InspectionState.Aborted).ConfigureAwait(false);
                        return result;
                    }
                    // ★ 万用表模式切换后等待硬件稳定（100~300ms），避免读值抖动
                    await Task.Delay(200, _inspectionCts.Token).ConfigureAwait(false);

                    _inspectionCts.Token.ThrowIfCancellationRequested();

                    UpdateCheckpoint(i, "ReadMultimeter");
                    string rawText;
                    if (testPoint.CheckMode == CheckModeConstants.Continuity)
                    {
                        rawText = await _multimeterDevice.ReadContinuityRawAsync(_inspectionCts.Token).ConfigureAwait(false);
                        _logger.LogWarning("[检测流程][审计][{INS}] GDM-9060 MEAS:CONT? RawText={RawText}", inspectionId, rawText);
                    }
                    else
                    {
                        rawText = await _multimeterDevice.ReadResistanceRawAsync(_inspectionCts.Token).ConfigureAwait(false);
                        _logger.LogWarning("[检测流程][审计][{INS}] GDM-9060 READ? RawText={RawText}", inspectionId, rawText);
                    }

                    _inspectionCts.Token.ThrowIfCancellationRequested();

                    MeasurementResult measurement = ParseMeasurementForInspection(rawText);

                    // ── 无效值中止分支（NaN / -Infinity / 负电阻值 / 解析失败）──
                    // 当前项标记为 NG，写 PLC、回调 StepCompleted，再中止整轮检测
                    if (measurement.ShouldAbortInspection)
                    {
                        testPoint.ActualValue = measurement.Value;
                        testPoint.Judgment = "NG";
                        testPoint.IsTested = true;
                        failCount++;
                        if (firstNgIndex < 0) firstNgIndex = i;

                        _logger.LogWarning("[检测流程][审计][INS-{INS}] 万用表返回异常值 {Kind}：点位 {Name} 值={RawText}，判定 NG，检测中止",
                            inspectionId, measurement.ValueKind, testPoint.Name, measurement.RawValue);

                        _inspectionCts.Token.ThrowIfCancellationRequested();
                        await _plcDevice.WritePointResultAsync(i, false, _inspectionCts.Token).ConfigureAwait(false);
                        _inspectionCts.Token.ThrowIfCancellationRequested();
                        StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint, measurement));
                        AddFinishedResult(testPoint);

                        await AbortCurrentRunAsync(result, $"万用表返回{measurement.ValueKind}：点位 {testPoint.Name} 判定 NG，检测中止", InspectionState.Aborted).ConfigureAwait(false);
                        InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
                        return result;
                    }

                    UpdateCheckpoint(i, "JudgeResult");
                    string judgment = JudgeResult(measurement, testPoint);
                    testPoint.ActualValue = measurement.Value;
                    testPoint.Judgment = judgment;
                    testPoint.IsTested = true;

                    if (judgment == "OK")
                    {
                        passCount++;
                    }
                    else
                    {
                        failCount++;
                        if (firstNgIndex < 0) firstNgIndex = i;
                    }

                    _logger.LogWarning("[检测流程][审计][{INS}] 判定 {Name}: Raw={RawText}, Result={Judgment}",
                        inspectionId, testPoint.Name, measurement.RawValue, judgment);

                    _inspectionCts.Token.ThrowIfCancellationRequested();
                    await _plcDevice.WritePointResultAsync(i, judgment == "OK", _inspectionCts.Token).ConfigureAwait(false);
                    _inspectionCts.Token.ThrowIfCancellationRequested();
                    StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint, measurement));
                    AddFinishedResult(testPoint);

                    // ── 单项 NG 后按系统设置选择继续或停止 ──
                    if (judgment == "NG" && !_config.ContinueTestingAfterNg)
                    {
                        result.IsAborted = false;
                        result.IsAllPassed = false;
                        result.ErrorMessage = $"第 {i + 1} 项 {testPoint.Name} 判定 NG，系统设置为停止检测，已停止本轮，等待复位或终了";
                        result.StopReason = InspectionStopReason.SingleItemNg;
                        result.StopPointIndex = i;
                        result.StopPointName = testPoint.Name;
                        result.TotalCount = _config.TestPoints.Count;
                        result.PassCount = passCount;
                        result.FailCount = failCount;
                        result.EndTime = DateTime.Now;

                        await ClearPlcOutputsAndRelayFlagAsync(_inspectionCts.Token).ConfigureAwait(false);
                        await _plcDevice.ClearPcReadyAsync(CancellationToken.None).ConfigureAwait(false);
                        // 单项 NG 不写 DT304/DT305，等待复位或终了

                        SetState(InspectionState.StoppedBySingleItemNg);
                        InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
                        return result;
                    }

                    await ClearPlcOutputsAndRelayFlagAsync(_inspectionCts.Token).ConfigureAwait(false);
                    _inspectionCts.Token.ThrowIfCancellationRequested();
                    _checkpoint.CurrentItemIndex = i + 1;
                    _checkpoint.LastUpdatedTime = DateTime.Now;
                }

                result.TotalCount = _config.TestPoints.Count;
                result.PassCount = passCount;
                result.FailCount = failCount;
                result.IsAllPassed = failCount == 0;
                result.EndTime = DateTime.Now;

                _checkpoint.HasBreakpoint = false;
                _checkpoint.CurrentStep = "Completed";
                SetState(result.IsAllPassed ? InspectionState.CompletedPass : InspectionState.CompletedFail);

                await _plcDevice.WriteFinalResultAsync(
                    result.IsAllPassed,
                    firstNgIndex >= 0 ? firstNgIndex : null,
                    _inspectionCts.Token).ConfigureAwait(false);

                // 正常完成时只写 DT304/DT305 最终结果。
                // DT120/DT234 的正常完成收口必须等用户处理保存弹窗后由 TestPageViewModel 执行。
                totalSw.Stop();
                LogBeat(inspectionId, "单件总耗时", totalSw.ElapsedMilliseconds);
                _inspectionCts.Token.ThrowIfCancellationRequested();
                InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
                return result;
            }
            catch (OperationCanceledException)
            {
                await ClearPlcOutputsAndRelayFlagSafelyAsync().ConfigureAwait(false);
                await _plcDevice.ClearPcReadyAsync(CancellationToken.None).ConfigureAwait(false);
                SetState(InspectionState.Aborted);
                result.IsAborted = true;
                result.ErrorMessage = "检测被取消";
                result.EndTime = DateTime.Now;
                // ★ 从当前引擎状态推断停止原因（HandlePlcInterruptAsync 已先 SetState）
                // ★ 使用原子中断原因字段，避免竞态
                if (_abortReason != InspectionStopReason.None)
                {
                    result.StopReason = _abortReason;
                }
                else
                {
                    result.StopReason = CurrentState switch
                    {
                        InspectionState.PausedByStop => InspectionStopReason.PlcStop,
                        InspectionState.ResetRequested => InspectionStopReason.Reset,
                        InspectionState.PausedByEmergencyStop => InspectionStopReason.EmergencyStop,
                        _ => InspectionStopReason.Canceled
                    };
                }
                InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[检测流程][{INS}] 检测流程异常", inspectionId);
                await AbortCurrentRunAsync(result, ex.Message, InspectionState.Error).ConfigureAwait(false);
                InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
                return result;
            }
        }
        finally
        {
            _isRunning = false;
            _currentInspectionId = null;
            _abortReason = InspectionStopReason.None; // ★ 重置中断原因
            _engineLock.Release();
        }
    }

    private int PrepareCheckpoint(string machineType, string serialNumber, string operatorName, int requestedStartIndex)
    {
        string planName = _config.PlanName;
        bool sameRun = _checkpoint.HasBreakpoint
                       && string.Equals(_checkpoint.MachineType, machineType, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(_checkpoint.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(_checkpoint.PlanName, planName, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(_checkpoint.OperatorName, operatorName, StringComparison.OrdinalIgnoreCase);

        if (sameRun)
        {
            LogInfo($"从内存断点续作：第 {_checkpoint.CurrentItemIndex + 1} 项");
            return Math.Clamp(_checkpoint.CurrentItemIndex, 0, _config.TestPoints.Count);
        }

        _checkpoint.ResetProgress();
        _checkpoint.MachineType = machineType;
        _checkpoint.SerialNumber = serialNumber;
        _checkpoint.PlanName = planName;
        _checkpoint.OperatorName = operatorName;
        _checkpoint.CurrentItemIndex = Math.Clamp(requestedStartIndex, 0, _config.TestPoints.Count);
        _checkpoint.LastUpdatedTime = DateTime.Now;
        return _checkpoint.CurrentItemIndex;
    }

    private void UpdateCheckpoint(int itemIndex, string step)
    {
        _checkpoint.CurrentItemIndex = itemIndex;
        _checkpoint.CurrentStep = step;
        _checkpoint.LastUpdatedTime = DateTime.Now;
    }

    private void AddFinishedResult(TestPointConfig testPoint)
    {
        _checkpoint.FinishedResults.Add(new PinResult
        {
            PinName = testPoint.Name,
            Result = testPoint.Judgment
        });
    }

    /// <summary>
    /// 检测初始化——验证万用表通信正常。
    /// 实际模式切换在每项测试前根据 CheckMode 单独进行，不在全局固定为电阻模式。
    /// </summary>
    private async Task InitializeInspectionAsync(CancellationToken ct)
    {
        // 先以电阻模式做一次连通性验证
        bool connected = await _multimeterDevice.InitializeResistanceModeAsync(ct).ConfigureAwait(false);
        if (!connected)
            throw new InvalidOperationException("万用表通信验证失败");

        LogInfo("检测初始化完成：万用表通信正常");
    }

    private enum PlcInterruptAction
    {
        Continue,
        Stop,
        Reset,
        EmergencyStop
    }

    private async Task<PlcInterruptAction> CheckPlcInterruptsAsync(CancellationToken ct)
    {
        var result = await _plcDevice.ReadMachineInputsAsync(ct).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value == null)
            return PlcInterruptAction.Continue;

        PlcMachineInputs inputs = result.Value;
        if (inputs.IsEmergencyStop) return PlcInterruptAction.EmergencyStop;
        if (inputs.IsResetRequested) return PlcInterruptAction.Reset;
        if (inputs.IsStopRequested) return PlcInterruptAction.Stop;
        return PlcInterruptAction.Continue;
    }

    private async Task<bool> HandlePlcInterruptAsync(PlcInterruptAction action, int currentPointIndex, CancellationToken ct)
    {
        switch (action)
        {
            case PlcInterruptAction.Stop:
                _abortReason = InspectionStopReason.PlcStop;
                SetState(InspectionState.PausedByStop);
                _checkpoint.HasBreakpoint = false;
                _checkpoint.LastErrorMessage = "停止触发，检测中止";
                await ClearPlcOutputsAndRelayFlagAsync(ct).ConfigureAwait(false);
                LogInfo($"PLC 停止信号 DT122=1，检测中止（不保留断点）");
                return false;

            case PlcInterruptAction.Reset:
                _abortReason = InspectionStopReason.Reset;
                SetState(InspectionState.ResetRequested);
                _checkpoint.ResetProgress();
                await ClearPlcOutputsAndRelayFlagAsync(ct).ConfigureAwait(false);
                // DT121 由 TestPageViewModel 在界面结果、内部状态和 PLC 输出全部清理完成后统一清零。
                // 引擎只负责中止当前检测，避免先清 DT121 导致运行界面轮询错过复位信号。
                _checkpoint.LastErrorMessage = "复位触发，检测中止";
                return false;

            case PlcInterruptAction.EmergencyStop:
                _abortReason = InspectionStopReason.EmergencyStop;
                SetState(InspectionState.PausedByEmergencyStop);
                _checkpoint.HasBreakpoint = false;
                _checkpoint.LastErrorMessage = "急停触发，必须复位后重新启动";
                await ClearPlcOutputsAndRelayFlagAsync(ct).ConfigureAwait(false);
                return false;

            default:
                return true;
        }
    }

    private static MeasurementResult ParseMeasurement(string rawText)
    {
        string text = rawText.Trim();
        if (string.Equals(text, "OPEN", StringComparison.OrdinalIgnoreCase))
            return new MeasurementResult { RawValue = rawText, Value = TemporaryOpenThresholdOhm, IsValid = true };

        if (string.Equals(text, "SHORT", StringComparison.OrdinalIgnoreCase))
            return new MeasurementResult { RawValue = rawText, Value = 0, IsValid = true };

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return new MeasurementResult { RawValue = rawText, Value = value, IsValid = true };

        return FailedMeasurement($"无法解析万用表返回值：{rawText}", rawText);
    }

    /// <summary>
    /// 万用表测量值解析与分类。public static 便于最小测试直接覆盖。
    /// 按无效测量值方案分类，填充 ValueKind / ShouldAbortInspection / DisplayTextOverride。
    /// 
    /// 【界面显示规则】
    ///   DisplayTextOverride 非空 → 界面"检查结果"列直接显示该文本
    ///   DisplayTextOverride 为空 → 由 FormatMeasurementResult() 按导通/电阻模式格式化
    ///
    /// 【各异常值界面显示对照】
    ///   NaN           → "NaN"
    ///   -Infinity     → "-Infinity"
    ///   +Infinity     → "+Infinity"
    ///   负电阻值       → 走数值格式化，显示 "X.XXXX Ω"
    ///   超量程         → "超量程"
    ///   无法解析       → 显示万用表原始返回文本
    /// </summary>
    public static MeasurementResult ParseMeasurementForInspection(string rawText)
    {
        string text = rawText.Trim();

        // OPEN / SHORT 保留现有兼容，由 JudgeResult() 设置 ActualContinuityState，
        // FormatMeasurementResult() 根据导通模式显示 "OPEN" 或 "SHORT"
        if (string.Equals(text, "OPEN", StringComparison.OrdinalIgnoreCase))
            return new MeasurementResult { RawValue = rawText, Value = TemporaryOpenThresholdOhm, IsValid = true, ValueKind = MeasurementValueKind.Normal };

        if (string.Equals(text, "SHORT", StringComparison.OrdinalIgnoreCase))
            return new MeasurementResult { RawValue = rawText, Value = 0, IsValid = true, ValueKind = MeasurementValueKind.Normal };

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            // 无法解析 → 中止，界面显示万用表原始返回文本（如 "ERROR"）
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = 0,
                IsValid = false,
                ValueKind = MeasurementValueKind.ParseFailed,
                ShouldAbortInspection = true,
                ErrorMessage = $"无法解析万用表返回值：{rawText}",
                DisplayTextOverride = rawText
            };
        }

        // ── 以下按 double 值分类 ──

        // NaN → 中止，界面显示 "NaN"
        if (double.IsNaN(value))
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.NaN,
                ShouldAbortInspection = true,
                DisplayTextOverride = "NaN"
            };

        // -Infinity → 中止，界面显示 "-Infinity"
        if (double.IsNegativeInfinity(value))
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.NegativeInfinity,
                ShouldAbortInspection = true,
                DisplayTextOverride = "-Infinity"
            };

        // +Infinity → 不中止、判 NG，界面显示 "+Infinity"
        if (double.IsPositiveInfinity(value))
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.PositiveInfinityOrOverRange,
                ShouldAbortInspection = false,
                DisplayTextOverride = "+Infinity"
            };

        // 负电阻值 → 中止（电阻测量不应为负），界面走数值格式化显示 "X.XXXX Ω"
        if (value < 0)
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.NegativeResistance,
                ShouldAbortInspection = true,
                DisplayTextOverride = null
            };

        // 超量程（>ResistanceMaxValue，即 119,999,900Ω）→ 不中止、判 NG，界面显示"超量程"
        if (value > InputValidationHelper.ResistanceMaxValue)
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.PositiveInfinityOrOverRange,
                ShouldAbortInspection = false,
                DisplayTextOverride = "超量程"
            };

        // 正常非负有限数，DisplayTextOverride 为 null，由 FormatMeasurementResult() 处理
        return new MeasurementResult
        {
            RawValue = rawText,
            Value = value,
            IsValid = true,
            ValueKind = MeasurementValueKind.Normal
        };
    }


    public static string ResolveContinuityState(double resistanceOhm, double thresholdOhm)
    {
        if (thresholdOhm < 1.0 || thresholdOhm > 1000.0)
            throw new ArgumentOutOfRangeException(nameof(thresholdOhm), "导通阈值必须在 1~1000Ω 范围内。");

        return resistanceOhm < thresholdOhm ? "SHORT" : "OPEN";
    }

    public static string JudgeContinuityResult(double resistanceOhm, string? expectedState, double thresholdOhm)
    {
        string actualState = ResolveContinuityState(resistanceOhm, thresholdOhm);
        string expected = NormalizeContinuityState(expectedState);
        return string.Equals(actualState, expected, StringComparison.OrdinalIgnoreCase) ? "OK" : "NG";
    }

    private static string NormalizeContinuityState(string? state)
        => string.Equals(state, "SHORT", StringComparison.OrdinalIgnoreCase) ? "SHORT" : "OPEN";

    private static string JudgeResult(MeasurementResult measurement, TestPointConfig testPoint)
    {
        // 超量程大数 / +Infinity 直接判 NG，不进入阈值比较
        if (measurement.ValueKind == MeasurementValueKind.PositiveInfinityOrOverRange)
            return "NG";

        string raw = measurement.RawValue.Trim();

        if (testPoint.CheckMode == CheckModeConstants.Resistance)
        {
            if (raw.Equals("OPEN", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("SHORT", StringComparison.OrdinalIgnoreCase))
                return "NG";

            double lower = testPoint.LowerLimit ?? 0;
            double upper = testPoint.UpperLimit ?? double.MaxValue;
            return measurement.Value >= lower && measurement.Value <= upper ? "OK" : "NG";
        }

        bool expectShort = string.Equals(testPoint.ModeValue, "SHORT", StringComparison.OrdinalIgnoreCase);
        if (raw.Equals("SHORT", StringComparison.OrdinalIgnoreCase))
        {
            testPoint.ActualContinuityState = "SHORT";
            return expectShort ? "OK" : "NG";
        }
        if (raw.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
        {
            testPoint.ActualContinuityState = "OPEN";
            return expectShort ? "NG" : "OK";
        }

        testPoint.ActualContinuityState = ResolveContinuityState(
            measurement.Value, testPoint.ContinuityThresholdOhm);
        return JudgeContinuityResult(
            measurement.Value, testPoint.ModeValue, testPoint.ContinuityThresholdOhm);
    }

    private static void MarkNg(TestPointConfig testPoint, string message)
    {
        testPoint.Judgment = "NG";
        testPoint.ActualValue = 0;
        testPoint.IsTested = true;
        testPoint.ErrorMessage = message;
    }

    private static MeasurementResult FailedMeasurement(string message, string rawValue = "")
    {
        return new MeasurementResult
        {
            RawValue = rawValue,
            IsValid = false,
            ErrorMessage = message,
            Timestamp = DateTime.Now
        };
    }

    private async Task AbortCurrentRunAsync(InspectionResult result, string message, InspectionState state)
    {
        _checkpoint.CurrentStep = "Faulted";
        _checkpoint.LastErrorMessage = message;
        _checkpoint.HasBreakpoint = false;
        _checkpoint.LastUpdatedTime = DateTime.Now;
        result.IsAborted = true;
        result.ErrorMessage = message;
        result.EndTime = DateTime.Now;
        await ClearPlcOutputsAndRelayFlagSafelyAsync().ConfigureAwait(false);
        await _plcDevice.ClearPcReadyAsync(CancellationToken.None).ConfigureAwait(false);
        await _plcDevice.WritePcErrorAsync(CancellationToken.None).ConfigureAwait(false);
        SetState(state);
    }

    /// <summary>
    /// 统一清理引脚输出区(DT130~DT185)和继电器动作完成标志(DT302)。
    /// 在每项完成后、复位、停止、急停、异常中止时调用。
    /// </summary>
    private async Task ClearPlcOutputsAndRelayFlagAsync(CancellationToken ct)
    {
        await _plcDevice.ClearPinOutputsAsync(ct).ConfigureAwait(false);
        await _plcDevice.ClearRelayActionCompletedAsync(ct).ConfigureAwait(false);
        LogInfo("已清空 DT130~DT185 和 DT302");
    }

    private async Task ClearPlcOutputsAndRelayFlagSafelyAsync()
    {
        try
        {
            await ClearPlcOutputsAndRelayFlagAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[检测流程] 清空 DT130~DT185 和 DT302 失败");
        }
    }

    public void Stop()
    {
        if (_isRunning)
        {
            LogInfo("正在中止检测...");
            _abortReason = InspectionStopReason.PlcStop;
            _inspectionCts?.Cancel();
        }
    }

    /// <summary>
    /// 急停专用中止入口：先标记急停状态，再取消检测任务，避免被归类为普通取消。
    /// </summary>
    public void StopForEmergencyStop()
    {
        _logger.LogWarning("[检测流程][审计] 急停触发，按急停原因中止当前检测");
        SetState(InspectionState.PausedByEmergencyStop);
        _checkpoint.HasBreakpoint = false;
        _checkpoint.LastErrorMessage = "急停触发，必须复位后重新启动";
        _abortReason = InspectionStopReason.EmergencyStop;
        _inspectionCts?.Cancel();
    }

    /// <summary>
    /// 停止检测并等待引擎退出，最大等待 timeout 时长。
    /// 超时后仍返回，不做额外强制中止；调用方继续安全清理 PLC 输出和 UI 状态。
    /// </summary>
    public async Task StopAndWaitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Stop();

        var deadline = DateTime.UtcNow + timeout;
        while (_isRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        if (_isRunning)
        {
            _logger.LogWarning("[检测流程][审计] 已请求停止检测，但等待 {TimeoutMs}ms 后仍未完全退出", timeout.TotalMilliseconds);
        }
    }

    /// <summary>
    /// 暂停检测并保留断点（Fake 调试停止时使用）。
    /// 与 Stop() 的区别：会设置 HasBreakpoint=true 和 Paused 状态，
    /// 重启时可从当前项续作。
    /// 已废弃：DT122 停止不再保留断点，使用 StopAndWaitAsync 替代。
    /// </summary>
    [Obsolete("最新文档要求 DT122 停止不再保留断点，改用 StopAndWaitAsync", false)]
    public void PauseWithCheckpoint()
    {
        if (_isRunning)
        {
            _checkpoint.CurrentStep = "Paused";
            _checkpoint.HasBreakpoint = true;
            _checkpoint.LastUpdatedTime = DateTime.Now;
            LogInfo($"PLC 停止信号 DT122=1，已保留断点：第 {_checkpoint.CurrentItemIndex + 1} 项");
            SetState(InspectionState.PausedByStop);
            _inspectionCts?.Cancel();
        }
    }

    public void ResumeAfterEmergencyStop()
    {
        _logger.LogWarning("[检测流程] 急停已解除，等待用户复位后重新启动");
    }

    public void ClearResetState()
    {
        InterruptedPointIndex = -1;
        _checkpoint.ResetProgress();
    }

    private void SetState(InspectionState newState)
    {
        var oldState = CurrentState;
        CurrentState = newState;
        _logger.LogDebug("[检测流程][{INS}] 状态变更 {OldState} -> {NewState}",
            _currentInspectionId ?? "-", oldState, newState);
        StateChanged?.Invoke(this, new InspectionStateChangedEventArgs(oldState, newState));
    }

    private void LogInfo(string message)
    {
        _logger.LogInformation("[检测流程][{INS}] {Message}", _currentInspectionId ?? "-", message);
        LogMessage?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void LogBeat(string inspectionId, string stage, long elapsedMs)
    {
        _logger.LogInformation("[检测流程][{INS}] {Stage} 耗时 {ElapsedMs}ms", inspectionId, stage, elapsedMs);
    }

    private static string CreateInspectionId()
    {
        return $"INS-{DateTime.Now:yyyyMMddHHmmssfff}";
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _inspectionCts?.Cancel();
        _inspectionCts?.Dispose();
        _engineLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public enum InspectionState
{
    Idle,
    Initializing,
    WaitingForProductInfo,
    WaitingForValidPlan,
    WaitingForDevices,
    WaitingForPlcStart,
    StartingValidation,
    Testing,
    PausedByStop,
    PausedByEmergencyStop,
    /// <summary>单项 NG 后按系统设置停止本轮，等待操作员复位或终了</summary>
    StoppedBySingleItemNg,
    ResetRequested,
    CompletedPendingSave,
    CompletedPass,
    CompletedFail,
    Aborted,
    Error
}

/// <summary>
/// 检测停止原因（区分正常完成、单项 NG 停止、PLC 停止/急停/复位等）。
/// </summary>
public enum InspectionStopReason
{
    /// <summary>正常完成或未设定</summary>
    None,
    /// <summary>单项 NG 后按系统设置停止本轮</summary>
    SingleItemNg,
    /// <summary>PLC 停止信号 DT122</summary>
    PlcStop,
    /// <summary>PLC 复位信号 DT121</summary>
    Reset,
    /// <summary>PLC 急停信号 DT123</summary>
    EmergencyStop,
    /// <summary>DT302 继电器动作完成超时</summary>
    RelayTimeout,
    /// <summary>无法恢复的异常</summary>
    Error,
    /// <summary>操作取消</summary>
    Canceled
}

public class InspectionConfig
{
    public string PlanName { get; set; } = string.Empty;
    public List<TestPointConfig> TestPoints { get; set; } = new();
    public int RelaySettleTimeMs { get; set; } = 150;
    public int RelaySwitchTimeoutMs { get; set; } = 3000;

    /// <summary>导通阈值(Ω)，用于导通模式判定 OPEN/SHORT。默认 10Ω，范围 1~1000Ω。</summary>
    public double ContinuityThresholdOhm { get; set; } = 10.0;

    /// <summary>跳过 DT302 继电器动作完成等待。仅半实物联调无夹具或 DT302 反馈未接通时使用。</summary>
    public bool SkipDt302Wait { get; set; }

    /// <summary>
    /// 单项 NG 后是否继续测试后续项目。
    /// true（默认）：记录该项 NG，继续测完整个方案；
    /// false：首个 NG 后停止本轮，等待操作员复位或终了。
    /// </summary>
    public bool ContinueTestingAfterNg { get; set; } = true;
}

public class TestPointConfig
{
    public string Name { get; set; } = string.Empty;
    public string PinLeft { get; set; } = string.Empty;
    public string PinRight { get; set; } = string.Empty;
    public ushort PinLeftCode { get; set; }
    public ushort PinRightCode { get; set; }
    public string PinLeftPolarity { get; set; } = PinPolarityConstants.Positive;
    public string PinRightPolarity { get; set; } = PinPolarityConstants.Negative;
    public ushort PinLeftPolarityCode { get; set; }
    public ushort PinRightPolarityCode { get; set; } = 1;
    public string CheckMode { get; set; } = CheckModeConstants.Continuity;
    public double? LowerLimit { get; set; }
    public double? UpperLimit { get; set; }
    public string? ModeValue { get; set; } = "OPEN";
    public double ContinuityThresholdOhm { get; set; } = 10.0;
    public string ActualContinuityState { get; set; } = string.Empty;
    public int? RelayChannel { get; set; }
    public double ActualValue { get; set; }
    public string Judgment { get; set; } = string.Empty;
    public bool IsTested { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    public static TestPointConfig FromPlanItem(PlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var (pinLeft, pinRight) = SplitItemName(item.ItemName);
        string leftPolarity = PinPolarityConstants.Normalize(item.PinLeftPolarity, PinPolarityConstants.Positive);
        string rightPolarity = PinPolarityConstants.Normalize(item.PinRightPolarity, PinPolarityConstants.Negative);

        return new TestPointConfig
        {
            Name = item.ItemName,
            PinLeft = pinLeft,
            PinRight = pinRight,
            PinLeftCode = PlcAddressMap.ConvertPinNameToNumber(pinLeft),
            PinRightCode = PlcAddressMap.ConvertPinNameToNumber(pinRight),
            PinLeftPolarity = leftPolarity,
            PinRightPolarity = rightPolarity,
            PinLeftPolarityCode = PlcAddressMap.ConvertPolarityToValue(leftPolarity),
            PinRightPolarityCode = PlcAddressMap.ConvertPolarityToValue(rightPolarity),
            CheckMode = item.CheckMode,
            LowerLimit = item.LowerLimit,
            UpperLimit = item.UpperLimit,
            ModeValue = item.ModeValue
        };
    }

    private static (string Left, string Right) SplitItemName(string itemName)
    {
        var parts = (itemName ?? string.Empty).Split('-', 2);
        return (
            parts.Length > 0 ? parts[0].Trim() : string.Empty,
            parts.Length > 1 ? parts[1].Trim() : string.Empty);
    }
}

public class InspectionResult
{
    public string InspectionId { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string OperatorName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int TotalCount { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool IsAllPassed { get; set; }
    public bool IsAborted { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    /// <summary>停止原因（正常完成、单项 NG 停止、PLC 停止/急停/复位等）</summary>
    public InspectionStopReason StopReason { get; set; } = InspectionStopReason.None;
    /// <summary>停止时的测试项索引（如有）</summary>
    public int? StopPointIndex { get; set; }
    /// <summary>停止时的测试项名称（如有）</summary>
    public string StopPointName { get; set; } = string.Empty;
    public TimeSpan Duration => EndTime - StartTime;
}

public class InspectionStateChangedEventArgs : EventArgs
{
    public InspectionState OldState { get; }
    public InspectionState NewState { get; }

    public InspectionStateChangedEventArgs(InspectionState oldState, InspectionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}

public class StepStartedEventArgs : EventArgs
{
    public int StepIndex { get; }
    public TestPointConfig TestPoint { get; }

    public StepStartedEventArgs(int stepIndex, TestPointConfig testPoint)
    {
        StepIndex = stepIndex;
        TestPoint = testPoint;
    }
}

public class StepCompletedEventArgs : EventArgs
{
    public int StepIndex { get; }
    public TestPointConfig TestPoint { get; }
    public MeasurementResult Measurement { get; }

    public StepCompletedEventArgs(int stepIndex, TestPointConfig testPoint, MeasurementResult measurement)
    {
        StepIndex = stepIndex;
        TestPoint = testPoint;
        Measurement = measurement;
    }
}

public class InspectionCompletedEventArgs : EventArgs
{
    public InspectionResult Result { get; }

    public InspectionCompletedEventArgs(InspectionResult result)
    {
        Result = result;
    }
}
