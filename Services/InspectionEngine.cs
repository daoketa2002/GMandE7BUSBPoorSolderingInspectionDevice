// 📁 Services/InspectionEngine.cs
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

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
    /// 
    /// 事件时序：
    ///   StepStarted → 继电器切换 → 延时稳定 → 万用表测量 → 判定 → StepCompleted
    ///   所有步骤完成后 → InspectionCompleted
    /// </summary>
    public class InspectionEngine : IAsyncDisposable, IDisposable
    {
        #region 字段

        private readonly ILogger<InspectionEngine> _logger;
        private readonly IPlcDevice _plcDevice;
        private readonly GwInstekGDM9060Driver _dmmDriver;
        private readonly HoneywellH1900Scanner? _scanner;

        private readonly SemaphoreSlim _engineLock = new(1, 1);
        private CancellationTokenSource? _inspectionCts;

        private volatile bool _isRunning;
        private volatile bool _isDisposed;
        private string? _currentInspectionId;

        /// <summary>
        /// PLC 地址映射（由 Models.PLC动作控制.PlcAddressMap 提供，保持现有默认值）
        /// 后续可以从配置文件读取以支持地址自定义
        /// </summary>
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
        /// 单步检测开始（在切换继电器之前触发，用于驱动 UI 显示"测试中"状态）
        /// 触发时机：确定当前测试点、输出日志之前
        /// ViewModel 收到此事件后，将对应行 CheckResult 更新为"测试中"
        /// </summary>
        public event EventHandler<StepStartedEventArgs>? StepStarted;

        /// <summary>
        /// 单步检测完成
        /// 触发时机：万用表测量完成、判定结果已生成之后
        /// ViewModel 收到此事件后，将对应行 CheckResult 更新为实际测量值、Judgment 更新为 OK/NG
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
            IPlcDevice plcDevice,
            GwInstekGDM9060Driver dmmDriver,
            HoneywellH1900Scanner? scanner = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
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
        /// 
        /// TODO: 当前由外部手动调用（如 ViewModel 通过某种信号触发）
        /// 实际部署时，此方法应由 PLC 启动信号触发：
        ///   1. PLC 通过 Modbus 轮询检测到启动信号（如 M0 线圈置位）
        ///   2. 上位机读取条码后调用 RunInspectionAsync
        ///   3. 具体触发路径待硬件联调时确认
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

                // 每次检测生成独立流水号，便于在日志中串起同一片产品的完整流程。
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

                try
                {
                    // === 步骤1：初始化 ===
                    SetState(InspectionState.Initializing);
                    LogInfo($"开始检测 - InspectionId:{inspectionId}, 条码:{barcode}, 机种:{modelName}, 作业员:{operatorName}, 测试点数:{_config.TestPoints.Count}");
                    await InitializeInspectionAsync(_inspectionCts.Token).ConfigureAwait(false);

                    // === 步骤2：逐点检测 ===
                    SetState(InspectionState.Testing);
                    int passCount = 0;
                    int failCount = 0;

                    for (int i = 0; i < _config.TestPoints.Count; i++)
                    {
                        _inspectionCts.Token.ThrowIfCancellationRequested();

                        var testPoint = _config.TestPoints[i];

                        // ═══════════════════════════════════════════════════════
                        // ⭐ 通知 ViewModel：当前项目即将开始检测
                        // 触发时机：在继电器切换之前，确保操作员第一时间看到"测试中"
                        // ViewModel 收到此事件后，将 TestItems[i].CheckResult 设为"测试中"
                        // ═══════════════════════════════════════════════════════
                        StepStarted?.Invoke(this, new StepStartedEventArgs(i, testPoint));

                        LogInfo($"正在检测 [{i + 1}/{_config.TestPoints.Count}] {testPoint.Name} ({testPoint.CheckMode}/{testPoint.ModeValue})");

                        // 2.1 切换继电器到当前测试点
                        await SwitchToTestPointAsync(i, _inspectionCts.Token).ConfigureAwait(false);

                        // 2.2 短暂延时等待继电器稳定
                        // 稳定时间由 InspectionConfig.RelaySettleTimeMs 控制，默认150ms
                        // 可根据实际硬件响应速度调整
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
                            var detail = testPoint.CheckMode == CheckModeConstants.Resistance
                                ? $" (范围:{testPoint.LowerLimit}~{testPoint.UpperLimit}Ω)"
                                : $" (期望:{testPoint.ModeValue})";
                            LogInfo($"  ❌ {testPoint.Name}: {measurement.Value:F4}Ω → NG{detail}");
                        }

                        // 2.5 将判定结果写入PLC
                        await WriteJudgmentToPlcAsync(i, judgment, _inspectionCts.Token).ConfigureAwait(false);

                        // 2.6 触发单步完成事件
                        // ViewModel 收到此事件后，将 CheckResult 设为实际测量值、Judgment 设为 OK/NG
                        StepCompleted?.Invoke(this, new StepCompletedEventArgs(i, testPoint, measurement));
                    }

                    // === 步骤3：完成 ===
                    result.TotalCount = _config.TestPoints.Count;
                    result.PassCount = passCount;
                    result.FailCount = failCount;
                    result.IsAllPassed = failCount == 0;
                    result.EndTime = DateTime.Now;

                    SetState(result.IsAllPassed ? InspectionState.CompletedPass : InspectionState.CompletedFail);
                    LogInfo($"检测完成 - InspectionId:{inspectionId}, 总数:{result.TotalCount}, 良品:{result.PassCount}, 不良:{result.FailCount}, 最终判定:{(result.IsAllPassed ? "OK" : "NG")}");

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
                    _logger.LogError(ex, "[检测流程][{InspectionId}] 检测流程异常 - 条码:{Barcode}, 机种:{ModelName}",
                        inspectionId, barcode, modelName);
                    SetState(InspectionState.Error);
                    result.ErrorMessage = ex.Message;

                    // 异常时写入PLC错误信号
                    await _plcDevice.SetErrorAsync(true).ConfigureAwait(false);
                }

                return result;
            }
            finally
            {
                _isRunning = false;
                _currentInspectionId = null;
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
            // 注意：StartFlag (M0) 是 PLC→PC 的输入信号，由 PLC 自行管理，上位机不写入
            await _plcDevice.SetOkAsync(false, ct).ConfigureAwait(false);
            await _plcDevice.SetNgAsync(false, ct).ConfigureAwait(false);
            await _plcDevice.SetErrorAsync(false, ct).ConfigureAwait(false);
            await _plcDevice.SetBusyAsync(true, ct).ConfigureAwait(false);

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
            await _plcDevice.SelectTestPointAsync(index, ct).ConfigureAwait(false);

            // 方案2：使用多个线圈控制继电器组
            if (testPoint.RelayChannel.HasValue)
            {
                // 先关闭所有继电器
                for (int i = 0; i < _config.TestPoints.Count; i++)
                {
                    if (_config.TestPoints[i].RelayChannel.HasValue)
                    {
                        await _plcDevice.SetRelayAsync(_config.TestPoints[i].RelayChannel!.Value, false, ct).ConfigureAwait(false);
                    }
                }

                // 再打开目标继电器
                await _plcDevice.SetRelayAsync(testPoint.RelayChannel.Value, true, ct).ConfigureAwait(false);
            }

            _logger.LogDebug("已切换到测试点: {TestPointName} (通道:{Channel})",
                testPoint.Name, testPoint.RelayChannel);
        }

        /// <summary>
        /// 判定测量结果
        ///
        /// 判定逻辑（方案需求变动）：
        ///   导通模式：
        ///     - ModeValue="OPEN"（期望开路）：实测值 > 1MΩ → OK，否则 NG
        ///     - ModeValue="SHORT"（期望短路）：实测值 < 1Ω → OK，否则 NG
        ///   电阻值模式：
        ///     - LowerLimit ≤ 实测值 ≤ UpperLimit → OK，否则 NG
        /// ★ CheckMode 比较使用中文常量 CheckModeConstants
        /// 
        /// 阈值说明：
        ///   导通判定的 1Ω/1MΩ 阈值基于通用工业标准，可根据实际需要调整
        /// </summary>
        private string JudgeResult(MeasurementResult measurement, TestPointConfig testPoint)
        {
            if (!measurement.IsValid)
            {
                _logger.LogWarning("[检测流程][{InspectionId}] 测量值无效: {ErrorMessage}",
                    _currentInspectionId ?? "-", measurement.ErrorMessage);
                return "NG";
            }

            double value = measurement.Value;

            // ★ 使用中文常量比较
            if (testPoint.CheckMode == CheckModeConstants.Resistance)
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
                if (testPoint.ModeValue == "SHORT")
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
            await _plcDevice.WriteTestResultAsync(index, value, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 写入最终结果到PLC
        /// </summary>
        private async Task WriteFinalResultToPlcAsync(bool isAllPassed, CancellationToken ct)
        {
            await _plcDevice.SetBusyAsync(false, ct).ConfigureAwait(false);

            if (isAllPassed)
            {
                await _plcDevice.SetOkAsync(true, ct).ConfigureAwait(false);
                await _plcDevice.SetNgAsync(false, ct).ConfigureAwait(false);
            }
            else
            {
                await _plcDevice.SetOkAsync(false, ct).ConfigureAwait(false);
                await _plcDevice.SetNgAsync(true, ct).ConfigureAwait(false);
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
            _logger.LogDebug("[检测流程][{InspectionId}] 状态变更: {OldState} -> {NewState}",
                _currentInspectionId ?? "-", oldState, newState);
            StateChanged?.Invoke(this, new InspectionStateChangedEventArgs(oldState, newState));
        }

        private void LogInfo(string message)
        {
            if (string.IsNullOrWhiteSpace(_currentInspectionId))
            {
                _logger.LogInformation("[检测流程] {Message}", message);
            }
            else
            {
                _logger.LogInformation("[检测流程][{InspectionId}] {Message}", _currentInspectionId, message);
            }

            LogMessage?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");
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
        /// 默认150ms，可根据实际继电器响应速度调整
        /// 设置过短可能导致测量时继电器未完全闭合，设置过长会影响检测节拍
        /// </summary>
        public int RelaySettleTimeMs { get; set; } = 150;
    }

    /// <summary>
    /// 测试点配置
    /// 每个测试点对应方案中的一个 PlanItem
    ///
    /// 改动说明（方案需求变动）：
    /// 新增 CheckMode / LowerLimit / UpperLimit / ModeValue 字段
    /// 新增左右引脚极性和 PLC 编码字段；当前先随检测配置流转，待电气地址表确认后再写入 PLC。
    /// 判定逻辑从全局阈值改为每项独立阈值
    /// </summary>
    public class TestPointConfig
    {
        /// <summary>项目名称（如 "A4-A5"）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>左引脚名称（如 A1）</summary>
        public string PinLeft { get; set; } = string.Empty;

        /// <summary>右引脚名称（如 A2）</summary>
        public string PinRight { get; set; } = string.Empty;

        /// <summary>
        /// 左引脚 PLC 编码。
        /// 编码规则：A1-A20 = 1-20，B1-B20 = 21-40。
        /// </summary>
        public ushort PinLeftCode { get; set; }

        /// <summary>
        /// 右引脚 PLC 编码。
        /// 编码规则：A1-A20 = 1-20，B1-B20 = 21-40。
        /// </summary>
        public ushort PinRightCode { get; set; }

        /// <summary>左引脚极性（正极/负极）</summary>
        public string PinLeftPolarity { get; set; } = PinPolarityConstants.Positive;

        /// <summary>右引脚极性（正极/负极）</summary>
        public string PinRightPolarity { get; set; } = PinPolarityConstants.Negative;

        /// <summary>
        /// 左引脚极性 PLC 编码。
        /// 编码规则：正极=1，负极=0。
        /// </summary>
        public ushort PinLeftPolarityCode { get; set; } = 1;

        /// <summary>
        /// 右引脚极性 PLC 编码。
        /// 编码规则：正极=1，负极=0。
        /// </summary>
        public ushort PinRightPolarityCode { get; set; }

        /// <summary>
        /// 检测方式（中文存储，与 CheckModeConstants 保持一致）
        /// "导通" — 导通检测，"电阻值" — 电阻值检测
        /// </summary>
        public string CheckMode { get; set; } = CheckModeConstants.Continuity;

        /// <summary>电阻值下限（Ω），导通模式为 null</summary>
        public double? LowerLimit { get; set; }

        /// <summary>电阻值上限（Ω），导通模式为 null</summary>
        public double? UpperLimit { get; set; }

        /// <summary>
        /// 模式值（替代旧字段 Unit）
        /// 导通模式： "OPEN"（期望开路）或 "SHORT"（期望短路）
        /// 电阻值模式： 万用表实际测量值（运行时由检测引擎填充）
        /// </summary>
        public string? ModeValue { get; set; } = "OPEN";

        /// <summary>继电器通道号</summary>
        public int? RelayChannel { get; set; }

        // 运行时填充
        public double ActualValue { get; set; }
        public string Judgment { get; set; } = string.Empty;
        public bool IsTested { get; set; }

        /// <summary>
        /// 从方案项目创建检测配置。
        /// 这里集中完成引脚拆分和 PLC 编码，避免 UI 层散落硬件编码规则。
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

        /// <summary>
        /// 拆分方案项目名。项目名仍保持 A1-A2 形式，不把正负极拼入名称。
        /// </summary>
        private static (string Left, string Right) SplitItemName(string itemName)
        {
            var parts = (itemName ?? string.Empty).Split('-', 2);
            var left = parts.Length > 0 ? parts[0].Trim() : string.Empty;
            var right = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            return (left, right);
        }

        /// <summary>
        /// 将 A/B 引脚转换为 PLC 数值编码。
        /// A1-A20 = 1-20，B1-B20 = 21-40；异常格式返回 0，便于后续 PLC 写入前识别无效值。
        /// </summary>
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
        /// <summary>
        /// 单次检测流水号，用于把检测开始、逐点测量、PLC写入和最终结果日志串联起来。
        /// </summary>
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

    /// <summary>
    /// 检测状态变更事件参数
    /// </summary>
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

    /// <summary>
    /// 单步检测开始事件参数
    /// 在 InspectionEngine 切换到指定测试点、开始测量之前触发
    /// ViewModel 收到此事件后，将对应行 CheckResult 更新为"测试中"
    /// </summary>
    public class StepStartedEventArgs : EventArgs
    {
        /// <summary>
        /// 当前步骤索引（0-based，对应 TestItems 集合的索引）
        /// 用于 ViewModel 定位 DataGrid 中的目标行
        /// </summary>
        public int StepIndex { get; }

        /// <summary>
        /// 即将检测的测试点配置（包含项目名称、检测方式、期望值等信息）
        /// </summary>
        public TestPointConfig TestPoint { get; }

        /// <summary>
        /// 构造单步检测开始事件参数
        /// </summary>
        /// <param name="stepIndex">当前步骤索引（0-based）</param>
        /// <param name="testPoint">即将检测的测试点配置</param>
        /// <exception cref="ArgumentNullException">testPoint 为 null 时抛出</exception>
        public StepStartedEventArgs(int stepIndex, TestPointConfig testPoint)
        {
            StepIndex = stepIndex;
            TestPoint = testPoint ?? throw new ArgumentNullException(nameof(testPoint));
        }
    }

    /// <summary>
    /// 单步检测完成事件参数
    /// 在万用表测量完成、判定结果生成后触发
    /// ViewModel 收到此事件后，将 CheckResult 设为实际测量值、Judgment 设为 OK/NG
    /// </summary>
    public class StepCompletedEventArgs : EventArgs
    {
        /// <summary>
        /// 当前步骤索引（0-based）
        /// </summary>
        public int StepIndex { get; }

        /// <summary>
        /// 已完成的测试点配置（包含判定结果 Judgment）
        /// </summary>
        public TestPointConfig TestPoint { get; }

        /// <summary>
        /// 万用表测量结果（包含实际测量值、有效性等信息）
        /// </summary>
        public MeasurementResult Measurement { get; }

        public StepCompletedEventArgs(int stepIndex, TestPointConfig testPoint, MeasurementResult measurement)
        {
            StepIndex = stepIndex;
            TestPoint = testPoint;
            Measurement = measurement;
        }
    }

    /// <summary>
    /// 全部检测完成事件参数
    /// </summary>
    public class InspectionCompletedEventArgs : EventArgs
    {
        /// <summary>
        /// 完整的检测结果（包含所有统计数据）
        /// </summary>
        public InspectionResult Result { get; }

        public InspectionCompletedEventArgs(InspectionResult result)
        {
            Result = result;
        }
    }

    #endregion
}
