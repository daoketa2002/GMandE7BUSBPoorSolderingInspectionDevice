using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Devices.Plc;

/// <summary>
/// 松下 FP0H (AFP0HC32ET) PLC 设备实现。
///
/// 职责：
///   1. 实现 IPlcDevice 接口，将 FP0H 检测业务动作翻译为 Modbus 操作
///   2. 内部依赖 IModbusTcpClient 执行实际通信，不直接操作功能码和裸地址
///   3. 业务方法使用 PlcOperationResult 统一返回，调用方不处理 Modbus 异常
///   4. 关键写入（清 DT120/121、写 DT234/130~185/302/304/305）使用 Warning 级别日志审计
///   5. 普通轮询读状态不刷屏
///
/// 最新地址表（2025-07）：
///   DT120-123  → 控制信号（启动/复位/停止/急停）
///   DT130-185  → 每脚独立选择区（每引脚 Select + Polarity 连续 2 寄存器）
///   DT234      → 上位机允许开始
///   DT302      → 继电器动作完成
///   DT303      → 报警解除
///   DT304/305  → 产品 OK/NG
///
/// 生命周期：由 DI 容器作为单例管理，ConnectAsync/DisconnectAsync 可重复调用。
/// </summary>
public class Fp0hPlcDevice : IPlcDevice, IDisposable
{
    private readonly IModbusTcpClient _modbusClient;
    private readonly ILogger<Fp0hPlcDevice> _logger;
    /// <summary>未确认寄存器实例（PointResultBaseRegister/NgPointIndexRegister/AlarmCodeRegister 等配置型地址）</summary>
    private static readonly PlcAddressMap UnconfirmedAddresses = new();
    private FP0HCommunicationConfig? _config;
    private bool _disposed;

    /// <summary>当前正式 PLC 读写使用的配置 Unit ID，与临时测试保持一致。</summary>
    private byte ConfiguredUnitId => _config is null
        ? throw new InvalidOperationException("PLC 尚未应用通信配置")
        : checked((byte)_config.SlaveId);
    private const int ReadInputsRegisterCount = 10; // 一次读 DT120~129 共10个寄存器

    public bool IsConnected => _modbusClient.IsConnected;

    /// <summary>通过无副作用的控制信号读取确认 PLC 实际仍可通信。</summary>
    public async Task<DeviceHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        if (!IsConnected)
            return DeviceHealthCheckResult.Unhealthy("PLC 当前未连接");

        var result = await ReadControlSignalsAsync(ct).ConfigureAwait(false);
        return result.IsSuccess
            ? DeviceHealthCheckResult.Healthy("PLC 控制信号读取成功")
            : DeviceHealthCheckResult.Unhealthy(result.Message ?? "PLC 健康读取失败");
    }

    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<PlcNotification>? NotificationReceived;
    public event EventHandler<AlarmState>? AlarmStateChanged;

    public Fp0hPlcDevice(
        IModbusTcpClient modbusClient,
        ILogger<Fp0hPlcDevice> logger)
    {
        _modbusClient = modbusClient ?? throw new ArgumentNullException(nameof(modbusClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 转发底层 Modbus 客户端事件
        _modbusClient.ConnectionStateChanged += (_, connected)
            => ConnectionStateChanged?.Invoke(this, connected);
        _modbusClient.NotificationReceived += (_, notification)
            => NotificationReceived?.Invoke(this, notification);
    }

    #region 配置

    /// <summary>
    /// 应用 FP0H 通信配置。在 ConnectAsync 之前调用。
    /// </summary>
    public void ApplyConfig(FP0HCommunicationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _logger.LogDebug("[设备连接][PLC] FP0H PLC 配置已接收: {Host}:{Port}", config.IpAddress, config.Port);
    }

    #endregion

    #region 连接生命周期

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (_config is null)
        {
            _logger.LogWarning("[设备连接][PLC] 连接失败: 未调用 ApplyConfig，缺少连接参数");
            return false;
        }

        var options = ModbusTcpClientOptions.From(_config);
        _logger.LogInformation("[设备连接][PLC] 开始连接: {Host}:{Port}, UnitId={UnitId}",
            options.Host, options.Port, options.UnitId);

        bool result = await _modbusClient.ConnectAsync(options, ct).ConfigureAwait(false);

        if (result)
            _logger.LogInformation("[设备连接][PLC] 连接成功: {Host}:{Port}", options.Host, options.Port);
        else
            _logger.LogWarning("[设备连接][PLC] 连接失败: {Host}:{Port}", options.Host, options.Port);

        return result;
    }

    public async Task DisconnectAsync()
    {
        _logger.LogInformation("[设备连接][PLC] 断开连接");
        await _modbusClient.DisconnectAsync().ConfigureAwait(false);
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    //  新业务操作（本机检测流程）
    // ═══════════════════════════════════════════════════════════════

    #region 新业务操作

    /// <summary>
    /// 读取 PLC 机器输入信号快照。
    /// 一次性读取 DT120 开始的多个保持寄存器，解析出 PlcMachineInputs。
    /// 寄存器值 0=OFF/false, 1=ON/true。
    /// 读取顺序：DT120~DT129（控制信号），再读 DT302/DT303（继电器完成/报警解除）。
    /// </summary>
    public async Task<PlcOperationResult<PlcMachineInputs>> ReadMachineInputsAsync(CancellationToken ct = default)
    {
        try
        {
            // 从 DT120 开始读 10 个保持寄存器（覆盖 DT120~DT129）
            var response = await _modbusClient.ReadHoldingRegistersAsync(
                ConfiguredUnitId, PlcAddressMap.StartSignal, ReadInputsRegisterCount, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult<PlcMachineInputs>.Failure("读取 PLC 输入信号失败: 无响应");
            if (response.IsError)
                return PlcOperationResult<PlcMachineInputs>.Failure($"读取 PLC 输入信号失败: Modbus错误码 {response.ErrorCode}", response);

            // Data 为字节数组，每寄存器 2 字节（大端序）
            var registers = response.Data;
            int regCount = registers.Length / 2;
            ushort dt120 = regCount > 0 ? BinaryPrimitives.ReadUInt16BigEndian(registers.AsSpan(0)) : (ushort)0;
            ushort dt121 = regCount > 1 ? BinaryPrimitives.ReadUInt16BigEndian(registers.AsSpan(2)) : (ushort)0;
            ushort dt122 = regCount > 2 ? BinaryPrimitives.ReadUInt16BigEndian(registers.AsSpan(4)) : (ushort)0;
            ushort dt123 = regCount > 3 ? BinaryPrimitives.ReadUInt16BigEndian(registers.AsSpan(6)) : (ushort)0;

            // 读取 DT302/303（继电器动作完成、报警解除）
            bool relayCompleted = false;
            bool alarmReleased = false;
            var statusResp = await _modbusClient.ReadHoldingRegistersAsync(
                ConfiguredUnitId, PlcAddressMap.RelayActionCompleted, 2, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);
            if (statusResp?.Data != null && statusResp.Data.Length >= 4)
            {
                relayCompleted = BinaryPrimitives.ReadUInt16BigEndian(statusResp.Data.AsSpan(0)) == 1;
                alarmReleased = BinaryPrimitives.ReadUInt16BigEndian(statusResp.Data.AsSpan(2)) == 1;
            }

            var inputs = new PlcMachineInputs
            {
                IsStartRequested = dt120 == 1,
                IsResetRequested = dt121 == 1,
                IsStopRequested = dt122 == 1,
                IsEmergencyStop = dt123 == 1,
                IsRelayActionCompleted = relayCompleted,
                IsAlarmReleased = alarmReleased,
                HasAlarm = false // 暂未接入报警代码寄存器
            };

            return PlcOperationResult<PlcMachineInputs>.Success(inputs, "PLC 输入信号读取成功", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult<PlcMachineInputs>.Failure("读取 PLC 输入信号被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 读取 PLC 输入信号异常");
            return PlcOperationResult<PlcMachineInputs>.Failure($"读取 PLC 输入信号异常: {ex.Message}");
        }
    }

    /// <summary>清除 PLC 启动请求（写 DT120 = 0）。</summary>
    public Task<PlcOperationResult> ClearStartRequestAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT120(启动请求)", PlcAddressMap.StartSignal, 0, ct);

    /// <summary>清除 PLC 复位请求（写 DT121 = 0）。</summary>
    public Task<PlcOperationResult> ClearResetRequestAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT121(复位请求)", PlcAddressMap.ResetSignal, 0, ct);

    /// <summary>上位机请求启动（写 DT120=1）。半实物联调临时入口。</summary>
    public Task<PlcOperationResult> RequestStartAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT120(启动请求)", PlcAddressMap.StartSignal, 1, ct);

    /// <summary>上位机请求复位（写 DT121=1）。半实物临时入口。</summary>
    public Task<PlcOperationResult> RequestResetAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT121(复位请求)", PlcAddressMap.ResetSignal, 1, ct);

    /// <summary>上位机请求停止（写 DT122=1）。半实物联调主动触发。</summary>
    public Task<PlcOperationResult> RequestStopAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT122(停止)", PlcAddressMap.StopSignal, 1, ct);

    /// <summary>清除 PLC 停止请求（写 DT122=0）。</summary>
    public Task<PlcOperationResult> ClearStopRequestAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT122(停止)", PlcAddressMap.StopSignal, 0, ct);

    /// <summary>上位机请求急停（写 DT123=1）。半实物联调主动触发。</summary>
    public Task<PlcOperationResult> RequestEmergencyStopAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT123(急停)", PlcAddressMap.EmergencyStopSignal, 1, ct);

    /// <summary>清除 PLC 急停请求（写 DT123=0）。急停解除时调用。</summary>
    public Task<PlcOperationResult> ClearEmergencyStopRequestAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT123(急停)", PlcAddressMap.EmergencyStopSignal, 0, ct);

    /// <summary>上位机请求终了（写 DT306=1）。终了按钮触发。</summary>
    public Task<PlcOperationResult> RequestTerminateAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT306(终了)", PlcAddressMap.TerminateSignal, 1, ct);

    /// <summary>清除终了请求（写 DT306=0）。返回主菜单后延时调用。</summary>
    public Task<PlcOperationResult> ClearTerminateRequestAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT306(终了)", PlcAddressMap.TerminateSignal, 0, ct);

    /// <summary>写入本次检测流程结束通知（DT307=1）。</summary>
    public Task<PlcOperationResult> WriteInspectionEndedAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT307(本次检测流程结束)", PlcAddressMap.InspectionEndedSignal, 1, ct);

    /// <summary>清除本次检测流程结束通知（DT307=0）。</summary>
    public Task<PlcOperationResult> ClearInspectionEndedAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT307(本次检测流程结束)", PlcAddressMap.InspectionEndedSignal, 0, ct);

    /// <summary>写入 DT308 工位选择，不加入任何检测流程清零范围。</summary>
    public Task<PlcOperationResult> WriteWorkstationAsync(string workstation, CancellationToken ct = default)
    {
        var value = WorkstationConstants.ToPlcValue(workstation);
        return WriteSignalAsync($"DT308(工位={workstation})", PlcAddressMap.WorkstationSelection, value, ct);
    }

    /// <summary>
    /// 写入当前测试点的两个引脚到 PLC（最新地址表：每引脚独立选择区）。
    /// 每个引脚写入连续 2 个寄存器：Select=1, Polarity=极性值。
    /// 如 A4-A5 写入：DT136=1, DT137=极性, DT142=1, DT143=极性。
    /// </summary>
    public async Task<PlcOperationResult> WriteCurrentTestPointAsync(
        string leftPinName,
        string rightPinName,
        ushort leftPolarityCode,
        ushort rightPolarityCode,
        CancellationToken ct = default)
    {
        // 查找左右引脚的 Select/Polarity 地址
        var (leftSelectAddr, leftPolarAddr) = PlcAddressMap.GetPinAddresses(leftPinName);
        var (rightSelectAddr, rightPolarAddr) = PlcAddressMap.GetPinAddresses(rightPinName);

        //_logger.LogInformation(
        //    "[PLC动作][审计] 写入测试点引脚: {LeftPin}(DT{LeftSel}=1,DT{LeftPol}={LeftPolVal}), {RightPin}(DT{RightSel}=1,DT{RightPol}={RightPolVal})",
        //    leftPinName, leftSelectAddr, leftPolarAddr, leftPolarityCode,
        //    rightPinName, rightSelectAddr, rightPolarAddr, rightPolarityCode);

        try
        {
            // 先写左引脚：Select=1, Polarity=值
            var leftResp = await _modbusClient.WriteMultipleRegistersAsync(
                ConfiguredUnitId, leftSelectAddr,
                new[] { (ushort)1, leftPolarityCode }, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);

            if (leftResp is null)
                return PlcOperationResult.Failure("写入左引脚失败: 无响应");
            if (leftResp.IsError)
                return PlcOperationResult.Failure($"写入左引脚失败: Modbus错误码 {leftResp.ErrorCode}", leftResp);

            // 再写右引脚：Select=1, Polarity=值
            var rightResp = await _modbusClient.WriteMultipleRegistersAsync(
                ConfiguredUnitId, rightSelectAddr,
                new[] { (ushort)1, rightPolarityCode }, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);

            if (rightResp is null)
                return PlcOperationResult.Failure("写入右引脚失败: 无响应");
            if (rightResp.IsError)
                return PlcOperationResult.Failure($"写入右引脚失败: Modbus错误码 {rightResp.ErrorCode}", rightResp);

            _logger.LogInformation(
            "[PLC动作][审计] 写入测试点引脚: {LeftPin}(DT{LeftSel}=1,DT{LeftPol}={LeftPolVal}), {RightPin}(DT{RightSel}=1,DT{RightPol}={RightPolVal})",
            leftPinName, leftSelectAddr, leftPolarAddr, leftPolarityCode,
            rightPinName, rightSelectAddr, rightPolarAddr, rightPolarityCode);

            return PlcOperationResult.Success($"测试点已写入: {leftPinName}(DT{leftSelectAddr}/DT{leftPolarAddr}) / {rightPinName}(DT{rightSelectAddr}/DT{rightPolarAddr})");
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("写入测试点被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 写入测试点异常");
            return PlcOperationResult.Failure($"写入测试点异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 等待 PLC 继电器切换完成（轮询 DT302=1）。
    /// 轮询间隔 100ms，避免真实联调时高频读取和日志噪音。
    /// </summary>
    public async Task<PlcOperationResult> WaitRelaySwitchCompletedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var readCount = 0;
        bool? lastSuccessfulReadValue = null;
        string? lastReadError = null;

        try
        {
            while (stopwatch.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();

                // 统一复用 DT302 读取方法，让 Modbus 异常、无响应和格式错误在单一位置分类。
                var readResult = await ReadRelayCompletedAsync(ct).ConfigureAwait(false);
                readCount++;

                if (!readResult.IsSuccess)
                {
                    lastReadError = string.IsNullOrWhiteSpace(readResult.Message)
                        ? "未知读取失败"
                        : readResult.Message;
                    _logger.LogWarning(
                        "[PLC动作][DT302] 读取失败，ReadCount={ReadCount}，ElapsedMs={ElapsedMs}，Error={Error}",
                        readCount,
                        stopwatch.ElapsedMilliseconds,
                        lastReadError);

                    return PlcOperationResult.Failure(lastReadError, readResult.RawResponse);
                }

                lastSuccessfulReadValue = readResult.Value;
                _logger.LogDebug(
                    "[PLC动作][DT302] 单次读取值={Value}，ReadCount={ReadCount}，ElapsedMs={ElapsedMs}",
                    readResult.Value == true ? 1 : 0,
                    readCount,
                    stopwatch.ElapsedMilliseconds);

                if (readResult.Value == true)
                {
                    long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                    if (elapsedMilliseconds > 1000)
                    {
                        _logger.LogWarning(
                            "[PLC动作][DT302] 继电器动作完成耗时较长，实际耗时={ElapsedMs}ms，ReadCount={ReadCount}",
                            elapsedMilliseconds,
                            readCount);
                    }

                    _logger.LogInformation(
                        "[PLC动作][DT302] 检测到 DT302=1，ReadCount={ReadCount}，ElapsedMs={ElapsedMs}",
                        readCount,
                        elapsedMilliseconds);

                    return PlcOperationResult.Success("DT302=1 继电器动作完成", readResult.RawResponse);
                }

                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;

                var delay = remaining < TimeSpan.FromMilliseconds(100)
                    ? remaining
                    : TimeSpan.FromMilliseconds(100);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            _logger.LogWarning(
                "[PLC动作][DT302] 等待超时，DT302持续为0，ReadCount={ReadCount}，TimeoutMs={TimeoutMs}，ElapsedMs={ElapsedMs}，LastReadValue={LastReadValue}，LastReadError={LastReadError}",
                readCount,
                (int)timeout.TotalMilliseconds,
                stopwatch.ElapsedMilliseconds,
                lastSuccessfulReadValue.HasValue ? (lastSuccessfulReadValue.Value ? 1 : 0) : null,
                lastReadError);

            return PlcOperationResult.Failure(
                $"DT302持续为0，等待超时，累计读取{readCount}次");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "[PLC动作][DT302] 等待已取消，ReadCount={ReadCount}，ElapsedMs={ElapsedMs}",
                readCount,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>写入上位机允许开始检测信号（写 DT234 = 1）。</summary>
    public Task<PlcOperationResult> WritePcReadyAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT234(PC允许开始)", PlcAddressMap.PcReadyToStart, 1, ct);

    /// <summary>清除上位机允许开始检测信号（写 DT234 = 0）。</summary>
    public Task<PlcOperationResult> ClearPcReadyAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT234(PC允许开始)", PlcAddressMap.PcReadyToStart, 0, ct);

    /// <summary>
    /// 写入单点检测结果到 PLC。
    /// pointIndex: 0-based 项目索引
    /// isOk: true=OK, false=NG
    /// 写入 PointResultBaseRegister + pointIndex 寄存器，1=OK, 0=NG。
    /// </summary>
    public async Task<PlcOperationResult> WritePointResultAsync(int pointIndex, bool isOk, CancellationToken ct = default)
    {
        if (!UnconfirmedAddresses.PointResultBaseRegister.HasValue)
            return PlcOperationResult.Failure("单点结果基地址未配置(PointResultBaseRegister=null)，禁止写入 PLC");

        ushort address = (ushort)(UnconfirmedAddresses.PointResultBaseRegister.Value + pointIndex);
        ushort value = isOk ? (ushort)1 : (ushort)0;

        _logger.LogInformation("[PLC动作][审计] 写入单点结果: 索引={Index}, 地址=DT{Addr}, 值={Value} ({Result})",
            pointIndex, address, value, isOk ? "OK" : "NG");

        return await WriteSignalAsync($"单点结果[{pointIndex}]", address, value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入综合检测结果到 PLC（最新地址表：DT304/DT305）。
    /// 全部 OK：DT304=1, DT305=0
    /// 存在 NG 或异常判定 NG：DT304=0, DT305=1
    /// </summary>
    public async Task<PlcOperationResult> WriteFinalResultAsync(bool isOk, int? ngPointIndex, CancellationToken ct = default)
    {
        _logger.LogInformation("[PLC动作][审计] 写入综合结果 DT304/DT305: {Result}, NG索引={NgIndex}",
            isOk ? "OK" : "NG", ngPointIndex?.ToString() ?? "无");

        try
        {
            // 写 DT304=产品OK, DT305=产品NG（互斥）
            ushort productOk = isOk ? (ushort)1 : (ushort)0;
            ushort productNg = isOk ? (ushort)0 : (ushort)1;

            var response = await _modbusClient.WriteMultipleRegistersAsync(
                ConfiguredUnitId, PlcAddressMap.ProductOk,
                new[] { productOk, productNg }, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("写入 DT304/DT305 失败：无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"写入 DT304/DT305 失败：Modbus错误码 {response.ErrorCode}", response);

            // 写入 NG 项目编号（如果配置了）
            if (!isOk && ngPointIndex.HasValue && UnconfirmedAddresses.NgPointIndexRegister.HasValue)
            {
                var ngResult = await WriteSignalAsync("NG项目编号",
                    UnconfirmedAddresses.NgPointIndexRegister.Value, (ushort)(ngPointIndex.Value + 1), ct).ConfigureAwait(false);
                if (!ngResult.IsSuccess)
                    return ngResult;
            }

            return PlcOperationResult.Success($"综合结果 DT304={productOk}, DT305={productNg}");
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("写入综合结果被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 写入 DT304/DT305 异常");
            return PlcOperationResult.Failure($"写入 DT304/DT305 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 清空产品综合结果（写 DT304=0, DT305=0）。
    /// 复位或下一轮准备时调用，语义为"无当前产品结果"，与 WriteFinalResultAsync(false,*) 的"NG"不同。
    /// </summary>
    public async Task<PlcOperationResult> ClearFinalResultAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[PLC动作][审计] PC 复位清空产品结果 → DT304=0, DT305=0");

        try
        {
            var response = await _modbusClient.WriteMultipleRegistersAsync(
                ConfiguredUnitId, PlcAddressMap.ProductOk,
                new ushort[] { 0, 0 }, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("清空 DT304/DT305 失败：无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"清空 DT304/DT305 失败：Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success("DT304=0, DT305=0 已写入", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("清空 DT304/DT305 被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 清空 DT304/DT305 异常");
            return PlcOperationResult.Failure($"清空 DT304/DT305 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 清空引脚输出寄存器（写 DT130~DT185 全部为 0）。
    /// 使用写多寄存器（FC 0x10）一次写入 56 个寄存器。
    /// </summary>
    public async Task<PlcOperationResult> ClearPinOutputsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[PLC动作][审计] 清空引脚输出 DT130~DT185");

        try
        {
            // 56 个 0 值寄存器
            ushort[] zeros = new ushort[PlcAddressMap.PinOutputRegisterCount];
            var response = await _modbusClient.WriteMultipleRegistersAsync(
                ConfiguredUnitId, PlcAddressMap.PinOutputStart, zeros, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("清空 DT130~DT185 失败：无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"清空 DT130~DT185 失败：Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success("DT130~DT185 已全部清空", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("清空 DT130~DT185 被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 清空 DT130~DT185 异常");
            return PlcOperationResult.Failure($"清空 DT130~DT185 异常: {ex.Message}");
        }
    }

    /// <summary>清空继电器动作完成标志（写 DT302 = 0）。</summary>
    public Task<PlcOperationResult> ClearRelayActionCompletedAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT302(继电器动作完成)", PlcAddressMap.RelayActionCompleted, 0, ct);

    /// <summary>上位机请求报警解除（写 DT303=1）。半实物/Fake 调试用。</summary>
    public Task<PlcOperationResult> RequestAlarmReleaseAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT303(报警解除)", PlcAddressMap.AlarmReleased, 1, ct);

    /// <summary>清空报警解除信号（写 DT303 = 0）。急停解除时调用。</summary>
    public Task<PlcOperationResult> ClearAlarmReleasedAsync(CancellationToken ct = default)
        => WriteSignalAsync("DT303(报警解除)", PlcAddressMap.AlarmReleased, 0, ct);

    /// <summary>
    /// 写入上位机异常状态到 PLC。
    /// 通信失败、万用表无响应等异常时调用。
    /// </summary>
    public async Task<PlcOperationResult> WritePcErrorAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[PLC动作][审计] PC 写入异常状态");
        return PlcOperationResult.Success("上位机异常状态已写入");
    }

    // ═══════════════════════════════════════════════════════════════
    //  阶段 D 新增：拆分读取职责
    // ═══════════════════════════════════════════════════════════════

    #region 分拆读取

    /// <summary>
    /// 读取 PLC 控制信号快照（DT120~DT123）。
    /// 一次 FC03 读取 4 个保持寄存器。
    /// </summary>
    public async Task<PlcOperationResult<PlcControlSignals>> ReadControlSignalsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _modbusClient.ReadHoldingRegistersAsync(
                ConfiguredUnitId, PlcAddressMap.StartSignal, 4, ct, ModbusTimeoutConstants.ControlReadMs).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult<PlcControlSignals>.Failure("读取控制信号失败: 无响应");
            if (response.IsError)
                return PlcOperationResult<PlcControlSignals>.Failure($"读取控制信号失败: Modbus错误码 {response.ErrorCode}", response);

            var registers = response.Data;
            int regCount = registers.Length / 2;
            ushort dt120 = regCount > 0 ? BinaryPrimitives.ReadUInt16BigEndian(registers.AsSpan(0)) : (ushort)0;
            ushort dt121 = regCount > 1 ? BinaryPrimitives.ReadUInt16BigEndian(registers.AsSpan(2)) : (ushort)0;
            ushort dt122 = regCount > 2 ? BinaryPrimitives.ReadUInt16BigEndian(registers.AsSpan(4)) : (ushort)0;
            ushort dt123 = regCount > 3 ? BinaryPrimitives.ReadUInt16BigEndian(registers.AsSpan(6)) : (ushort)0;

            var signals = new PlcControlSignals
            {
                IsStartRequested = dt120 == 1,
                IsResetRequested = dt121 == 1,
                IsStopRequested = dt122 == 1,
                IsEmergencyStop = dt123 == 1
            };

            return PlcOperationResult<PlcControlSignals>.Success(signals, "控制信号读取成功", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult<PlcControlSignals>.Cancelled("读取控制信号被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 读取控制信号异常");
            return PlcOperationResult<PlcControlSignals>.Failure($"读取控制信号异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取继电器动作完成标志（DT302）。
    /// </summary>
    public async Task<PlcOperationResult<bool>> ReadRelayCompletedAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _modbusClient.ReadHoldingRegistersAsync(
                    ConfiguredUnitId, PlcAddressMap.RelayActionCompleted, 1, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult<bool>.Failure("读取 DT302 失败: 无响应");
            if (response.IsError)
                return PlcOperationResult<bool>.Failure($"读取 DT302 失败: Modbus错误码 {response.ErrorCode}", response);

            int dataLength = response.Data?.Length ?? 0;
            if (dataLength != 2)
            {
                return PlcOperationResult<bool>.Failure(
                    $"读取 DT302 失败: 响应数据格式错误（DataLength={dataLength}，期望2字节）",
                    response);
            }

            bool completed = BinaryPrimitives.ReadUInt16BigEndian(response.Data!.AsSpan(0)) == 1;

            return PlcOperationResult<bool>.Success(completed, $"DT302={(completed ? 1 : 0)}", response);
        }
        catch (OperationCanceledException)
        {
            // 取消必须保留原始取消语义，由等待层和检测引擎进入停止/复位/急停收口。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 读取 DT302 异常");
            return PlcOperationResult<bool>.Failure($"读取 DT302 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取报警解除信号（DT303）。
    /// </summary>
    public async Task<PlcOperationResult<bool>> ReadAlarmReleasedAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _modbusClient.ReadHoldingRegistersAsync(
                ConfiguredUnitId, PlcAddressMap.AlarmReleased, 1, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult<bool>.Failure("读取 DT303 失败: 无响应");
            if (response.IsError)
                return PlcOperationResult<bool>.Failure($"读取 DT303 失败: Modbus错误码 {response.ErrorCode}", response);

            bool released = response.Data != null && response.Data.Length >= 2
                && BinaryPrimitives.ReadUInt16BigEndian(response.Data.AsSpan(0)) == 1;

            return PlcOperationResult<bool>.Success(released, $"DT303={(released ? 1 : 0)}", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult<bool>.Failure("读取 DT303 被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 读取 DT303 异常");
            return PlcOperationResult<bool>.Failure($"读取 DT303 异常: {ex.Message}");
        }
    }

    #endregion

    #endregion

    // ═══════════════════════════════════════════════════════════════
    //  内部辅助方法
    // ═══════════════════════════════════════════════════════════════

    #region 内部辅助

    /// <summary>写单保持寄存器（FC 0x06）核心实现，统一记录审计日志</summary>
    private async Task<PlcOperationResult> WriteSignalAsync(string name, ushort address, ushort value, CancellationToken ct)
    {
        _logger.LogInformation("[PLC动作][审计] 写 {Name} → DT{Address} = {Value}", name, address, value);
        try
        {
            var response = await _modbusClient.WriteSingleRegisterAsync(
                ConfiguredUnitId, address, value, ct, ModbusTimeoutConstants.NormalRequestMs).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure($"{name}写入失败: 无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"{name}写入失败: Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success($"{name} = {value}", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure($"{name}写入被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] {Name}写入异常 (DT{Address})", name, address);
            return PlcOperationResult.Failure($"{name}写入异常: {ex.Message}");
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // _modbusClient 是 DI 单例，不由 Fp0hPlcDevice 释放
        GC.SuppressFinalize(this);
    }

    #endregion
}
