using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Devices.Fakes;

// FakeInspectionHardware 是最小检测闭环阶段使用的硬件替身。
//
// 目的：
// 1. 在没有稳定 PLC / 万用表接入时，先跑通 InspectionEngine 主流程。
// 2. 模拟 PLC DT 地址读写、DT130~DT185 引脚选择区、DT302 引脚继电器动作完成。
// 3. 模拟 GDM-9060 READ? 返回值，包括数值、OPEN、SHORT。
//
// 最新地址表说明：
// - DT130~DT185 为每引脚独立选择区，不再使用 DT130~DT133 通用左右脚模型。
// - DT160/DT161 现在是 A12 引脚的 Select/Polarity 地址，不再是流程信号。
// - DT302 替代旧 DT160 作为继电器动作完成信号。
//
// 后续接入真实设备后：
// 1. 在 Program.cs 中关闭 UseFakeInspectionHardware。
// 2. 删除或保留但不注册 FakeInspectionHardware。
// 3. 不允许在正式生产环境启用。
public sealed class FakeInspectionHardware : IPlcDevice, IMultimeterDevice
{
    private readonly ILogger<FakeInspectionHardware> _logger;
    private readonly object _syncRoot = new();
    private readonly Dictionary<ushort, ushort> _registers = new();
    private bool _isConnected;

    // 电阻模式模拟返回序列（轮转）
    private readonly string[] _resistanceValues = ["+1.05000000E+01", "OPEN", "SHORT", "150.000"];
    private int _resistanceValueIndex;

    // 导通模式模拟返回序列（独立轮转，包含典型导通/开路值）
    private readonly string[] _continuityValues = ["+0.50000000E+00", "OPEN", "SHORT", "+9.50000000E+00"];
    private int _continuityValueIndex;

    public FakeInspectionHardware(ILogger<FakeInspectionHardware> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _isConnected;

    public Task<DeviceHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        => Task.FromResult(_isConnected
            ? DeviceHealthCheckResult.Healthy("Fake 硬件在线")
            : DeviceHealthCheckResult.Unhealthy("Fake 硬件未连接"));

    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<PlcNotification>? NotificationReceived;
    public event EventHandler<AlarmState>? AlarmStateChanged;

    /// <summary>
    /// 写入特定的输入信号寄存器值（仅 Fake 模式调试用）。
    /// 用于模拟 PLC 写入 DT121/DT122/DT123 等信号，
    /// 方便在 UI 上触发停止/复位/急停等异常路径验证。
    /// 接入真实 PLC 后删除该方法。
    /// </summary>
    public Task WriteInputRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
    {
        WriteRegister(address, value);
        _logger.LogInformation("[Fake][控制动作] 模拟信号写入 DT{Address} = {Value}", address, value);
        return Task.CompletedTask;
    }

    public void ApplyConfig(FP0HCommunicationConfig config)
    {
        // Fake 不需要真实通信参数，保留方法是为了兼容 DeviceConnectionManager。
    }

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        _isConnected = true;
        ConnectionStateChanged?.Invoke(this, true);
        _logger.LogInformation("[Fake][连接] Fake PLC/万用表已连接，仅用于最小闭环调试");
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        _isConnected = false;
        ConnectionStateChanged?.Invoke(this, false);
        _logger.LogInformation("[Fake][连接] Fake PLC/万用表已断开");
        return Task.CompletedTask;
    }

    public Task<PlcOperationResult<PlcMachineInputs>> ReadMachineInputsAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            var inputs = new PlcMachineInputs
            {
                IsStartRequested = ReadRegister(PlcAddressMap.StartSignal) == 1,
                IsResetRequested = ReadRegister(PlcAddressMap.ResetSignal) == 1,
                IsStopRequested = ReadRegister(PlcAddressMap.StopSignal) == 1,
                IsEmergencyStop = ReadRegister(PlcAddressMap.EmergencyStopSignal) == 1,
                // DT302 替代旧 DT160 作为继电器动作完成信号
                IsRelayActionCompleted = ReadRegister(PlcAddressMap.RelayActionCompleted) == 1,
                // DT303 报警解除
                IsAlarmReleased = ReadRegister(PlcAddressMap.AlarmReleased) == 1,
                HasAlarm = false
            };

            return Task.FromResult(PlcOperationResult<PlcMachineInputs>.Success(inputs, "Fake PLC 输入读取成功"));
        }
    }

    public Task<PlcOperationResult> ClearStartRequestAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.StartSignal, 0);
        _logger.LogInformation("[Fake][PLC] 已清除 DT120=0。");
        return Task.FromResult(PlcOperationResult.Success("Fake DT120 已清除"));
    }

    public Task<PlcOperationResult> ClearResetRequestAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.ResetSignal, 0);
        return Task.FromResult(PlcOperationResult.Success("Fake DT121 已清除"));
    }

    /// <summary>
    /// Fake 上位机请求启动（写 DT120=1）。
    /// 半实物联调临时入口，正式整机联调后删除。
    /// </summary>
    public Task<PlcOperationResult> RequestStartAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.StartSignal, 1);
        _logger.LogInformation("[Fake][控制动作] 已写入 DT120=1。");
        return Task.FromResult(PlcOperationResult.Success("Fake DT120=1"));
    }

    /// <summary>
    /// Fake 上位机请求复位（写 DT121=1）。
    /// 半实物/现场临时入口。如果后续复位改为只由实体按钮触发，应删除。
    /// </summary>
    public Task<PlcOperationResult> RequestResetAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.ResetSignal, 1);
        _logger.LogInformation("[Fake][控制动作] 已写入 DT121=1。");
        return Task.FromResult(PlcOperationResult.Success("Fake DT121=1"));
    }

    public Task<PlcOperationResult> RequestStopAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.StopSignal, 1);
        _logger.LogInformation("[Fake][控制动作] 已写入 DT122=1（停止）。");
        return Task.FromResult(PlcOperationResult.Success("Fake DT122=1"));
    }

    public Task<PlcOperationResult> ClearStopRequestAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.StopSignal, 0);
        _logger.LogInformation("[Fake][控制动作] 已清除 DT122=0。");
        return Task.FromResult(PlcOperationResult.Success("Fake DT122 已清除"));
    }

    public Task<PlcOperationResult> RequestEmergencyStopAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.EmergencyStopSignal, 1);
        _logger.LogInformation("[Fake][控制动作] 已写入 DT123=1（急停）。");
        return Task.FromResult(PlcOperationResult.Success("Fake DT123=1"));
    }

    public Task<PlcOperationResult> ClearEmergencyStopRequestAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.EmergencyStopSignal, 0);
        _logger.LogInformation("[Fake][控制动作] 已清除 DT123=0。");
        return Task.FromResult(PlcOperationResult.Success("Fake DT123 已清除"));
    }

    public Task<PlcOperationResult> RequestTerminateAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.TerminateSignal, 1);
        _logger.LogInformation("[Fake][控制动作] 终了按钮触发：已写入 DT306=1。");
        return Task.FromResult(PlcOperationResult.Success("Fake DT306=1"));
    }

    public Task<PlcOperationResult> ClearTerminateRequestAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.TerminateSignal, 0);
        _logger.LogInformation("[Fake][控制动作] 已清除 DT306=0。");
        return Task.FromResult(PlcOperationResult.Success("Fake DT306 已清除"));
    }

    /// <summary>Fake PLC：写入本次检测流程结束通知 DT307=1。</summary>
    public Task<PlcOperationResult> WriteInspectionEndedAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.InspectionEndedSignal, 1);
        _logger.LogInformation("[Fake][PLC][流程结束] DT307=1");
        return Task.FromResult(PlcOperationResult.Success("Fake DT307=1"));
    }

    /// <summary>Fake PLC：清除本次检测流程结束通知 DT307=0。</summary>
    public Task<PlcOperationResult> ClearInspectionEndedAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.InspectionEndedSignal, 0);
        _logger.LogInformation("[Fake][PLC][流程结束] DT307=0");
        return Task.FromResult(PlcOperationResult.Success("Fake DT307=0"));
    }

    /// <summary>Fake PLC 写入 DT308，保留内部寄存器值供 U1 验收。</summary>
    public Task<PlcOperationResult> WriteWorkstationAsync(string workstation, CancellationToken ct = default)
    {
        var value = WorkstationConstants.ToPlcValue(workstation);
        WriteRegister(PlcAddressMap.WorkstationSelection, value);
        _logger.LogWarning("[Fake][PLC][审计] 写入工位 DT308={Value} ({Workstation})", value, workstation);
        return Task.FromResult(PlcOperationResult.Success($"Fake DT308={value}"));
    }

    /// <summary>
    /// 写入当前测试点的两个引脚到 Fake PLC（最新地址表：每引脚独立选择区）。
    /// 模拟写入引脚选择区，200ms 后置 DT302=1 表示继电器动作完成。
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

        bool isClearAll = string.IsNullOrWhiteSpace(leftPinName) && string.IsNullOrWhiteSpace(rightPinName);

        lock (_syncRoot)
        {
            if (isClearAll)
            {
                // 清空所有引脚输出区的标记（调用方后续会调 ClearPinOutputsAsync 真清空）
                _registers[leftSelectAddr] = 0;
                _registers[leftPolarAddr] = 0;
                _registers[rightSelectAddr] = 0;
                _registers[rightPolarAddr] = 0;
            }
            else
            {
                // 写入左引脚 Select=1, Polarity=值
                _registers[leftSelectAddr] = 1;
                _registers[leftPolarAddr] = leftPolarityCode;
                // 写入右引脚 Select=1, Polarity=值
                _registers[rightSelectAddr] = 1;
                _registers[rightPolarAddr] = rightPolarityCode;
            }

            // 清除之前的 DT302 状态，模拟 PLC 清 0
            _registers[PlcAddressMap.RelayActionCompleted] = 0;
        }

        if (isClearAll)
        {
            _logger.LogInformation("[Fake][PLC] 清空引脚选择区（内部清 DT302）");
            return PlcOperationResult.Success("Fake 引脚选择区已清空");
        }

        _logger.LogInformation(
            "[Fake][PLC] 写入引脚选择区: {LeftPin}(DT{LeftSel}=1,DT{LeftPol}={LeftPolVal}), {RightPin}(DT{RightSel}=1,DT{RightPol}={RightPolVal})",
            leftPinName, leftSelectAddr, leftPolarAddr, leftPolarityCode,
            rightPinName, rightSelectAddr, rightPolarAddr, rightPolarityCode);

        // 模拟 PLC 引脚继电器动作延时
        await Task.Delay(200, ct).ConfigureAwait(false);
        WriteRegister(PlcAddressMap.RelayActionCompleted, 1);
        return PlcOperationResult.Success($"Fake 测试点已写入，DT302 已置 1");
    }

    /// <summary>
    /// 等待继电器动作完成（轮询 DT302=1）。
    /// </summary>
    public async Task<PlcOperationResult> WaitRelaySwitchCompletedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadRegister(PlcAddressMap.RelayActionCompleted) == 1)
                return PlcOperationResult.Success("Fake DT302=1");

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return PlcOperationResult.Failure("Fake 等待 DT302=1 超时");
    }

    /// <summary>
    /// 写入上位机允许开始检测信号（写 DT234 = 1）。
    /// </summary>
    public Task<PlcOperationResult> WritePcReadyAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.PcReadyToStart, 1);
        _logger.LogInformation("[Fake][PLC] PC 允许开始检测 → DT234=1");
        return Task.FromResult(PlcOperationResult.Success("Fake DT234=1"));
    }

    /// <summary>
    /// 清除上位机允许开始检测信号（写 DT234 = 0）。
    /// </summary>
    public Task<PlcOperationResult> ClearPcReadyAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.PcReadyToStart, 0);
        _logger.LogInformation("[Fake][PLC] PC 清除允许开始 → DT234=0");
        return Task.FromResult(PlcOperationResult.Success("Fake DT234=0"));
    }

    public Task<PlcOperationResult> WritePointResultAsync(int pointIndex, bool isOk, CancellationToken ct = default)
    {
        _logger.LogInformation("[Fake][PLC] 单点结果 Index={Index}, IsOk={IsOk}", pointIndex, isOk);
        return Task.FromResult(PlcOperationResult.Success("Fake 单点结果已记录"));
    }

    public Task<PlcOperationResult> WriteFinalResultAsync(bool isOk, int? ngPointIndex, CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            // 写 DT304/DT305
            _registers[PlcAddressMap.ProductOk] = isOk ? (ushort)1 : (ushort)0;
            _registers[PlcAddressMap.ProductNg] = isOk ? (ushort)0 : (ushort)1;
        }

        _logger.LogInformation("[Fake][PLC] 综合结果 DT304/DT305: IsOk={IsOk}, NgPointIndex={NgPointIndex}", isOk, ngPointIndex);
        return Task.FromResult(PlcOperationResult.Success("Fake 综合结果已记录"));
    }

    public Task<PlcOperationResult> ClearFinalResultAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            _registers[PlcAddressMap.ProductOk] = 0;
            _registers[PlcAddressMap.ProductNg] = 0;
        }

        _logger.LogInformation("[Fake][PLC] 已清除 DT304/DT305（复位清空产品结果）");
        return Task.FromResult(PlcOperationResult.Success("Fake DT304/DT305 已清除"));
    }

    /// <summary>
    /// 清空引脚输出寄存器（清 DT130~DT185 全部为 0）。
    /// </summary>
    public Task<PlcOperationResult> ClearPinOutputsAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            for (ushort addr = PlcAddressMap.PinOutputStart; addr <= PlcAddressMap.PinOutputEnd; addr++)
            {
                _registers[addr] = 0;
            }
        }

        _logger.LogInformation("[Fake][PLC] 已清空 DT130~DT185");
        return Task.FromResult(PlcOperationResult.Success("Fake DT130~DT185 已清空"));
    }

    /// <summary>
    /// 写入上位机异常状态到 PLC。
    /// </summary>
    public Task<PlcOperationResult> WritePcErrorAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[Fake][异常注入] PC 异常已写入 Fake PLC");
        return Task.FromResult(PlcOperationResult.Success("Fake PC 异常已记录"));
    }

    public Task<PlcOperationResult<PlcControlSignals>> ReadControlSignalsAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            var signals = new PlcControlSignals
            {
                IsStartRequested = ReadRegister(PlcAddressMap.StartSignal) == 1,
                IsResetRequested = ReadRegister(PlcAddressMap.ResetSignal) == 1,
                IsStopRequested = ReadRegister(PlcAddressMap.StopSignal) == 1,
                IsEmergencyStop = ReadRegister(PlcAddressMap.EmergencyStopSignal) == 1,
            };
            return Task.FromResult(
                PlcOperationResult<PlcControlSignals>.Success(signals, "Fake 控制信号读取成功"));
        }
    }

    public Task<PlcOperationResult<WorkstationInstallRejectSignals>> ReadWorkstationInstallRejectSignalsAsync(
        CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            var signals = new WorkstationInstallRejectSignals
            {
                LeftCode = ReadRegister(PlcAddressMap.LeftWorkstationInstallReject),
                RightCode = ReadRegister(PlcAddressMap.RightWorkstationInstallReject)
            };
            return Task.FromResult(PlcOperationResult<WorkstationInstallRejectSignals>.Success(
                signals, $"Fake DT309={signals.LeftCode}, DT310={signals.RightCode}"));
        }
    }

    public Task<PlcOperationResult> ClearWorkstationInstallRejectSignalsAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            _registers[PlcAddressMap.LeftWorkstationInstallReject] = 0;
            _registers[PlcAddressMap.RightWorkstationInstallReject] = 0;
        }
        _logger.LogWarning("[Fake][PLC][安装拒绝] 清除 DT309=0, DT310=0");
        return Task.FromResult(PlcOperationResult.Success("Fake DT309=0, DT310=0 已清除"));
    }

    public Task<PlcOperationResult> WriteWorkstationInstallRejectForAcceptanceAsync(
        ushort leftCode,
        ushort rightCode,
        CancellationToken ct = default)
    {
        if (leftCode > 2 || rightCode > 2)
            return Task.FromResult(PlcOperationResult.Failure("临时验收注入只允许 DT309/DT310 使用 0、1、2"));

        lock (_syncRoot)
        {
            _registers[PlcAddressMap.LeftWorkstationInstallReject] = leftCode;
            _registers[PlcAddressMap.RightWorkstationInstallReject] = rightCode;
        }
        _logger.LogWarning("[Fake][验收注入][安装拒绝] 写入 DT309={LeftCode}, DT310={RightCode}", leftCode, rightCode);
        return Task.FromResult(PlcOperationResult.Success($"Fake DT309={leftCode}, DT310={rightCode} 已写入"));
    }

    public Task<PlcOperationResult<bool>> ReadRelayCompletedAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            bool completed = ReadRegister(PlcAddressMap.RelayActionCompleted) == 1;
            return Task.FromResult(
                PlcOperationResult<bool>.Success(completed, $"Fake DT302={(completed ? 1 : 0)}"));
        }
    }

    public Task<PlcOperationResult<bool>> ReadAlarmReleasedAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            bool released = ReadRegister(PlcAddressMap.AlarmReleased) == 1;
            return Task.FromResult(
                PlcOperationResult<bool>.Success(released, $"Fake DT303={(released ? 1 : 0)}"));
        }
    }

    public Task<PlcOperationResult> ClearAlarmReleasedAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.AlarmReleased, 0);
        _logger.LogInformation("[Fake][PLC] PC 清除 DT303 报警解除信号");
        return Task.FromResult(PlcOperationResult.Success("Fake DT303=0"));
    }

    public Task<PlcOperationResult> RequestAlarmReleaseAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.AlarmReleased, 1);
        _logger.LogInformation("[Fake][控制动作] 模拟 PLC 写入 DT303=1，报警解除按钮可用");
        return Task.FromResult(PlcOperationResult.Success("Fake DT303=1"));
    }

    public Task<bool> InitializeResistanceModeAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[Fake][DMM] 模拟切换为四线电阻模式，Mode=Resistance4Wire");
        return Task.FromResult(true);
    }

    /// <summary>
    /// 模拟导通模式初始化，记录导通阈值，直接返回成功。
    /// </summary>
    public Task<bool> InitializeContinuityModeAsync(double thresholdOhm = 10.0, CancellationToken ct = default)
    {
        _logger.LogDebug("[Fake][DMM] 模拟切换为导通模式，导通阈值={ThresholdOhm}Ω", thresholdOhm);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Fake：恢复万用表为远程 4 线电阻空闲态。
    /// 只记录日志，不操作真实硬件。
    /// </summary>
    public Task<bool> PrepareIdleResistanceModeAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[Fake][DMM] 模拟恢复为远程 4 线电阻空闲态，Mode=Resistance4Wire");
        return Task.FromResult(true);
    }

    /// <summary>
    /// Fake：退出远程控制。
    /// 只记录日志，不操作真实硬件。
    /// </summary>
    public Task ReleaseToLocalAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[Fake][DMM] 已退出远程控制");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fake 模式：万用表永远在线，直接返回 true。
    /// </summary>
    public Task<bool> PingAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[Fake][连接] Ping 万用表 → 在线");
        return Task.FromResult(true);
    }

    /// <summary>
    /// 清 DT302=0，当前测试点继电器动作完成标志。
    /// </summary>
    public Task<PlcOperationResult> ClearRelayActionCompletedAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.RelayActionCompleted, 0);
        _logger.LogInformation("[Fake][PLC] 清 DT302=0");
        return Task.FromResult(PlcOperationResult.Success("Fake DT302=0"));
    }

    /// <summary>
    /// Fake 电阻模式读取：轮转返回 _resistanceValues 中的值。
    /// 模拟 GDM-9060 READ? 的典型返回：正常电阻值、OPEN、SHORT、大电阻值。
    /// </summary>
    public Task<string> ReadResistanceRawAsync(CancellationToken ct = default)
    {
        string raw;
        lock (_syncRoot)
        {
            raw = _resistanceValues[_resistanceValueIndex % _resistanceValues.Length];
            _resistanceValueIndex++;
        }

        _logger.LogInformation("[Fake][DMM] 模拟返回 Command=READ?, RawText={RawText}", raw);
        return Task.FromResult(raw);
    }

    /// <summary>
    /// Fake 导通模式读取：使用独立的 _continuityValues 序列轮转。
    /// 与电阻模式区分开，模拟导通模式下的典型返回值：
    /// 小电阻（SHORT）、OPEN、SHORT 文本、临界值（接近阈值 10Ω）。
    /// </summary>
    public Task<string> ReadContinuityRawAsync(CancellationToken ct = default)
    {
        string raw;
        lock (_syncRoot)
        {
            raw = _continuityValues[_continuityValueIndex % _continuityValues.Length];
            _continuityValueIndex++;
        }

        _logger.LogInformation("[Fake][DMM] 模拟返回 Command=MEAS:CONT?, RawText={RawText}", raw);
        return Task.FromResult(raw);
    }

    private ushort ReadRegister(ushort address)
    {
        return _registers.TryGetValue(address, out ushort value) ? value : (ushort)0;
    }

    private void WriteRegister(ushort address, ushort value)
    {
        lock (_syncRoot)
        {
            _registers[address] = value;
        }
    }
}
