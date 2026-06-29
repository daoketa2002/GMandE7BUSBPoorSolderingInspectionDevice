// 📁 Services/InspectionEngine.cs
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services;

/// <summary>
/// GM和E78 USB焊接不良检查 检测流程引擎。
/// 协调 PLC + 万用表完成完整的检测流程闭环。
///
/// 主检测流程（每点）：
///   1. 写左右引脚编号到 PLC
///   2. 等待 PLC 继电器切换完成（带超时）
///   3. 等待稳定延时（RelaySettleTimeMs）
///   4. 万用表测量
///   5. 判定 OK/NG
///   6. 写单点结果到 PLC
///   7. 继续下一点
///
/// PLC 信号处理（检测过程中持续监控）：
///   - DT122=1 停止 → PausedByStop，暂停当前项目，等待恢复后从当前点继续
///   - DT121=1 复位 → ResetRequested，中止检测→写DT160→回到待机
///   - DT123=1 急停 → PausedByEmergencyStop，弹窗锁定，恢复后走复位→启动流程
///   - DT161=1 板离设备报警 → Aborted，立即中止
///
/// 节拍日志：
///   每阶段使用 Stopwatch 计时，输出带 InspectionId 的耗时日志。
/// </summary>
public partial class InspectionEngine : IAsyncDisposable, IDisposable
{
    #region 字段

    private readonly ILogger<InspectionEngine> _logger;
    private readonly IPlcDevice _plcDevice;
    private readonly GwInstekGDM9060Driver _dmmDriver;

    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private CancellationTokenSource? _inspectionCts;

    private volatile bool _isRunning;
    private volatile bool _isDisposed;
    private string? _currentInspectionId;

    // 检测配置
    private InspectionConfig _config;

    #endregion

    #region 事件

    /// <summary>检测流程状态变更</summary>
    public event EventHandler<InspectionStateChangedEventArgs>? StateChanged;

    /// <summary>单步检测开始（写引脚之前触发）</summary>
    public event EventHandler<StepStartedEventArgs>? StepStarted;

    /// <summary>单步检测完成（判定生成后触发）</summary>
    public event EventHandler<StepCompletedEventArgs>? StepCompleted;

    /// <summary>全部检测完成</summary>
    public event EventHandler<InspectionCompletedEventArgs>? InspectionCompleted;

    /// <summary>日志事件</summary>
    public event EventHandler<string>? LogMessage;

    #endregion

    #region 属性

    public bool IsRunning => _isRunning;
    public InspectionState CurrentState { get; private set; } = InspectionState.Idle;
    public InspectionConfig Config => _config;

    /// <summary>检测中被打断时的当前点索引（0-based），-1 表示无中断</summary>
    public int InterruptedPointIndex { get; private set; } = -1;

    #endregion

    #region 构造函数

    public InspectionEngine(
        ILogger<InspectionEngine> logger,
        IPlcDevice plcDevice,
        GwInstekGDM9060Driver dmmDriver)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _dmmDriver = dmmDriver ?? throw new ArgumentNullException(nameof(dmmDriver));

        _config = new InspectionConfig();
    }

    #endregion

    #region 初始化与配置

    /// <summary>设置检测配置</summary>
    public void SetConfig(InspectionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        LogInfo($"检测配置已更新：测试点数={config.TestPoints.Count}");
    }

    #endregion

    #region 主检测流程

    /// <summary>
    /// 启动检测流程。
    /// 由外部（TestPageViewModel）在收到 PLC DT120=1 并完成启动复合后调用。
    /// startFromPointIndex: 从指定点开始（停止恢复时使用），默认 0。
    /// </summary>
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

            var inspectionId = CreateInspectionId();
            _currentInspectionId = inspectionId;

            var result = new InspectionResult
            {
                InspectionId = inspectionId,
                Barcode = barcode,
                ModelName = modelName,
                OperatorName = operatorName,
                StartTime = DateTime.Now
            };

            // 单件总计时
            var totalSw = Stopwatch.StartNew();

            try
            {
                // ═══════════════════════════════════════════════════
                // 步骤1：初始化检测
                // ═══════════════════════════════════════════════════
                var initSw = Stopwatch.StartNew();
                SetState(InspectionState.Testing);
                LogInfo($"开始检测 - INS:{inspectionId}, 条码:{barcode}, 机种:{modelName}, 测试点数:{_config.TestPoints.Count}");
                await InitializeInspectionAsync(_inspectionCts.Token).ConfigureAwait(false);
                initSw.Stop();
                LogBeat(inspectionId, "检测初始化", initSw.ElapsedMilliseconds);

                // ═══════════════════════════════════════════════════
                // 步骤2：逐点检测
                // ═══════════════════════════════════════════════════
                int passCount = 0;
                int failCount = 0;
                int firstNgIndex = -1;

                for (int i = startFromPointIndex; i < _config.TestPoints.Count; i++)
                {
                    _inspectionCts.Token.ThrowIfCancellationRequested();

                    // 每点开始前检查 PLC 中断信号
                    var interruptResult = await CheckPlcInterruptsAsync(_inspectionCts.Token).ConfigureAwait(false);
                    if (interruptResult != PlcInterruptAction.Continue)
                    {
                        var handled = await HandlePlcInterruptAsync(interruptResult, i, _inspectionCts.Token).ConfigureAwait(false);
                        if (!handled)
                        {
                            // 未恢复（急停/复位/板离）→ 中止本次检测
                            SetState(InspectionState.Aborted);
                            result.IsAborted = true;
                            result.ErrorMessage = interruptResult switch
                            {
                                PlcInterruptAction.Reset => "复位请求，检测中止",
                                PlcInterruptAction.EmergencyStop => "急停触发，检测中止",
                                PlcInterruptAction.BoardLeaving => "板离设备，检测中止",
                                _ => "检测被中断"
                            };
                            LogInfo($"检测中止: {result.ErrorMessage}");
                            await _plcDevice.RequestRelayDisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                            return result;
                        }
                    }

                    var testPoint = _config.TestPoints[i];

                    // 通知 ViewModel：当前项目开始检测
                    StepStarted?.Invoke(this, new StepStartedEventArgs(i, testPoint));
                    LogInfo($"正在检测 [{i + 1}/{_config.TestPoints.Count}] {testPoint.Name} ({testPoint.CheckMode})");

                    // ─── 2.1 写左右引脚编号到 PLC ───
                    var pinSw = Stopwatch.StartNew();
                    var pinResult = await _plcDevice.WriteCurrentTestPinsAsync(
                        testPoint.PinLeftCode, testPoint.PinRightCode, _inspectionCts.Token).ConfigureAwait(false);
                    pinSw.Stop();
                    LogBeat(inspectionId, $"点位 {testPoint.Name} PLC写入", pinSw.ElapsedMilliseconds);

                    if (!pinResult.IsSuccess)
                    {
                        _logger.LogWarning("[检测流程][{INS}] {Name} PLC引脚写入失败: {Msg}",
                            inspectionId, testPoint.Name, pinResult.Message);
                        testPoint.Judgment = "NG";
                        testPoint.ActualValue = 0;
                        testPoint.IsTested = true;
                        failCount++;
                        if (firstNgIndex < 0) firstNgIndex = i;
                        StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint,
                            new MeasurementResult { IsValid = false, ErrorMessage = pinResult.Message }));
                        continue;
                    }

                    // ─── 2.2 等待继电器切换完成 ───
                    var relaySw = Stopwatch.StartNew();
                    var relayResult = await _plcDevice.WaitRelaySwitchCompletedAsync(
                        TimeSpan.FromMilliseconds(_config.RelaySwitchTimeoutMs), _inspectionCts.Token).ConfigureAwait(false);
                    relaySw.Stop();
                    LogBeat(inspectionId, $"点位 {testPoint.Name} 继电器切换", relaySw.ElapsedMilliseconds);

                    if (!relayResult.IsSuccess)
                    {
                        _logger.LogWarning("[检测流程][{INS}] {Name} 继电器切换超时或失败: {Msg}",
                            inspectionId, testPoint.Name, relayResult.Message);
                        testPoint.Judgment = "NG";
                        testPoint.ActualValue = 0;
                        testPoint.IsTested = true;
                        testPoint.ErrorMessage = "继电器切换超时";
                        failCount++;
                        if (firstNgIndex < 0) firstNgIndex = i;
                        StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint,
                            new MeasurementResult { IsValid = false, ErrorMessage = "继电器切换超时" }));
                        continue;
                    }

                    // ─── 2.3 等待稳定延时 ───
                    var settleSw = Stopwatch.StartNew();
                    await Task.Delay(_config.RelaySettleTimeMs, _inspectionCts.Token).ConfigureAwait(false);
                    settleSw.Stop();
                    LogBeat(inspectionId, $"点位 {testPoint.Name} 稳定延时", settleSw.ElapsedMilliseconds);

                    // ─── 2.4 万用表测量 ───
                    var measSw = Stopwatch.StartNew();
                    var measurement = await _dmmDriver.MeasureResistanceAsync(_inspectionCts.Token).ConfigureAwait(false);
                    measSw.Stop();
                    LogBeat(inspectionId, $"点位 {testPoint.Name} 万用表测量", measSw.ElapsedMilliseconds);

                    if (!measurement.IsValid)
                    {
                        _logger.LogWarning("[检测流程][{INS}] {Name} 万用表测量无效: {Msg}",
                            inspectionId, testPoint.Name, measurement.ErrorMessage);
                        testPoint.Judgment = "NG";
                        testPoint.ActualValue = 0;
                        testPoint.IsTested = true;
                        failCount++;
                        if (firstNgIndex < 0) firstNgIndex = i;
                        StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint, measurement));
                        continue;
                    }

                    // ─── 2.5 判定 ───
                    var judgment = JudgeResult(measurement, testPoint);
                    testPoint.ActualValue = measurement.Value;
                    testPoint.Judgment = judgment;
                    testPoint.IsTested = true;

                    if (judgment == "OK")
                    {
                        passCount++;
                        LogInfo($"  ✅ {testPoint.Name}: {InputValidationHelper.FormatResistanceValue(measurement.Value)}Ω → OK");
                    }
                    else
                    {
                        failCount++;
                        if (firstNgIndex < 0) firstNgIndex = i;
                        var detail = testPoint.CheckMode == CheckModeConstants.Resistance
                            ? $" (范围:{FormatNullableResistance(testPoint.LowerLimit)}~{FormatNullableResistance(testPoint.UpperLimit)}Ω)"
                            : $" (期望:{testPoint.ModeValue})";
                        LogInfo($"  ❌ {testPoint.Name}: {InputValidationHelper.FormatResistanceValue(measurement.Value)}Ω → NG{detail}");
                    }

                    // ─── 2.6 写单点结果到 PLC ───
                    var writeSw = Stopwatch.StartNew();
                    await _plcDevice.WritePointResultAsync(i, judgment == "OK", _inspectionCts.Token).ConfigureAwait(false);
                    writeSw.Stop();
                    LogBeat(inspectionId, $"点位 {testPoint.Name} PC写回PLC", writeSw.ElapsedMilliseconds);

                    // ─── 2.7 触发单步完成事件 ───
                    StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint, measurement));
                }

                // ═══════════════════════════════════════════════════
                // 步骤3：完成
                // ═══════════════════════════════════════════════════
                result.TotalCount = _config.TestPoints.Count;
                result.PassCount = passCount;
                result.FailCount = failCount;
                result.IsAllPassed = failCount == 0;
                result.EndTime = DateTime.Now;

                SetState(result.IsAllPassed ? InspectionState.CompletedPass : InspectionState.CompletedFail);
                LogInfo($"检测完成 - INS:{inspectionId}, 总数:{result.TotalCount}, 良品:{result.PassCount}, 不良:{result.FailCount}");

                // 写综合结果到 PLC
                await _plcDevice.WriteFinalResultAsync(result.IsAllPassed,
                    firstNgIndex >= 0 ? firstNgIndex : null, _inspectionCts.Token).ConfigureAwait(false);

                // 写 DT160 通知 PLC 断开引脚输出
                var discSw = Stopwatch.StartNew();
                await _plcDevice.RequestRelayDisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                discSw.Stop();
                LogBeat(inspectionId, "PC写DT160断开引脚输出", discSw.ElapsedMilliseconds);

                // 清 DT120 防止重复触发
                await _plcDevice.ClearStartRequestAsync(CancellationToken.None).ConfigureAwait(false);

                // 总耗时节拍日志
                totalSw.Stop();
                LogBeat(inspectionId, "单件总耗时", totalSw.ElapsedMilliseconds);

                // 触发完成事件
                InspectionCompleted?.Invoke(this, new InspectionCompletedEventArgs(result));
            }
            catch (OperationCanceledException)
            {
                SetState(InspectionState.Aborted);
                LogInfo("检测被取消");
                result.IsAborted = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[检测流程][{INS}] 检测流程异常 - 条码:{Barcode}, 机种:{Model}",
                    inspectionId, barcode, modelName);
                SetState(InspectionState.Error);
                result.ErrorMessage = ex.Message;

                // 异常时通知 PLC
                await _plcDevice.WritePcErrorAsync(CancellationToken.None).ConfigureAwait(false);
                totalSw.Stop();
                LogBeat(inspectionId, "异常中止耗时", totalSw.ElapsedMilliseconds);
            }

            return result;
        }
        finally
        {
            _isRunning = false;
            _currentInspectionId = null;
            InterruptedPointIndex = -1;
            _engineLock.Release();
        }
    }

    #endregion

    #region PLC 中断信号处理

    /// <summary>PLC 中断动作类型</summary>
    private enum PlcInterruptAction
    {
        Continue,          // 无中断，继续检测
        Stop,              // DT122=1 停止
        Reset,             // DT121=1 复位
        EmergencyStop,     // DT123=1 急停
        BoardLeaving       // DT161=1 板离设备
    }

    /// <summary>
    /// 检查 PLC 中断信号。每点检测前调用。
    /// </summary>
    private async Task<PlcInterruptAction> CheckPlcInterruptsAsync(CancellationToken ct)
    {
        try
        {
            var inputs = await _plcDevice.ReadMachineInputsAsync(ct).ConfigureAwait(false);
            if (!inputs.IsSuccess || inputs.Value == null)
                return PlcInterruptAction.Continue;

            var mi = inputs.Value;

            // 优先级：板离 > 急停 > 复位 > 停止
            if (mi.IsBoardLeavingAlarm) return PlcInterruptAction.BoardLeaving;
            if (mi.IsEmergencyStop) return PlcInterruptAction.EmergencyStop;
            if (mi.IsResetRequested) return PlcInterruptAction.Reset;
            if (mi.IsStopRequested) return PlcInterruptAction.Stop;

            return PlcInterruptAction.Continue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[检测流程] 检查 PLC 中断信号异常");
            return PlcInterruptAction.Continue;
        }
    }

    /// <summary>
    /// 处理 PLC 中断信号。
    /// 返回 true 表示已恢复可继续，false 表示检测应中止。
    /// </summary>
    private async Task<bool> HandlePlcInterruptAsync(PlcInterruptAction action, int currentPointIndex, CancellationToken ct)
    {
        switch (action)
        {
            case PlcInterruptAction.Stop:
                // DT122=1 停止：暂停当前项目
                SetState(InspectionState.PausedByStop);
                InterruptedPointIndex = currentPointIndex;
                LogInfo($"⚠️ PLC 停止信号(DT122)，检测已暂停，当前点索引={currentPointIndex}");

                // 等待 DT122=0（停止解除）
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    var inputs = await _plcDevice.ReadMachineInputsAsync(ct).ConfigureAwait(false);
                    if (inputs.IsSuccess && inputs.Value != null && !inputs.Value.IsStopRequested)
                        break;
                }

                // 停止解除后，等待 PLC 再次 DT120=1
                SetState(InspectionState.WaitingForPlcStart);
                LogInfo("停止已解除，等待 PLC 重新置 DT120=1 启动");

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    var inputs = await _plcDevice.ReadMachineInputsAsync(ct).ConfigureAwait(false);
                    if (inputs.IsSuccess && inputs.Value != null && inputs.Value.IsStartRequested)
                    {
                        await _plcDevice.ClearStartRequestAsync(ct).ConfigureAwait(false);
                        SetState(InspectionState.Testing);
                        LogInfo("PLC 重新启动，从当前点继续检测");
                        return true; // 从当前点继续
                    }
                }
                return false;

            case PlcInterruptAction.Reset:
                // DT121=1 复位：中止检测 → 写 DT160 → 写 DT121=0
                SetState(InspectionState.ResetRequested);
                InterruptedPointIndex = currentPointIndex;
                LogInfo("⚠️ PLC 复位信号(DT121)，检测中止");
                await _plcDevice.RequestRelayDisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                await _plcDevice.ClearResetRequestAsync(CancellationToken.None).ConfigureAwait(false);
                return false;

            case PlcInterruptAction.EmergencyStop:
                // DT123=1 急停：暂停并弹窗锁定
                SetState(InspectionState.PausedByEmergencyStop);
                InterruptedPointIndex = currentPointIndex;
                LogInfo("⚠️ PLC 急停信号(DT123)，检测暂停并弹窗锁定");
                return false;

            case PlcInterruptAction.BoardLeaving:
                // DT161=1 板离设备：立即中止
                SetState(InspectionState.Aborted);
                LogInfo("⚠️ PLC 板离设备报警(DT161)，检测立即中止");
                await _plcDevice.RequestRelayDisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                return false;

            default:
                return true;
        }
    }

    #endregion

    #region 子步骤实现

    /// <summary>初始化检测：切换万用表到电阻模式</summary>
    private async Task InitializeInspectionAsync(CancellationToken ct)
    {
        await _dmmDriver.SetMeasureFunctionAsync(MeasureFunction.Resistance2W, ct).ConfigureAwait(false);
        await Task.Delay(200, ct).ConfigureAwait(false);
        LogInfo("检测初始化完成（万用表电阻模式已设定）");
    }

    /// <summary>
    /// 判定测量结果。
    /// 导通模式：OPEN→>1MΩ，SHORT→<1Ω
    /// 电阻值模式：LowerLimit ≤ 值 ≤ UpperLimit
    /// </summary>
    private static string JudgeResult(MeasurementResult measurement, TestPointConfig testPoint)
    {
        if (!measurement.IsValid) return "NG";

        double value = measurement.Value;

        if (testPoint.CheckMode == CheckModeConstants.Resistance)
        {
            double lower = testPoint.LowerLimit ?? 0;
            double upper = testPoint.UpperLimit ?? double.MaxValue;
            return value >= lower && value <= upper ? "OK" : "NG";
        }

        // 导通模式
        return testPoint.ModeValue == "SHORT"
            ? (value < 1.0 ? "OK" : "NG")
            : (value > 1_000_000.0 ? "OK" : "NG");
    }

    private static string FormatNullableResistance(double? value)
    {
        return value.HasValue
            ? InputValidationHelper.FormatResistanceValue(value.Value)
            : "-";
    }

    #endregion

    #region 外部控制（供 ViewModel 调用）

    /// <summary>停止检测（终了按钮）</summary>
    public void Stop()
    {
        if (_isRunning)
        {
            LogInfo("正在中止检测...");
            _inspectionCts?.Cancel();
        }
    }

    /// <summary>急停解除后继续</summary>
    public void ResumeAfterEmergencyStop()
    {
        _logger.LogWarning("[检测流程] 急停已解除，等待用户按复位→启动");
    }

    /// <summary>复位状态清空</summary>
    public void ClearResetState()
    {
        InterruptedPointIndex = -1;
    }

    #endregion

    #region 状态管理

    private void SetState(InspectionState newState)
    {
        var oldState = CurrentState;
        CurrentState = newState;
        _logger.LogDebug("[检测流程][{INS}] 状态变更: {OldState} -> {NewState}",
            _currentInspectionId ?? "-", oldState, newState);
        StateChanged?.Invoke(this, new InspectionStateChangedEventArgs(oldState, newState));
    }

    private void LogInfo(string message)
    {
        if (string.IsNullOrWhiteSpace(_currentInspectionId))
            _logger.LogInformation("[检测流程] {Message}", message);
        else
            _logger.LogInformation("[检测流程][{INS}] {Message}", _currentInspectionId, message);

        LogMessage?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    /// <summary>节拍日志：带 InspectionId 和阶段名称的耗时记录</summary>
    private void LogBeat(string inspectionId, string stage, long elapsedMs)
    {
        _logger.LogInformation("[检测流程][{INS}] {Stage} 耗时 {ElapsedMs}ms", inspectionId, stage, elapsedMs);
    }

    private static string CreateInspectionId()
    {
        return $"INS-{DateTime.Now:yyyyMMddHHmmssfff}";
    }

    #endregion

    #region IDisposable / IAsyncDisposable

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _inspectionCts?.Cancel();
        _inspectionCts?.Dispose();
        _engineLock?.Dispose();

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _inspectionCts?.Cancel();
        _inspectionCts?.Dispose();
        _engineLock?.Dispose();

        await Task.CompletedTask;
        GC.SuppressFinalize(this);
    }

    #endregion
}

// ═══════════════════════════════════════════════════════════════
//  配置与模型（被 ViewModel 引用，保留在此文件）
// ═══════════════════════════════════════════════════════════════

#region 配置与模型

/// <summary>
/// 检测状态枚举
/// 状态流：Idle -> WaitingForProductInfo -> WaitingForValidPlan -> WaitingForDevices
///         -> WaitingForPlcStart -> StartingValidation -> Testing
///         -> CompletedPendingSave -> CompletedPass/CompletedFail
/// 异常中断：PausedByStop, PausedByEmergencyStop, ResetRequested, Aborted, Error
/// </summary>
public enum InspectionState
{
    Idle,                   // 空闲
    Initializing,           // 初始化中
    WaitingForProductInfo,  // 等待机种名称和序列号输入
    WaitingForValidPlan,    // 等待方案有效
    WaitingForDevices,      // 等待 PLC/万用表/扫描枪连接就绪
    WaitingForPlcStart,     // 等待 PLC 确认基板到位后置 DT120=1
    StartingValidation,     // 启动前复核
    Testing,                // 检测中
    PausedByStop,           // DT122=1 暂停
    PausedByEmergencyStop,  // DT123=1 急停暂停
    ResetRequested,         // DT121=1 复位请求
    CompletedPendingSave,   // 完成待保存
    CompletedPass,          // 完成-良品
    CompletedFail,          // 完成-不良
    Aborted,                // 已中止（板离设备、继电器超时、通信失败等）
    Error                   // 软件异常或不可恢复通信异常
}

/// <summary>
/// 检测配置。由方案文件（PlanModel）的检测项目填充。
/// </summary>
public class InspectionConfig
{
    /// <summary>测试点列表</summary>
    public List<TestPointConfig> TestPoints { get; set; } = new();

    /// <summary>继电器稳定时间 (ms)，默认 150ms</summary>
    public int RelaySettleTimeMs { get; set; } = 150;

    /// <summary>继电器切换完成等待超时 (ms)，默认 3000ms</summary>
    public int RelaySwitchTimeoutMs { get; set; } = 3000;
}

/// <summary>
/// 测试点配置。每个测试点对应方案中的一个 PlanItem。
/// </summary>
public class TestPointConfig
{
    public string Name { get; set; } = string.Empty;
    public string PinLeft { get; set; } = string.Empty;
    public string PinRight { get; set; } = string.Empty;

    /// <summary>左引脚 PLC 编码（A{n}=n, B{n}=20+n）</summary>
    public ushort PinLeftCode { get; set; }

    /// <summary>右引脚 PLC 编码</summary>
    public ushort PinRightCode { get; set; }

    public string PinLeftPolarity { get; set; } = PinPolarityConstants.Positive;
    public string PinRightPolarity { get; set; } = PinPolarityConstants.Negative;
    public ushort PinLeftPolarityCode { get; set; } = 1;
    public ushort PinRightPolarityCode { get; set; }

    /// <summary>检测方式（"导通"/"电阻值"）</summary>
    public string CheckMode { get; set; } = CheckModeConstants.Continuity;

    public double? LowerLimit { get; set; }
    public double? UpperLimit { get; set; }
    public string? ModeValue { get; set; } = "OPEN";
    public int? RelayChannel { get; set; }

    // 运行时填充
    public double ActualValue { get; set; }
    public string Judgment { get; set; } = string.Empty;
    public bool IsTested { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// 从方案项目创建检测配置。
    /// 集中完成引脚拆分和 PLC 编码，避免 UI 层散落硬件编码规则。
    /// </summary>
    public static TestPointConfig FromPlanItem(PlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var (pinLeft, pinRight) = SplitItemName(item.ItemName);
        var leftPolarity = PinPolarityConstants.Normalize(
            item.PinLeftPolarity, PinPolarityConstants.Positive);
        var rightPolarity = PinPolarityConstants.Normalize(
            item.PinRightPolarity, PinPolarityConstants.Negative);

        return new TestPointConfig
        {
            Name = item.ItemName,
            PinLeft = pinLeft,
            PinRight = pinRight,
            PinLeftCode = EncodePin(pinLeft),
            PinRightCode = EncodePin(pinRight),
            PinLeftPolarity = leftPolarity,
            PinRightPolarity = rightPolarity,
            PinLeftPolarityCode = PinPolarityConstants.ToPlcCode(leftPolarity),
            PinRightPolarityCode = PinPolarityConstants.ToPlcCode(rightPolarity),
            CheckMode = item.CheckMode,
            LowerLimit = item.LowerLimit,
            UpperLimit = item.UpperLimit,
            ModeValue = item.ModeValue
        };
    }

    /// <summary>拆分方案项目名（如 "A4-A5" → ("A4","A5")）</summary>
    private static (string Left, string Right) SplitItemName(string itemName)
    {
        var parts = (itemName ?? string.Empty).Split('-', 2);
        var left = parts.Length > 0 ? parts[0].Trim() : string.Empty;
        var right = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        return (left, right);
    }

    /// <summary>将引脚名转换为 PLC 数值编码：A{n}=n, B{n}=20+n</summary>
    private static ushort EncodePin(string pinName)
    {
        if (string.IsNullOrWhiteSpace(pinName) || pinName.Length < 2)
            return 0;

        char group = char.ToUpperInvariant(pinName[0]);
        if (!int.TryParse(pinName[1..], out int number) || number < 1 || number > 20)
            return 0;

        return group switch
        {
            'A' => (ushort)number,
            'B' => (ushort)(20 + number),
            _ => (ushort)0
        };
    }
}

/// <summary>检测结果</summary>
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

    public TimeSpan Duration => EndTime - StartTime;
}

/// <summary>检测状态变更事件参数</summary>
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

/// <summary>单步检测开始事件参数</summary>
public class StepStartedEventArgs : EventArgs
{
    public int StepIndex { get; }
    public TestPointConfig TestPoint { get; }

    public StepStartedEventArgs(int stepIndex, TestPointConfig testPoint)
    {
        StepIndex = stepIndex;
        TestPoint = testPoint ?? throw new ArgumentNullException(nameof(testPoint));
    }
}

/// <summary>单步检测完成事件参数</summary>
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

/// <summary>全部检测完成事件参数</summary>
public class InspectionCompletedEventArgs : EventArgs
{
    public InspectionResult Result { get; }

    public InspectionCompletedEventArgs(InspectionResult result)
    {
        Result = result;
    }
}

#endregion
