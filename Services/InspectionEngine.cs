// 📁 Services/InspectionEngine.cs
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// GM和E78 USB焊接不良检查 检测流程引擎
    /// 协调 PLC + 万用表 + 扫描枪 完成完整的检测流程
    /// 
    /// 检测流程：
    /// 1. 等待启动信号（条码扫描 或 PLC触发）
    /// 2. 条码绑定 → 上位机记录
    /// 3. PLC切换继电器 → 接入当前测试点回路
    /// 4. 万用表测量电阻值
    /// 5. 判定 OK/NG → 写入PLC
    /// 6. 切换下一个测试点 → 循环
    /// 7. 全部完成 → 记录结果
    /// </summary>
    public class InspectionEngine : IAsyncDisposable, IDisposable
    {
        #region 字段

        private readonly ILogger<InspectionEngine> _logger;
        private readonly ITcpClientPLCMotionService _plcService;
        private readonly GwInstekGDM9060Driver _dmmDriver;
        private readonly HoneywellH1900Scanner? _scanner;

        private readonly SemaphoreSlim _engineLock = new(1, 1);
        private CancellationTokenSource? _inspectionCts;

        private volatile bool _isRunning;
        private volatile bool _isDisposed;

        // PLC 地址映射（可根据实际PLC程序调整）
        private readonly PlcAddressMap _plcMap;

        // 检测配置
        private InspectionConfig _config;

        #endregion

        #region 事件

        /// <summary>
        /// 检测流程状态变更
        /// </summary>
        public event EventHandler<InspectionStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// 单步检测完成
        /// </summary>
        public event EventHandler<StepCompletedEventArgs>? StepCompleted;

        /// <summary>
        /// 全部检测完成
        /// </summary>
        public event EventHandler<InspectionCompletedEventArgs>? InspectionCompleted;

        /// <summary>
        /// 条码扫描事件（转发自扫描枪）
        /// </summary>
        public event EventHandler<BarcodeReceivedEventArgs>? BarcodeScanned;

        /// <summary>
        /// 日志事件
        /// </summary>
        public event EventHandler<string>? LogMessage;

        #endregion

        #region 属性

        public bool IsRunning => _isRunning;
        public InspectionState CurrentState { get; private set; } = InspectionState.Idle;
        public InspectionConfig Config => _config;

        #endregion

        #region 构造函数

        public InspectionEngine(
            ILogger<InspectionEngine> logger,
            ITcpClientPLCMotionService plcService,
            GwInstekGDM9060Driver dmmDriver,
            HoneywellH1900Scanner? scanner = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _dmmDriver = dmmDriver ?? throw new ArgumentNullException(nameof(dmmDriver));
            _scanner = scanner;

            _plcMap = new PlcAddressMap();
            _config = new InspectionConfig();

            // 订阅扫描枪事件
            if (_scanner != null)
            {
                _scanner.BarcodeReceived += OnScannerBarcodeReceived;
            }
        }

        #endregion

        #region 初始化与配置

        /// <summary>
        /// 设置检测配置
        /// </summary>
        public void SetConfig(InspectionConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            LogInfo($"检测配置已更新：测试点数={config.TestPoints.Count}");
        }

        #endregion

        #region 主检测流程

        /// <summary>
        /// 启动检测流程
        /// </summary>
        public async Task<InspectionResult> RunInspectionAsync(
            string barcode,
            string modelName,
            string operatorName,
            CancellationToken ct = default)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("检测引擎正在运行中");
            }

            await _engineLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _isRunning = true;
                _inspectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                var result = new InspectionResult
                {
                    Barcode = barcode,
                    ModelName = modelName,
                    OperatorName = operatorName,
                    StartTime = DateTime.Now
                };

                try
                {
                    // === 步骤1：初始化 ===
                    SetState(InspectionState.Initializing);
                    LogInfo($"开始检测 - 条码:{barcode}, 机种:{modelName}, 作业员:{operatorName}");
                    await InitializeInspectionAsync(_inspectionCts.Token).ConfigureAwait(false);

                    // === 步骤2：逐点检测 ===
                    SetState(InspectionState.Testing);
                    int passCount = 0;
                    int failCount = 0;

                    for (int i = 0; i < _config.TestPoints.Count; i++)
                    {
                        _inspectionCts.Token.ThrowIfCancellationRequested();

                        var testPoint = _config.TestPoints[i];
                        LogInfo($"正在检测 [{i + 1}/{_config.TestPoints.Count}] {testPoint.Name} ({testPoint.CheckMode}/{testPoint.Unit})");

                        // 2.1 切换继电器到当前测试点
                        await SwitchToTestPointAsync(i, _inspectionCts.Token).ConfigureAwait(false);

                        // 2.2 短暂延时等待继电器稳定
                        await Task.Delay(_config.RelaySettleTimeMs, _inspectionCts.Token).ConfigureAwait(false);

                        // 2.3 万用表测量
                        var measurement = await _dmmDriver.MeasureResistanceAsync(_inspectionCts.Token).ConfigureAwait(false);

                        // 2.4 判定
                        var judgment = JudgeResult(measurement, testPoint);
                        testPoint.ActualValue = measurement.Value;
                        testPoint.Judgment = judgment;
                        testPoint.IsTested = true;

                        if (judgment == "OK")
                        {
                            passCount++;
                            LogInfo($"  ✅ {testPoint.Name}: {measurement.Value:F4}Ω → OK");
                        }
                        else
                        {
                            failCount++;
                            var detail = testPoint.CheckMode == "Resistance"
                                ? $" (范围:{testPoint.LowerLimit}~{testPoint.UpperLimit}Ω)"
                                : $" (期望:{testPoint.Unit})";
                            LogInfo($"  ❌ {testPoint.Name}: {measurement.Value:F4}Ω → NG{detail}");
                        }

                        // 2.5 将判定结果写入PLC
                        await WriteJudgmentToPlcAsync(i, judgment, _inspectionCts.Token).ConfigureAwait(false);

                        // 2.6 触发单步完成事件
                        StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint, measurement));
                    }

                    // === 步骤3：完成 ===
                    result.TotalCount = _config.TestPoints.Count;
                    result.PassCount = passCount;
                    result.FailCount = failCount;
                    result.IsAllPassed = failCount == 0;
                    result.EndTime = DateTime.Now;

                    SetState(result.IsAllPassed ? InspectionState.CompletedPass : InspectionState.CompletedFail);
                    LogInfo($"检测完成 - 总数:{result.TotalCount}, 良品:{result.PassCount}, 不良:{result.FailCount}");

                    // 写入最终结果到PLC
                    await WriteFinalResultToPlcAsync(result.IsAllPassed, _inspectionCts.Token).ConfigureAwait(false);

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
                    _logger.LogError(ex, "检测流程异常");
                    SetState(InspectionState.Error);
                    result.ErrorMessage = ex.Message;

                    // 异常时写入PLC错误信号
                    await SafeWritePlcAsync(_plcMap.ErrorFlag, true).ConfigureAwait(false);
                }

                return result;
            }
            finally
            {
                _isRunning = false;
                _engineLock.Release();
            }
        }

        /// <summary>
        /// 停止检测
        /// </summary>
        public void Stop()
        {
            if (_isRunning)
            {
                LogInfo("正在中止检测...");
                _inspectionCts?.Cancel();
            }
        }

        #endregion

        #region 子步骤实现

        /// <summary>
        /// 初始化检测
        /// </summary>
        private async Task InitializeInspectionAsync(CancellationToken ct)
        {
            // 1. 重置PLC相关信号
            await SafeWritePlcAsync(_plcMap.StartFlag, false).ConfigureAwait(false);
            await SafeWritePlcAsync(_plcMap.OkFlag, false).ConfigureAwait(false);
            await SafeWritePlcAsync(_plcMap.NgFlag, false).ConfigureAwait(false);
            await SafeWritePlcAsync(_plcMap.ErrorFlag, false).ConfigureAwait(false);
            await SafeWritePlcAsync(_plcMap.BusyFlag, true).ConfigureAwait(false);

            // 2. 切换万用表到电阻测量模式
            await _dmmDriver.SetMeasureFunctionAsync(MeasureFunction.Resistance2W, ct).ConfigureAwait(false);

            // 3. 短暂延时
            await Task.Delay(200, ct).ConfigureAwait(false);

            LogInfo("检测初始化完成");
        }

        /// <summary>
        /// 切换到指定测试点（通过PLC控制继电器）
        /// </summary>
        private async Task SwitchToTestPointAsync(int index, CancellationToken ct)
        {
            var testPoint = _config.TestPoints[index];

            // 方案1：使用保持寄存器写入测试点编号
            await SafeWritePlcRegisterAsync(_plcMap.TestPointSelectRegister, (ushort)(index + 1)).ConfigureAwait(false);

            // 方案2：使用多个线圈控制继电器组
            if (testPoint.RelayChannel.HasValue)
            {
                // 先关闭所有继电器
                for (int i = 0; i < _config.TestPoints.Count; i++)
                {
                    if (_config.TestPoints[i].RelayChannel.HasValue)
                    {
                        // ✅ 修正：显式转换为 ushort
                        ushort coilAddress = (ushort)(_plcMap.RelayBaseAddress + _config.TestPoints[i].RelayChannel!.Value);
                        await SafeWritePlcAsync(coilAddress, false).ConfigureAwait(false);
                    }
                }

                // 再打开目标继电器
                // ✅ 修正：显式转换为 ushort
                ushort targetCoilAddress = (ushort)(_plcMap.RelayBaseAddress + testPoint.RelayChannel.Value);
                await SafeWritePlcAsync(targetCoilAddress, true).ConfigureAwait(false);
            }

            _logger.LogDebug("已切换到测试点: {TestPointName} (通道:{Channel})",
                testPoint.Name, testPoint.RelayChannel);
        }

        /// <summary>
        /// 判定测量结果
        ///
        /// 判定逻辑（方案需求变动）：
        ///   导通模式 (Continuity)：
        ///     - Unit="OPEN"（期望开路）：实测值 > 1MΩ → OK，否则 NG
        ///     - Unit="SHORT"（期望短路）：实测值 < 1Ω → OK，否则 NG
        ///   电阻模式 (Resistance)：
        ///     - LowerLimit ≤ 实测值 ≤ UpperLimit → OK，否则 NG
        /// </summary>
        private string JudgeResult(MeasurementResult measurement, TestPointConfig testPoint)
        {
            if (!measurement.IsValid)
            {
                _logger.LogWarning("测量值无效: {ErrorMessage}", measurement.ErrorMessage);
                return "NG";
            }

            double value = measurement.Value;

            if (testPoint.CheckMode == "Resistance")
            {
                // ─── 电阻值检测：判断实测值是否在下限～上限范围内 ───
                double lower = testPoint.LowerLimit ?? 0;
                double upper = testPoint.UpperLimit ?? double.MaxValue;

                if (value >= lower && value <= upper)
                    return "OK";
                else
                    return "NG";
            }
            else
            {
                // ─── 导通检测 ───
                if (testPoint.Unit == "SHORT")
                {
                    // 期望短路：实测值 < 1Ω → OK
                    return value < 1.0 ? "OK" : "NG";
                }
                else
                {
                    // 期望开路（OPEN）：实测值 > 1MΩ → OK
                    return value > 1_000_000.0 ? "OK" : "NG";
                }
            }
        }

        /// <summary>
        /// 将单点判定结果写入PLC
        /// </summary>
        private async Task WriteJudgmentToPlcAsync(int index, string judgment, CancellationToken ct)
        {
            // 将结果写入PLC的保持寄存器区域
            // 例如：D100开始存放每个点的判定结果 (1=OK, 0=NG)
            ushort value = judgment == "OK" ? (ushort)1 : (ushort)0;
            ushort registerAddress = (ushort)(_plcMap.TestResultBaseRegister + index);

            await SafeWritePlcRegisterAsync(registerAddress, value).ConfigureAwait(false);
        }

        /// <summary>
        /// 写入最终结果到PLC
        /// </summary>
        private async Task WriteFinalResultToPlcAsync(bool isAllPassed, CancellationToken ct)
        {
            await SafeWritePlcAsync(_plcMap.BusyFlag, false).ConfigureAwait(false);

            if (isAllPassed)
            {
                await SafeWritePlcAsync(_plcMap.OkFlag, true).ConfigureAwait(false);
                await SafeWritePlcAsync(_plcMap.NgFlag, false).ConfigureAwait(false);
            }
            else
            {
                await SafeWritePlcAsync(_plcMap.OkFlag, false).ConfigureAwait(false);
                await SafeWritePlcAsync(_plcMap.NgFlag, true).ConfigureAwait(false);
            }
        }

        #endregion

        #region PLC安全写入辅助

        private async Task SafeWritePlcAsync(ushort coilAddress, bool value)
        {
            try
            {
                await _plcService.ExecuteWriteOperationAsync(
                    functionCode: 0x05,        // 写单个线圈
                    unitId: 1,
                    startAddress: coilAddress,
                    data: new ushort[] { value ? (ushort)0xFF00 : (ushort)0x0000 },
                    timeoutMs: 3000
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PLC写入失败 (地址:{Address}, 值:{Value})", coilAddress, value);
            }
        }

        private async Task SafeWritePlcRegisterAsync(ushort registerAddress, ushort value)
        {
            try
            {
                await _plcService.ExecuteWriteOperationAsync(
                    functionCode: 0x06,        // 写单个保持寄存器
                    unitId: 1,
                    startAddress: registerAddress,
                    data: new ushort[] { value },
                    timeoutMs: 3000
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PLC寄存器写入失败 (地址:{Address}, 值:{Value})", registerAddress, value);
            }
        }

        #endregion

        #region 扫描枪事件处理

        private void OnScannerBarcodeReceived(object? sender, BarcodeReceivedEventArgs e)
        {
            BarcodeScanned?.Invoke(this, e);
        }

        #endregion

        #region 状态管理

        private void SetState(InspectionState newState)
        {
            var oldState = CurrentState;
            CurrentState = newState;
            StateChanged?.Invoke(this, new InspectionStateChangedEventArgs(oldState, newState));
        }

        private void LogInfo(string message)
        {
            _logger.LogInformation(message);
            LogMessage?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        #endregion

        #region IDisposable / IAsyncDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_scanner != null)
            {
                _scanner.BarcodeReceived -= OnScannerBarcodeReceived;
            }

            _inspectionCts?.Cancel();
            _inspectionCts?.Dispose();
            _engineLock?.Dispose();

            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_scanner != null)
            {
                _scanner.BarcodeReceived -= OnScannerBarcodeReceived;
            }

            _inspectionCts?.Cancel();
            _inspectionCts?.Dispose();
            _engineLock?.Dispose();

            await Task.CompletedTask;
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    #region 配置与模型

    /// <summary>
    /// 检测配置
    /// 由方案文件（PlanModel）的检测项目填充，不再硬编码默认测试点
    ///
    /// 改动说明（方案需求变动）：
    /// 每项检测使用独立阈值（来自 PlanItem），不再使用全局阈值
    /// </summary>
    public class InspectionConfig
    {
        /// <summary>
        /// 测试点列表
        /// 由外部加载方案文件后通过 SetConfig() 或直接赋值注入
        /// </summary>
        public List<TestPointConfig> TestPoints { get; set; } = new();

        /// <summary>
        /// 继电器稳定时间 (ms)
        /// </summary>
        public int RelaySettleTimeMs { get; set; } = 150;
    }

    /// <summary>
    /// 测试点配置
    /// 每个测试点对应方案中的一个 PlanItem
    ///
    /// 改动说明（方案需求变动）：
    /// 新增 CheckMode / LowerLimit / UpperLimit / Unit 字段
    /// 判定逻辑从全局阈值改为每项独立阈值
    /// </summary>
    public class TestPointConfig
    {
        /// <summary>项目名称（如 "A4-A5"）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 检测方式
        /// "Continuity" — 导通检测，"Resistance" — 电阻值检测
        /// </summary>
        public string CheckMode { get; set; } = "Continuity";

        /// <summary>电阻值下限（Ω），导通模式为 null</summary>
        public double? LowerLimit { get; set; }

        /// <summary>电阻值上限（Ω），导通模式为 null</summary>
        public double? UpperLimit { get; set; }

        /// <summary>
        /// 导通模式期望结果： "OPEN"（期望开路）或 "SHORT"（期望短路）
        /// 电阻模式： "Ω"
        /// </summary>
        public string Unit { get; set; } = "OPEN";

        /// <summary>继电器通道号</summary>
        public int? RelayChannel { get; set; }

        // 运行时填充
        public double ActualValue { get; set; }
        public string Judgment { get; set; } = string.Empty;
        public bool IsTested { get; set; }
    }

    /// <summary>
    /// 检测状态枚举
    /// </summary>
    public enum InspectionState
    {
        Idle,           // 空闲
        Initializing,   // 初始化中
        Testing,        // 检测中
        CompletedPass,  // 检测完成-良品
        CompletedFail,  // 检测完成-不良
        Aborted,        // 已中止
        Error           // 错误
    }

    /// <summary>
    /// 检测结果
    /// </summary>
    public class InspectionResult
    {
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

    /// <summary>
    /// PLC地址映射（松下FP0H Modbus地址）
    /// ⚠️ 请根据实际PLC程序修改
    /// </summary>
    public class PlcAddressMap
    {
        // 线圈地址 (0x05功能码)
        public ushort StartFlag { get; set; } = 0;       // M0: 启动信号
        public ushort OkFlag { get; set; } = 1;          // M1: OK信号
        public ushort NgFlag { get; set; } = 2;          // M2: NG信号
        public ushort ErrorFlag { get; set; } = 3;       // M3: 错误信号
        public ushort BusyFlag { get; set; } = 4;        // M4: 忙碌信号
        public ushort RelayBaseAddress { get; set; } = 10; // M10开始: 继电器控制

        // 保持寄存器地址 (0x06/0x10功能码)
        public ushort TestPointSelectRegister { get; set; } = 100;  // D100: 测试点选择
        public ushort TestResultBaseRegister { get; set; } = 200;   // D200开始: 测试结果存储
    }

    #endregion

    #region 事件参数

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

    #endregion
}