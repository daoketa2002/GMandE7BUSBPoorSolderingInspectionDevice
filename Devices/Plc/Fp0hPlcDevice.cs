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
///   4. 关键写入（清 DT120/121、写 DT160、复位/停止/急停/报警检测）使用 Warning 级别日志审计
///   5. 普通轮询读状态不刷屏
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
    /// </summary>
    public async Task<PlcOperationResult<PlcMachineInputs>> ReadMachineInputsAsync(CancellationToken ct = default)
    {
        try
        {
            // 从 DT120 开始读 10 个保持寄存器（覆盖 DT120~DT129，含 120-123，后续 160/161 需另外读取）
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

            // 读取 DT160/161（需要额外读取）
            ushort dt160 = 0, dt161 = 0;
            var alarmResp = await _modbusClient.ReadHoldingRegistersAsync(
                DefaultUnitId, _addressMap.RelayDisconnectRegister, 2, ct).ConfigureAwait(false);
            if (alarmResp?.Data != null && alarmResp.Data.Length >= 4)
            {
                dt160 = BinaryPrimitives.ReadUInt16BigEndian(alarmResp.Data.AsSpan(0));
                dt161 = BinaryPrimitives.ReadUInt16BigEndian(alarmResp.Data.AsSpan(2));
            }

            // 读取继电器切换完成标志
            bool relayCompleted = false;
            if (_addressMap.RelaySwitchCompletedRegister.HasValue)
            {
                var relayResp = await _modbusClient.ReadHoldingRegistersAsync(
                    DefaultUnitId, _addressMap.RelaySwitchCompletedRegister.Value, 1, ct).ConfigureAwait(false);
                if (relayResp?.Data != null && relayResp.Data.Length >= 2)
                    relayCompleted = BinaryPrimitives.ReadUInt16BigEndian(relayResp.Data.AsSpan(0)) == 1;
            }

            var inputs = new PlcMachineInputs
            {
                IsStartRequested = dt120 == 1,
                IsResetRequested = dt121 == 1,
                IsStopRequested = dt122 == 1,
                IsEmergencyStop = dt123 == 1,
                IsBoardLeavingAlarm = dt161 == 1,
                IsRelaySwitchCompleted = relayCompleted,
                HasAlarm = dt161 == 1
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
    /// PC 完成复位处理（已写 DT160=1 通知断开引脚输出）后调用。
    /// </summary>
    public async Task<PlcOperationResult> ClearResetRequestAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] PC 清除 DT121 复位请求 → 写 DT121 = 0");
        return await WriteRegisterSingleAsync("DT121(复位请求)", _addressMap.ResetRegister, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入当前测试点左右引脚编号到 PLC。
    /// leftPinCode/rightPinCode 编码规则：A{n}=n, B{n}=20+n（由 TestPointConfig.EncodePin 生成）。
    /// 使用写多寄存器（FC 0x10）一次写入两个地址，减少通信次数。
    /// </summary>
    public async Task<PlcOperationResult> WriteCurrentTestPinsAsync(ushort leftPinCode, ushort rightPinCode, CancellationToken ct = default)
    {
        // 校验引脚编号寄存器是否已配置
        if (!_addressMap.LeftPinCodeRegister.HasValue)
            return PlcOperationResult.Failure("左引脚编号寄存器未配置(LeftPinCodeRegister=null)，禁止写入 PLC");
        if (!_addressMap.RightPinCodeRegister.HasValue)
            return PlcOperationResult.Failure("右引脚编号寄存器未配置(RightPinCodeRegister=null)，禁止写入 PLC");

        _logger.LogWarning("[PLC动作][审计] 写入测试点引脚编号: Left={LeftCode}({LeftReg}), Right={RightCode}({RightReg})",
            leftPinCode, _addressMap.LeftPinCodeRegister.Value,
            rightPinCode, _addressMap.RightPinCodeRegister.Value);

        try
        {
            // 使用写多寄存器一次写入左右引脚编号
            var response = await _modbusClient.WriteMultipleRegistersAsync(
                DefaultUnitId, _addressMap.LeftPinCodeRegister.Value,
                new[] { leftPinCode, rightPinCode }, ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("写入引脚编号失败: 无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"写入引脚编号失败: Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success($"引脚编号已写入: Left={leftPinCode}, Right={rightPinCode}", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("写入引脚编号被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 写入引脚编号异常");
            return PlcOperationResult.Failure($"写入引脚编号异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 写入当前测试点的左右引脚编号和极性到 DT130~DT133。
    /// 真实 PLC 模式下只写 PC 可写寄存器，不写 DT160/DT161。
    /// </summary>
    public async Task<PlcOperationResult> WriteCurrentTestPointAsync(
        ushort leftPinCode,
        ushort rightPinCode,
        ushort leftPolarityCode,
        ushort rightPolarityCode,
        CancellationToken ct = default)
    {
        if (!_addressMap.LeftPinCodeRegister.HasValue
            || !_addressMap.RightPinCodeRegister.HasValue
            || !_addressMap.LeftPinPolarityRegister.HasValue
            || !_addressMap.RightPinPolarityRegister.HasValue)
        {
            return PlcOperationResult.Failure("当前测试点寄存器 DT130~DT133 未完整配置，禁止写入 PLC");
        }

        _logger.LogWarning(
            "[PLC动作][审计] 写入测试点 DT130~DT133: Left={LeftCode}, Right={RightCode}, LeftPolarity={LeftPolarity}, RightPolarity={RightPolarity}",
            leftPinCode, rightPinCode, leftPolarityCode, rightPolarityCode);

        try
        {
            var response = await _modbusClient.WriteMultipleRegistersAsync(
                DefaultUnitId,
                _addressMap.LeftPinCodeRegister.Value,
                new[] { leftPinCode, rightPinCode, leftPolarityCode, rightPolarityCode },
                ct).ConfigureAwait(false);

            if (response is null)
                return PlcOperationResult.Failure("写入测试点 DT130~DT133 失败：无响应");
            if (response.IsError)
                return PlcOperationResult.Failure($"写入测试点 DT130~DT133 失败：Modbus错误码 {response.ErrorCode}", response);

            return PlcOperationResult.Success("测试点 DT130~DT133 已写入", response);
        }
        catch (OperationCanceledException)
        {
            return PlcOperationResult.Failure("写入测试点 DT130~DT133 被取消");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PLC动作] 写入测试点 DT130~DT133 异常");
            return PlcOperationResult.Failure($"写入测试点 DT130~DT133 异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 等待 PLC 继电器切换完成。
    /// 轮询 RelaySwitchCompletedRegister 寄存器的值，直到不为 0 或超时。
    /// 轮询间隔 20ms，避免高频刷屏。
    /// </summary>
    public async Task<PlcOperationResult> WaitRelaySwitchCompletedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_addressMap.RelaySwitchCompletedRegister.HasValue)
        {
            // 未配置继电器完成信号：使用固定延时替代（兼容旧方案）
            _logger.LogWarning("[PLC动作] 继电器切换完成寄存器未配置，使用默认延时 500ms");
            await Task.Delay(500, ct).ConfigureAwait(false);
            return PlcOperationResult.Success("继电器切换完成寄存器未配置，使用默认延时");
        }

        ushort addr = _addressMap.RelaySwitchCompletedRegister.Value;
        var deadline = DateTime.UtcNow + timeout;

        while (!ct.IsCancellationRequested)
        {
            // 检查超时
            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning("[PLC动作][审计] 继电器切换超时 ({TimeoutSeconds}s)", timeout.TotalSeconds);
                return PlcOperationResult.Failure($"继电器切换超时 ({timeout.TotalSeconds}s)");
            }

            try
            {
                var response = await _modbusClient.ReadHoldingRegistersAsync(
                    DefaultUnitId, addr, 1, ct).ConfigureAwait(false);

                if (response?.Data != null && response.Data.Length >= 2
                    && BinaryPrimitives.ReadUInt16BigEndian(response.Data.AsSpan(0)) == 1)
                    return PlcOperationResult.Success("继电器切换完成", response);
            }
            catch (OperationCanceledException)
            {
                return PlcOperationResult.Failure("等待继电器切换被取消");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PLC动作] 读取继电器切换状态异常");
                return PlcOperationResult.Failure($"读取继电器切换状态异常: {ex.Message}");
            }

            // 轮询间隔 20ms
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        return PlcOperationResult.Failure("等待继电器切换被取消");
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
    /// 写入综合检测结果到 PLC。
    /// 写入 FinalResultRegister（1=OK, 0=NG）和 NgPointIndexRegister（第一个NG索引）。
    /// </summary>
    public async Task<PlcOperationResult> WriteFinalResultAsync(bool isOk, int? ngPointIndex, CancellationToken ct = default)
    {
        ushort resultValue = isOk ? (ushort)1 : (ushort)0;

        // 先写综合结果
        if (_addressMap.FinalResultRegister.HasValue)
        {
            var result = await WriteRegisterSingleAsync("综合结果",
                _addressMap.FinalResultRegister.Value, resultValue, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
                return result;
        }

        // 写 NG 项目编号（isOk=false 且有有效索引时）
        if (!isOk && ngPointIndex.HasValue && _addressMap.NgPointIndexRegister.HasValue)
        {
            var ngResult = await WriteRegisterSingleAsync("NG项目编号",
                _addressMap.NgPointIndexRegister.Value, (ushort)(ngPointIndex.Value + 1), ct).ConfigureAwait(false);
            if (!ngResult.IsSuccess)
                return ngResult;
        }

        _logger.LogWarning("[PLC动作][审计] 写入综合结果: {Result}, NG索引={NgIndex}",
            isOk ? "OK" : "NG", ngPointIndex?.ToString() ?? "无");
        return PlcOperationResult.Success($"综合结果已写入: {(isOk ? "OK" : "NG")}");
    }

    /// <summary>
    /// 通知 PLC 断开引脚输出（写 DT160 = 1）。
    /// 测试完成、检测中止或复位时调用。
    /// PLC 收到后负责断开当前继电器并复位 DT160。
    /// </summary>
    public async Task<PlcOperationResult> RequestRelayDisconnectAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[PLC动作][审计] PC 通知 PLC 断开引脚输出 → 写 DT{Reg} = 1",
            _addressMap.RelayDisconnectRegister);
        return await WriteRegisterSingleAsync("DT160(断开引脚输出)",
            _addressMap.RelayDisconnectRegister, 1, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入上位机异常状态到 PLC。
    /// 通信失败、板离设备报警、万用表无响应等异常时调用。
    /// </summary>
    public async Task<PlcOperationResult> WritePcErrorAsync(CancellationToken ct = default)
    {
        // 写 DT160=1 通知断开引脚输出
        await RequestRelayDisconnectAsync(ct).ConfigureAwait(false);

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
