namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

/// <summary>
/// PLC 控制信号快照，来自 DT120~DT123。
/// 由 ReadControlSignalsAsync 一次性读出 4 个寄存器后组装。
/// 用于 UI 轮询、引擎中断检查、复位验证等高频路径。
/// </summary>
public sealed class PlcControlSignals
{
    /// <summary>DT120=1: PLC 请求启动</summary>
    public bool IsStartRequested { get; init; }

    /// <summary>DT121=1: 复位请求</summary>
    public bool IsResetRequested { get; init; }

    /// <summary>DT122=1: 停止</summary>
    public bool IsStopRequested { get; init; }

    /// <summary>DT123=1: 急停</summary>
    public bool IsEmergencyStop { get; init; }

    /// <summary>是否存在需要上位机响应的中断信号。</summary>
    public bool HasInterrupt => IsStopRequested || IsEmergencyStop || IsResetRequested;
}
