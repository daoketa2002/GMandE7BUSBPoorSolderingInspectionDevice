using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Devices.Fakes;

// FakeInspectionHardware 是最小检测闭环阶段使用的硬件替身。
//
// 目的：
// 1. 在没有稳定 PLC / 万用表接入时，先跑通 InspectionEngine 主流程。
// 2. 模拟 PLC DT 地址读写、DT160 引脚闭合完成、DT161 板离设备报警。
// 3. 模拟 GDM-9060 READ? 返回值，包括数值、OPEN、SHORT。
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
    /// 用于模拟 PLC 写入 DT121/DT122/DT123/DT161 等信号，
    /// 方便在 UI 上触发停止/复位/急停/板离等异常路径验证。
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
                IsBoardLeavingAlarm = ReadRegister(PlcAddressMap.BoardRemovedAlarm) == 1,
                IsRelaySwitchCompleted = ReadRegister(PlcAddressMap.RelayClosedCompleted) == 1
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

    public Task<PlcOperationResult> WriteCurrentTestPinsAsync(ushort leftPinCode, ushort rightPinCode, CancellationToken ct = default)
    {
        return WriteCurrentTestPointAsync(leftPinCode, rightPinCode, 0, 1, ct);
    }

    public async Task<PlcOperationResult> WriteCurrentTestPointAsync(
        ushort leftPinCode,
        ushort rightPinCode,
        ushort leftPolarityCode,
        ushort rightPolarityCode,
        CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            _registers[PlcAddressMap.LeftPinNumber] = leftPinCode;
            _registers[PlcAddressMap.RightPinNumber] = rightPinCode;
            _registers[PlcAddressMap.LeftPinPolarity] = leftPolarityCode;
            _registers[PlcAddressMap.RightPinPolarity] = rightPolarityCode;
            _registers[PlcAddressMap.RelayClosedCompleted] = 0;
        }

        if (leftPinCode == 0 && rightPinCode == 0 && leftPolarityCode == 0 && rightPolarityCode == 0)
        {
            _logger.LogWarning("[Fake硬件][审计] 清空 DT130~DT133，Fake 内部清 DT160");
            return PlcOperationResult.Success("Fake DT130~DT133 已清空");
        }

        _logger.LogWarning(
            "[Fake硬件][审计] 写入 DT130~DT133: {Left}, {Right}, {LeftPolarity}, {RightPolarity}",
            leftPinCode, rightPinCode, leftPolarityCode, rightPolarityCode);

        await Task.Delay(200, ct).ConfigureAwait(false);
        WriteRegister(PlcAddressMap.RelayClosedCompleted, 1);
        return PlcOperationResult.Success("Fake 测试点已写入，DT160 已置 1");
    }

    public async Task<PlcOperationResult> WaitRelaySwitchCompletedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadRegister(PlcAddressMap.RelayClosedCompleted) == 1)
                return PlcOperationResult.Success("Fake DT160=1");

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return PlcOperationResult.Failure("Fake 等待 DT160=1 超时");
    }

    public Task<PlcOperationResult> WritePointResultAsync(int pointIndex, bool isOk, CancellationToken ct = default)
    {
        _logger.LogWarning("[Fake硬件][审计] 单点结果 Index={Index}, IsOk={IsOk}", pointIndex, isOk);
        return Task.FromResult(PlcOperationResult.Success("Fake 单点结果已记录"));
    }

    public Task<PlcOperationResult> WriteFinalResultAsync(bool isOk, int? ngPointIndex, CancellationToken ct = default)
    {
        _logger.LogWarning("[Fake硬件][审计] 综合结果 IsOk={IsOk}, NgPointIndex={NgPointIndex}", isOk, ngPointIndex);
        return Task.FromResult(PlcOperationResult.Success("Fake 综合结果已记录"));
    }

    public Task<PlcOperationResult> RequestRelayDisconnectAsync(CancellationToken ct = default)
    {
        lock (_syncRoot)
        {
            _registers[PlcAddressMap.LeftPinNumber] = 0;
            _registers[PlcAddressMap.RightPinNumber] = 0;
            _registers[PlcAddressMap.LeftPinPolarity] = 0;
            _registers[PlcAddressMap.RightPinPolarity] = 0;
            _registers[PlcAddressMap.RelayClosedCompleted] = 0;
            _registers[PlcAddressMap.BoardRemovedAlarm] = 0;
        }

        _logger.LogWarning("[Fake硬件][审计] 已清空 DT130~DT133，并由 Fake 内部清 DT160/DT161");
        return Task.FromResult(PlcOperationResult.Success("Fake 引脚输出已断开"));
    }

    public Task<PlcOperationResult> WritePcErrorAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[Fake硬件][审计] PC 异常已写入 Fake PLC");
        return Task.FromResult(PlcOperationResult.Success("Fake PC 异常已记录"));
    }

    public Task<bool> InitializeResistanceModeAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("[Fake硬件][审计] Fake 万用表初始化为电阻模式");
        return Task.FromResult(true);
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
