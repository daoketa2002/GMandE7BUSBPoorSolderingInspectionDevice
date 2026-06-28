namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

/// <summary>
/// PLC 当前输入信号快照。
/// 由 ReadMachineInputsAsync 一次性读出 DT120/121/122/123/161 等信号后组装。
/// </summary>
public sealed class PlcMachineInputs
{
    /// <summary>DT120=1: PLC 请求启动（已确认基板到位）</summary>
    public bool IsStartRequested { get; init; }

    /// <summary>DT121=1: 复位请求</summary>
    public bool IsResetRequested { get; init; }

    /// <summary>DT122=1: 停止</summary>
    public bool IsStopRequested { get; init; }

    /// <summary>DT123=1: 急停</summary>
    public bool IsEmergencyStop { get; init; }

    /// <summary>DT161=1: 测试中板离设备报警</summary>
    public bool IsBoardLeavingAlarm { get; init; }

    /// <summary>是否有报警（汇总指示，由外部组装）</summary>
    public bool HasAlarm { get; init; }

    /// <summary>报警代码（来自 AlarmCodeRegister，未确认时恒为 0）</summary>
    public ushort AlarmCode { get; init; }

    /// <summary>继电器切换完成标志（来自 RelaySwitchCompletedRegister，未确认时恒为 false）</summary>
    public bool IsRelaySwitchCompleted { get; init; }

    /// <summary>
    /// 是否存在需要上位机响应的中断信号。
    /// 包含停止、急停、复位、板离报警四种。
    /// </summary>
    public bool HasInterrupt => IsStopRequested || IsEmergencyStop || IsResetRequested || IsBoardLeavingAlarm;
}
