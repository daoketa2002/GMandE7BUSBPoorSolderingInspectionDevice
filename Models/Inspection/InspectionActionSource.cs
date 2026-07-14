namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>
/// 控制动作的来源。只用于日志区分和提示策略，不参与业务状态判断。
/// </summary>
public enum InspectionActionSource
{
    /// <summary>PLC 轮询检测到信号（真实模式下实体按钮触发）</summary>
    PlcPolling,
    /// <summary>调试面板按钮直接触发（半实物/Fake 调试）</summary>
    DebugPanel,
    /// <summary>真实模式按钮（上位机界面按钮，非 PLC 轮询）</summary>
    RealModeButton,
    /// <summary>停止收口后自动补执行的复位请求。</summary>
    PendingAfterStop
}
