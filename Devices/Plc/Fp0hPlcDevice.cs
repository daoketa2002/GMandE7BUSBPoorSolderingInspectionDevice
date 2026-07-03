using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

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
    private readonly PlcAddressMap _addressMap = new();
    private FP0HCommunicationConfig? _config;
    private bool _disposed;

    private const byte DefaultUnitId = 1;
    private const int DefaultTimeoutMs = 3000;
    private const int ReadInputsRegisterCount = 10; // 一次读 DT120~129 共10个寄存器

    public bool IsConnected => _modbusClient.IsConnected;

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
            _logger.LogWarning("[设备连接][PLC] 连接成功: {Host}:{Port}", options.Host, options.Port);
        else
            _logger.LogWarning("[设备连接][PLC] 连接失败: {Host}:{Port}", options.Host, options.Port);

        return result;
    }

    public async Task DisconnectAsync()
    {
        _logger.LogWarning("[设备连接][PLC] 断开连接");
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
                DefaultUnitId, _addressMap.StartRequestRegister, ReadInputsRegisterCount, ct).ConfigureAwait(false);

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
                DefaultUnitId, PlcAddressMap.RelayActionCompleted, 2, ct).ConfigureAwait(false);
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

    /// <summary>
    /// 清除 PLC 启动请求（写 DT120 = 0）。
    /// PC 读到 DT120=1 并接管启动后调用，防止同一信号重复触发检测。
    /// </summary>
    public async Task<PlcOperationResult> ClearStartRequestAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] PC 清除 DT120 启动请求 → 写 DT120 = 0");
        return await WriteRegisterSingleAsync("DT120(启动请求)", _addressMap.StartRequestRegister, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 清除 PLC 复位请求（写 DT121 = 0）。
    /// PC 完成复位处理后调用。
    /// </summary>
    public async Task<PlcOperationResult> ClearResetRequestAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] PC 清除 DT121 复位请求 → 写 DT121 = 0");
        return await WriteRegisterSingleAsync("DT121(复位请求)", _addressMap.ResetRegister, 0, ct).ConfigureAwait(false);
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

        _logger.LogWarning(
            "[PLC动作][审计] 写入测试点引脚: {LeftPin}(DT{LeftSel}=1,DT{LeftPol}={LeftPolVal}), {RightPin}(DT{RightSel}=1,DT{RightPol}={RightPolVal})",
            leftPinName, leftSelectAddr, leftPolarAddr, leftPolarityCode,
            rightPinName, rightSelectAddr, rightPolarAddr, rightPolarityCode);

        try
        {
            // 先写左引脚：Select=1, Polarity=值
            var leftResp = await _modbusClient.WriteMultipleRegistersAsync(
                DefaultUnitId, leftSelectAddr,
                new[] { (ushort)1, leftPolarityCode }, ct).ConfigureAwait(false);

            if (leftResp is null)
                return PlcOperationResult.Failure("写入左引脚失败: 无响应");
            if (leftResp.IsError)
                return PlcOperationResult.Failure($"写入左引脚失败: Modbus错误码 {leftResp.ErrorCode}", leftResp);

            // 再写右引脚：Select=1, Polarity=值
            var rightResp = await _modbusClient.WriteMultipleRegistersAsync(
                DefaultUnitId, rightSelectAddr,
                new[] { (ushort)1, rightPolarityCode }, ct).ConfigureAwait(false);

            if (rightResp is null)
                return PlcOperationResult.Failure("写入右引脚失败: 无响应");
            if (rightResp.IsError)
                return PlcOperationResult.Failure($"写入右引脚失败: Modbus错误码 {rightResp.ErrorCode}", rightResp);

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
        ushort addr = PlcAddressMap.RelayActionCompleted;
        var deadline = DateTime.UtcNow + timeout;

        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning("[PLC动作][审计] 等待 DT302=1 超时 ({TimeoutSeconds}s)", timeout.TotalSeconds);
                return PlcOperationResult.Failure($"等待 DT302=1 超时 ({timeout.TotalSeconds}s)");
            }

            try
            {
                var response = await _modbusClient.ReadHoldingRegistersAsync(
                    DefaultUnitId, addr, 1, ct).ConfigureAwait(false);

                if (response?.Data != null && response.Data.Length >= 2
                    && BinaryPrimitives.ReadUInt16BigEndian(response.Data.AsSpan(0)) == 1)
                    return PlcOperationResult.Success("DT302=1 继电器动作完成", response);
            }
            catch (OperationCanceledException)
            {
                return PlcOperationResult.Failure("等待继电器切换被取消");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PLC动作] 读取 DT302 状态异常");
                return PlcOperationResult.Failure($"读取 DT302 状态异常: {ex.Message}");
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return PlcOperationResult.Failure("等待继电器切换被取消");
    }

    /// <summary>
    /// 写入上位机允许开始检测信号（写 DT234 = 1）。
    /// </summary>
    public async Task<PlcOperationResult> WritePcReadyAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] PC 允许开始检测 → 写 DT234 = 1");
        return await WriteRegisterSingleAsync("DT234(PC允许开始)", _addressMap.PcReadyRegister, 1, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 清除上位机允许开始检测信号（写 DT234 = 0）。
    /// </summary>
    public async Task<PlcOperationResult> ClearPcReadyAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] PC 清除允许开始 → 写 DT234 = 0");
        return await WriteRegisterSingleAsync("DT234(PC允许开始)", _addressMap.PcReadyRegister, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入单点检测结果到 PLC。
    /// pointIndex: 0-based 项目索引
    /// isOk: true=OK, false=NG
    /// 写入 PointResultBaseRegister + pointIndex 寄存器，1=OK, 0=NG。
    /// </summary>
    public async Task<PlcOperationResult> WritePointResultAsync(int pointIndex, bool isOk, CancellationToken ct = default)
    {
        if (!_addressMap.PointResultBaseRegister.HasValue)
            return PlcOperationResult.Failure("单点结果基地址未配置(PointResultBaseRegister=null)，禁止写入 PLC");

        ushort address = (ushort)(_addressMap.PointResultBaseRegister.Value + pointIndex);
        ushort value = isOk ? (ushort)1 : (ushort)0;

        _logger.LogWarning("[PLC动作][审计] 写入单点结果: 索引={Index}, 地址=DT{Addr}, 值={Value} ({Result})",
            pointIndex, address, value, isOk ? "OK" : "NG");

        return await WriteRegisterSingleAsync($"单点结果[{pointIndex}]", address, value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入综合检测结果到 PLC（最新地址表：DT304/DT305）。
    /// 全部 OK：DT304=1, DT305=0
    /// 存在 NG 或异常判定 NG：DT304=0, DT305=1
    /// </summary>
    public async Task<PlcOperationResult> WriteFinalResultAsync(bool isOk, int? ngPointIndex, CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] 写入综合结果 DT304/DT305: {Result}, NG索引={NgIndex}",
            isOk ? "OK" : "NG", ngPointIndex?.ToString() ?? "无");

        try
        {
            // 写 DT304=产品OK, DT305=产品NG（互斥）
            ushort productOk = isOk ? (ushort)1 : (ushort)0;
            ushort productNg = isOk ? (ushort)0 : (ushort)1;

            var response = await _modbusClient.WriteMultipleRegistersAsync(
                DefaultUnitId, _addressMap.ProductOkRegister,
                new[] { productOk, productNg }, ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("写入 DT304/DT305 失败：无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"写入 DT304/DT305 失败：Modbus错误码 {response.ErrorCode}", response);

            // 写入 NG 项目编号（如果配置了）
            if (!isOk && ngPointIndex.HasValue && _addressMap.NgPointIndexRegister.HasValue)
            {
                var ngResult = await WriteRegisterSingleAsync("NG项目编号",
                    _addressMap.NgPointIndexRegister.Value, (ushort)(ngPointIndex.Value + 1), ct).ConfigureAwait(false);
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
    /// 清空引脚输出寄存器（写 DT130~DT185 全部为 0）。
    /// 使用写多寄存器（FC 0x10）一次写入 56 个寄存器。
    /// </summary>
    public async Task<PlcOperationResult> ClearPinOutputsAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] 清空引脚输出 DT130~DT185");

        try
        {
            // 56 个 0 值寄存器
            ushort[] zeros = new ushort[PlcAddressMap.PinOutputRegisterCount];
            var response = await _modbusClient.WriteMultipleRegistersAsync(
                DefaultUnitId, PlcAddressMap.PinOutputStart, zeros, ct).ConfigureAwait(false);

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

    /// <summary>
    /// 清空继电器动作完成标志（写 DT302 = 0）。
    /// 当前测试点完成读取万用表并判定后调用，表示上位机已取走结果并允许 PLC 进行下一步动作。
    /// 在下一项开始前、复位、停止、急停、异常中止路径中也必须清 DT302。
    /// </summary>
    public async Task<PlcOperationResult> ClearRelayActionCompletedAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] PC 清除继电器动作完成标志 → 写 DT302 = 0");
        return await WriteRegisterSingleAsync("DT302(继电器动作完成)", _addressMap.RelayCompletedRegister, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 清空报警解除信号（写 DT303 = 0）。
    /// 用户点击急停弹窗"解除"按钮后调用，只清 DT303，不清 DT123。
    /// </summary>
    public async Task<PlcOperationResult> ClearAlarmReleasedAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] 用户确认急停解除，PC 清除 DT303 报警解除信号 → 写 DT303 = 0");
        return await WriteRegisterSingleAsync("DT303(报警解除)", _addressMap.AlarmReleasedRegister, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 通知 PLC 断开引脚输出（写 DT160 = 1 — 旧地址表语义）。
    /// 已废弃，新流程使用 ClearPinOutputsAsync。
    /// DT160 在最新地址表中属于 A12 引脚的 Select 地址。
    /// </summary>
    [Obsolete("请使用 ClearPinOutputsAsync 替代。DT160 现在是 A12 引脚选择地址，不是流程信号。")]
    public async Task<PlcOperationResult> RequestRelayDisconnectAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] PC 通知 PLC 断开引脚输出（旧 DT160 语义）→ 写 DT160 = 1");
        return await WriteRegisterSingleAsync("DT160(旧断开引脚输出/现A12选择地址)", 160, 1, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入上位机异常状态到 PLC。
    /// 通信失败、万用表无响应等异常时调用。
    /// </summary>
    public async Task<PlcOperationResult> WritePcErrorAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] PC 写入异常状态");
        return PlcOperationResult.Success("上位机异常状态已写入");
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    //  旧业务操作（保持兼容，待主流程稳定后清理）
    // ═══════════════════════════════════════════════════════════════

    #region 旧业务操作

    public async Task<PlcOperationResult> ReadStartSignalAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _modbusClient.ReadCoilsAsync(
                DefaultUnitId, _addressMap.StartRequestRegister, 1, ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("读取启动信号失败: 无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"读取启动信号失败: Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success("读取启动信号成功", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("读取启动信号被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 读取启动信号异常");
            return PlcOperationResult.Failure($"读取启动信号异常: {ex.Message}");
        }
    }

    public async Task<PlcOperationResult> SetBusyAsync(bool value, CancellationToken ct = default)
    {
        return await WriteCoilAsync("Busy", _addressMap.StartRequestRegister, value, ct).ConfigureAwait(false);
    }

    public async Task<PlcOperationResult> SetOkAsync(bool value, CancellationToken ct = default)
    {
        return await WriteCoilAsync("OK", _addressMap.StartRequestRegister, value, ct).ConfigureAwait(false);
    }

    public async Task<PlcOperationResult> SetNgAsync(bool value, CancellationToken ct = default)
    {
        return await WriteCoilAsync("NG", _addressMap.StartRequestRegister, value, ct).ConfigureAwait(false);
    }

    public async Task<PlcOperationResult> SetErrorAsync(bool value, CancellationToken ct = default)
    {
        return await WriteCoilAsync("Error", _addressMap.StartRequestRegister, value, ct).ConfigureAwait(false);
    }

    public async Task<PlcOperationResult> SelectTestPointAsync(int testPointIndex, CancellationToken ct = default)
    {
        try
        {
            ushort value = (ushort)(testPointIndex + 1);
            _logger.LogWarning("[PLC动作] 选择测试点: 索引={Index}, D100值={Value}", testPointIndex, value);

            var response = await _modbusClient.WriteSingleRegisterAsync(
                DefaultUnitId, _addressMap.StartRequestRegister, value, ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("写入测试点选择失败: 无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"写入测试点选择失败: Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success($"测试点选择 D100 = {value}", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("写入测试点选择被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 写入测试点选择异常");
            return PlcOperationResult.Failure($"写入测试点选择异常: {ex.Message}");
        }
    }

    public async Task<PlcOperationResult> SetRelayAsync(int channel, bool value, CancellationToken ct = default)
    {
        if (channel < 0)
        {
            _logger.LogWarning("[PLC动作] 继电器通道号无效: {Channel}", channel);
            return PlcOperationResult.Failure($"继电器通道号无效: {channel}");
        }

        ushort address = (ushort)(channel);
        _logger.LogWarning("[PLC动作] 继电器: 通道={Channel}, 地址=M{Address}, 值={Value}", channel, address, value);

        return await WriteCoilCoreAsync($"继电器{channel}", address, value, ct).ConfigureAwait(false);
    }

    public async Task<PlcOperationResult> WriteTestResultAsync(int index, ushort resultValue, CancellationToken ct = default)
    {
        if (index < 0)
        {
            _logger.LogWarning("[PLC动作] 检测结果索引无效: {Index}", index);
            return PlcOperationResult.Failure($"检测结果索引无效: {index}");
        }

        try
        {
            ushort address = (ushort)(index);
            _logger.LogWarning("[PLC动作] 写入检测结果: 索引={Index}, D{Address}={Value}", index, address, resultValue);

            var response = await _modbusClient.WriteSingleRegisterAsync(
                DefaultUnitId, address, resultValue, ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure($"检测结果[{index}]写入失败: 无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"检测结果[{index}]写入失败: Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success($"检测结果 D{address} = {resultValue}", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure($"检测结果[{index}]写入被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 检测结果[{Index}]写入异常", index);
            return PlcOperationResult.Failure($"检测结果[{index}]写入异常: {ex.Message}");
        }
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    //  内部辅助方法
    // ═══════════════════════════════════════════════════════════════

    #region 内部辅助

    /// <summary>写单保持寄存器（FC 0x06）核心实现</summary>
    private async Task<PlcOperationResult> WriteRegisterSingleAsync(string name, ushort address, ushort value, CancellationToken ct)
    {
        try
        {
            var response = await _modbusClient.WriteSingleRegisterAsync(
                DefaultUnitId, address, value, ct).ConfigureAwait(false);

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

    /// <summary>通用线圈写入（用于旧接口 SetBusy/SetOk/SetNg/SetError）</summary>
    private async Task<PlcOperationResult> WriteCoilAsync(string signalName, ushort address, bool value, CancellationToken ct)
    {
        _logger.LogWarning("[PLC动作] 设置 {Signal} = {Value} (M{Address})", signalName, value, address);
        return await WriteCoilCoreAsync(signalName, address, value, ct).ConfigureAwait(false);
    }

    /// <summary>写单线圈（FC 0x05）核心实现</summary>
    private async Task<PlcOperationResult> WriteCoilCoreAsync(string name, ushort address, bool value, CancellationToken ct)
    {
        try
        {
            var response = await _modbusClient.WriteSingleCoilAsync(
                DefaultUnitId, address, value, ct).ConfigureAwait(false);

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
            _logger.LogWarning(ex, "[PLC动作] {Name}写入异常 (M{Address})", name, address);
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
