using System.Diagnostics;
using System.Globalization;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.Inspection;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services;

/// <summary>
/// GM/E78 USB 焊接不良检查流程引擎。
/// 负责最小闭环：写 PLC 每脚独立选择区 DT130~DT185、等待 DT302、读取万用表、判定、维护内存断点。
/// </summary>
public partial class InspectionEngine : IAsyncDisposable, IDisposable
{
    private readonly ILogger<InspectionEngine> _logger;
    private readonly IPlcDevice _plcDevice;
    private readonly IMultimeterDevice _multimeterDevice;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    /// <summary>轻量执行进度（仅日志诊断用，不支持断点续测）</summary>
    private readonly InspectionExecutionProgress _progress = new();

    private CancellationTokenSource? _inspectionCts;
    private volatile bool _isRunning;
    private volatile bool _isDisposed;
    private string? _currentInspectionId;
    private InspectionConfig _config = new();

    public event EventHandler<StepStartedEventArgs>? StepStarted;
    public event EventHandler<StepCompletedEventArgs>? StepCompleted;
    public event EventHandler<string>? LogMessage;

    /// <summary>原子中断原因，在 Cancel 之前设置，供 OperationCanceledException 分支读取</summary>
    private volatile InspectionStopReason _abortReason = InspectionStopReason.None;
    public bool IsRunning => _isRunning;
    public InspectionState CurrentState { get; private set; } = InspectionState.Idle;
    public InspectionConfig Config => _config;

    /// <summary>当前检测执行阶段，用于 Timeout 诊断。每次步进前由 UpdateProgress 更新。</summary>
    private volatile string _currentExecutionStage = "Idle";
    /// <summary>当前检测执行阶段（只读），供外部查询引擎卡在哪个环节。</summary>
    public string CurrentExecutionStage => _currentExecutionStage;

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

            _progress.Reset();
            var totalSw = Stopwatch.StartNew();

            try
            {
                SetState(InspectionState.Testing);
                LogInfo($"开始检测 - INS:{inspectionId}, 序列号:{barcode}, 机种:{modelName}, 测试点数:{_config.TestPoints.Count}");

                await InitializeInspectionAsync(_inspectionCts.Token).ConfigureAwait(false);

                // ── 本轮检测开始时清上一轮残留 ──
                // 清 DT130~DT185、DT302，确保引脚输出区和继电器完成标志为初始状态
                await ClearPlcOutputsAndRelayFlagAsync(_inspectionCts.Token).ConfigureAwait(false);

                int passCount = 0;
                int failCount = 0;
                int firstNgIndex = -1;

                for (int i = 0; i < _config.TestPoints.Count; i++)
                {
                    _inspectionCts.Token.ThrowIfCancellationRequested();
                    _currentExecutionStage = "CheckInterrupts";

                    var interruptResult = await CheckPlcInterruptsAsync(_inspectionCts.Token).ConfigureAwait(false);
                    if (interruptResult != PlcInterruptAction.Continue)
                    {
                        bool canContinue = await HandlePlcInterruptAsync(interruptResult, i, _inspectionCts.Token).ConfigureAwait(false);
                        if (!canContinue)
                        {
                            result.IsAborted = true;
                            result.ErrorMessage = _progress.LastErrorMessage ?? "检测被 PLC 信号中断";
                            result.EndTime = DateTime.Now;
                            // ★ 从 interruptResult 推断停止原因
                            result.StopReason = interruptResult switch
                            {
                                PlcInterruptAction.Stop => InspectionStopReason.PlcStop,
                                PlcInterruptAction.Reset => InspectionStopReason.Reset,
                                PlcInterruptAction.EmergencyStop => InspectionStopReason.EmergencyStop,
                                _ => InspectionStopReason.None
                            };
                            return result;
                        }
                    }

                    TestPointConfig testPoint = _config.TestPoints[i];
                    testPoint.ContinuityThresholdOhm = _config.ContinuityThresholdOhm;
                    testPoint.ActualContinuityState = string.Empty;
                    StepStarted?.Invoke(this, new StepStartedEventArgs(i, testPoint));
                    LogInfo($"正在检测 [{i + 1}/{_config.TestPoints.Count}] {testPoint.Name} ({testPoint.CheckMode})");

                    _currentExecutionStage = "WritePinsToPlc";
                    var writeResult = await _plcDevice.WriteCurrentTestPointAsync(
                        testPoint.PinLeft,
                        testPoint.PinRight,
                        testPoint.PinLeftPolarityCode,
                        testPoint.PinRightPolarityCode,
                        _inspectionCts.Token).ConfigureAwait(false);

                    if (!writeResult.IsSuccess)
                    {
                        InspectionMeasurementEvaluator.MarkNg(testPoint, writeResult.Message);
                        failCount++;
                        if (firstNgIndex < 0) firstNgIndex = i;
                        StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint, InspectionMeasurementEvaluator.Failed(writeResult.Message)));
                        continue;
                    }

                    _inspectionCts.Token.ThrowIfCancellationRequested();

                    _currentExecutionStage = "WaitDt302";
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
                            result.ErrorMessage = _progress.LastErrorMessage ?? "检测被 PLC 信号中断";
                            result.EndTime = DateTime.Now;
                            result.StopReason = postRelayInterrupt switch
                            {
                                PlcInterruptAction.Stop => InspectionStopReason.PlcStop,
                                PlcInterruptAction.Reset => InspectionStopReason.Reset,
                                PlcInterruptAction.EmergencyStop => InspectionStopReason.EmergencyStop,
                                _ => InspectionStopReason.None
                            };
                            return result;
                        }
                    }

                    await Task.Delay(_config.RelaySettleTimeMs, _inspectionCts.Token).ConfigureAwait(false);

                    // ── 按检查方式切换万用表模式 ──
                    _currentExecutionStage = "SetupMultimeterMode";
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

                    _currentExecutionStage = "ReadMultimeter";
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

                    MeasurementResult measurement = InspectionMeasurementEvaluator.Parse(rawText);

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
                        _progress.CurrentItemIndex = i;
                        _progress.LastUpdatedTime = DateTime.Now;

                        await AbortCurrentRunAsync(result, $"万用表返回{measurement.ValueKind}：点位 {testPoint.Name} 判定 NG，检测中止", InspectionState.Aborted).ConfigureAwait(false);
                        return result;
                    }

                    _currentExecutionStage = "JudgeResult";
                    string judgment = InspectionMeasurementEvaluator.Judge(measurement, testPoint);
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
                    _currentExecutionStage = "WritePointResult";
                    await _plcDevice.WritePointResultAsync(i, judgment == "OK", _inspectionCts.Token).ConfigureAwait(false);
                    _inspectionCts.Token.ThrowIfCancellationRequested();
                    StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint, measurement));
                    _progress.CurrentItemIndex = i + 1;
                    _progress.LastUpdatedTime = DateTime.Now;

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

                        _currentExecutionStage = "ClearPointOutputs";
                        await ClearPlcOutputsAndRelayFlagAsync(_inspectionCts.Token).ConfigureAwait(false);
                        _currentExecutionStage = "ClearPcReady";
                        await _plcDevice.ClearPcReadyAsync(CancellationToken.None).ConfigureAwait(false);
                        // 单项 NG 不写 DT304/DT305，等待复位或终了

                        SetState(InspectionState.StoppedBySingleItemNg);
                        return result;
                    }

                    _currentExecutionStage = "ClearPointOutputs";
                    await ClearPlcOutputsAndRelayFlagAsync(_inspectionCts.Token).ConfigureAwait(false);
                    _inspectionCts.Token.ThrowIfCancellationRequested();
                    _currentExecutionStage = "UpdateProgress";
                    _progress.CurrentItemIndex = i + 1;
                    _progress.LastUpdatedTime = DateTime.Now;
                }

                result.TotalCount = _config.TestPoints.Count;
                result.PassCount = passCount;
                result.FailCount = failCount;
                result.IsAllPassed = failCount == 0;
                result.EndTime = DateTime.Now;

                _progress.CurrentStep = "Completed";
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
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[检测流程][{INS}] 检测流程异常", inspectionId);
                await AbortCurrentRunAsync(result, ex.Message, InspectionState.Error).ConfigureAwait(false);
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
                _progress.LastErrorMessage = "停止触发，检测中止";
                await ClearPlcOutputsAndRelayFlagAsync(ct).ConfigureAwait(false);
                LogInfo($"PLC 停止信号 DT122=1，检测中止（不保留断点）");
                return false;

            case PlcInterruptAction.Reset:
                _abortReason = InspectionStopReason.Reset;
                SetState(InspectionState.ResetRequested);
                _progress.Reset();
                await ClearPlcOutputsAndRelayFlagAsync(ct).ConfigureAwait(false);
                // DT121 由 TestPageViewModel 在界面结果、内部状态和 PLC 输出全部清理完成后统一清零。
                // 引擎只负责中止当前检测，避免先清 DT121 导致运行界面轮询错过复位信号。
                _progress.LastErrorMessage = "复位触发，检测中止";
                return false;

            case PlcInterruptAction.EmergencyStop:
                _abortReason = InspectionStopReason.EmergencyStop;
                SetState(InspectionState.PausedByEmergencyStop);
                _progress.LastErrorMessage = "急停触发，必须复位后重新启动";
                await ClearPlcOutputsAndRelayFlagAsync(ct).ConfigureAwait(false);
                return false;

            default:
                return true;
        }
    }

    private async Task AbortCurrentRunAsync(InspectionResult result, string message, InspectionState state)
    {
        _currentExecutionStage = "Faulted";
        _progress.LastErrorMessage = message;
        _progress.LastUpdatedTime = DateTime.Now;
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

    public void Stop(InspectionStopReason stopReason = InspectionStopReason.PlcStop)
    {
        if (_isRunning)
        {
            LogInfo($"正在中止检测（原因={stopReason}）...");
            _abortReason = stopReason;
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
        _progress.LastErrorMessage = "急停触发，必须复位后重新启动";
        _abortReason = InspectionStopReason.EmergencyStop;
        _inspectionCts?.Cancel();
    }

    /// <summary>
    /// 停止检测并等待引擎退出，最大等待 timeout 时长。
    /// 超时后仍返回，不做额外强制中止；调用方继续安全清理 PLC 输出和 UI 状态。
    /// </summary>
    public async Task<InspectionStopWaitResult> StopAndWaitAsync(TimeSpan timeout, InspectionStopReason stopReason = InspectionStopReason.PlcStop, CancellationToken ct = default)
    {
        if (!_isRunning)
        {
            _logger.LogInformation("[检测流程][停止等待] 调用时引擎已停止");
            return InspectionStopWaitResult.AlreadyStopped;
        }

        Stop(stopReason);

        var deadline = DateTime.UtcNow + timeout;
        while (_isRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        if (_isRunning)
        {
            _logger.LogError("[检测流程][停止等待][审计] 已请求停止检测，但等待 {TimeoutMs}ms 后仍未完全退出",
                timeout.TotalMilliseconds);
            return InspectionStopWaitResult.Timeout;
        }

        _logger.LogInformation("[检测流程][停止等待] 引擎已在 {TimeoutMs}ms 内停止", timeout.TotalMilliseconds);
        return InspectionStopWaitResult.Stopped;
    }
    public void ClearResetState()
    {
        _progress.Reset();
    }

    private void SetState(InspectionState newState)
    {
        var oldState = CurrentState;
        CurrentState = newState;
        _logger.LogDebug("[检测流程][{INS}] 状态变更 {OldState} -> {NewState}",
            _currentInspectionId ?? "-", oldState, newState);
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
