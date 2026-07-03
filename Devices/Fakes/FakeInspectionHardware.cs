using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
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
    private readonly string[] _rawValues = ["+1.05000000E+01", "OPEN", "SHORT", "150.000"];
    private int _rawValueIndex;
    private bool _isConnected;

    public FakeInspectionHardware(ILogger<FakeInspectionHardware> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _isConnected;

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
        _logger.LogWarning("[Fake硬件][调试] 模拟信号写入 DT{Address} = {Value}", address, value);
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
        _logger.LogWarning("[Fake硬件][审计] Fake PLC/万用表已连接，仅用于最小闭环调试");
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        _isConnected = false;
        ConnectionStateChanged?.Invoke(this, false);
        _logger.LogWarning("[Fake硬件][审计] Fake PLC/万用表已断开");
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
        return Task.FromResult(PlcOperationResult.Success("Fake DT120 已清除"));
    }

    public Task<PlcOperationResult> ClearResetRequestAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.ResetSignal, 0);
        return Task.FromResult(PlcOperationResult.Success("Fake DT121 已清除"));
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
            _logger.LogWarning("[Fake硬件][审计] 清空引脚选择区（内部清 DT302）");
            return PlcOperationResult.Success("Fake 引脚选择区已清空");
        }

        _logger.LogWarning(
            "[Fake硬件][审计] 写入引脚选择区: {LeftPin}(DT{LeftSel}=1,DT{LeftPol}={LeftPolVal}), {RightPin}(DT{RightSel}=1,DT{RightPol}={RightPolVal})",
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
        _logger.LogWarning("[Fake硬件][审计] PC 允许开始检测 → DT234=1");
        return Task.FromResult(PlcOperationResult.Success("Fake DT234=1"));
    }

    /// <summary>
    /// 清除上位机允许开始检测信号（写 DT234 = 0）。
    /// </summary>
    public Task<PlcOperationResult> ClearPcReadyAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.PcReadyToStart, 0);
        _logger.LogWarning("[Fake硬件][审计] PC 清除允许开始 → DT234=0");
        return Task.FromResult(PlcOperationResult.Success("Fake DT234=0"));
    }

    public Task<PlcOperationResult> WritePointResultAsync(int pointIndex, bool isOk, CancellationToken ct = default)
    {
        _logger.LogWarning("[Fake硬件][审计] 单点结果 Index={Index}, IsOk={IsOk}", pointIndex, isOk);
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

        _logger.LogWarning("[Fake硬件][审计] 综合结果 DT304/DT305: IsOk={IsOk}, NgPointIndex={NgPointIndex}", isOk, ngPointIndex);
        return Task.FromResult(PlcOperationResult.Success("Fake 综合结果已记录"));
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

        _logger.LogWarning("[Fake硬件][审计] 已清空 DT130~DT185");
        return Task.FromResult(PlcOperationResult.Success("Fake DT130~DT185 已清空"));
    }

    /// <summary>
    /// 通知 PLC 断开引脚输出（旧语义，新流程使用 ClearPinOutputsAsync）。
    /// </summary>
    [Obsolete("请使用 ClearPinOutputsAsync 替代")]
    public Task<PlcOperationResult> RequestRelayDisconnectAsync(CancellationToken ct = default)
    {
        // 兼容：调用 ClearPinOutputsAsync 内部逻辑
        lock (_syncRoot)
        {
            for (ushort addr = PlcAddressMap.PinOutputStart; addr <= PlcAddressMap.PinOutputEnd; addr++)
            {
                _registers[addr] = 0;
            }
        }

        _logger.LogWarning("[Fake硬件][审计] 已清空 DT130~DT185（通过旧 RequestRelayDisconnectAsync）");
        return Task.FromResult(PlcOperationResult.Success("Fake 引脚输出已断开"));
    }

    public Task<PlcOperationResult> WritePcErrorAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[Fake硬件][审计] PC 异常已写入 Fake PLC");
        return Task.FromResult(PlcOperationResult.Success("Fake PC 异常已记录"));
    }

    public Task<PlcOperationResult> ClearAlarmReleasedAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.AlarmReleased, 0);
        _logger.LogWarning("[Fake硬件][审计] PC 清除 DT303 报警解除信号");
        return Task.FromResult(PlcOperationResult.Success("Fake DT303=0"));
    }

    public Task<bool> InitializeResistanceModeAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[Fake硬件][审计] Fake 万用表初始化为电阻模式");
        return Task.FromResult(true);
    }

    /// <summary>
    /// 模拟导通模式初始化，记录导通阈值，直接返回成功。
    /// </summary>
    public Task<bool> InitializeContinuityModeAsync(double thresholdOhm = 10.0, CancellationToken ct = default)
    {
        _logger.LogWarning("[Fake硬件][审计] Fake 万用表切换为导通模式，导通阈值={ThresholdOhm}Ω", thresholdOhm);
        return Task.FromResult(true);
    }

    /// <summary>
    /// 清 DT302=0，当前测试点继电器动作完成标志。
    /// </summary>
    public Task<PlcOperationResult> ClearRelayActionCompletedAsync(CancellationToken ct = default)
    {
        WriteRegister(PlcAddressMap.RelayActionCompleted, 0);
        _logger.LogWarning("[Fake硬件][审计] Fake 清 DT302=0");
        return Task.FromResult(PlcOperationResult.Success("Fake DT302=0"));
    }

    public Task<string> ReadResistanceRawAsync(CancellationToken ct = default)
    {
        string raw;
        lock (_syncRoot)
        {
            raw = _rawValues[_rawValueIndex % _rawValues.Length];
            _rawValueIndex++;
        }

        _logger.LogWarning("[Fake硬件][审计] Fake GDM-9060 READ? RawText={RawText}", raw);
        return Task.FromResult(raw);
    }

    // ── 旧接口兼容实现 ──

    public Task<PlcOperationResult> ReadStartSignalAsync(CancellationToken ct = default)
        => Task.FromResult(ReadRegister(PlcAddressMap.StartSignal) == 1
            ? PlcOperationResult.Success("Fake 启动信号=1")
            : PlcOperationResult.Failure("Fake 启动信号=0"));

    public Task<PlcOperationResult> SetBusyAsync(bool value, CancellationToken ct = default) => Task.FromResult(PlcOperationResult.Success());
    public Task<PlcOperationResult> SetOkAsync(bool value, CancellationToken ct = default) => Task.FromResult(PlcOperationResult.Success());
    public Task<PlcOperationResult> SetNgAsync(bool value, CancellationToken ct = default) => Task.FromResult(PlcOperationResult.Success());
    public Task<PlcOperationResult> SetErrorAsync(bool value, CancellationToken ct = default) => Task.FromResult(PlcOperationResult.Success());
    public Task<PlcOperationResult> SelectTestPointAsync(int testPointIndex, CancellationToken ct = default) => Task.FromResult(PlcOperationResult.Success());
    public Task<PlcOperationResult> SetRelayAsync(int channel, bool value, CancellationToken ct = default) => Task.FromResult(PlcOperationResult.Success());
    public Task<PlcOperationResult> WriteTestResultAsync(int index, ushort resultValue, CancellationToken ct = default) => Task.FromResult(PlcOperationResult.Success());

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
