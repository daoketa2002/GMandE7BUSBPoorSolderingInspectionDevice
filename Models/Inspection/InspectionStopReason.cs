namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>检测停止原因（区分正常完成、单项 NG 停止、PLC 停止/急停/复位等）</summary>
public enum InspectionStopReason
{
    /// <summary>正常完成或未设定</summary>
    None,
    /// <summary>单项 NG 后按系统设置停止本轮</summary>
    SingleItemNg,
    /// <summary>PLC 停止信号 DT122</summary>
    PlcStop,
    /// <summary>PLC 复位信号 DT121</summary>
    Reset,
    /// <summary>PLC 急停信号 DT123</summary>
    EmergencyStop,
    /// <summary>终了按钮或终了控制动作主动结束本轮</summary>
    Terminate,
    /// <summary>检测过程中 PLC 或 DMM 断线导致本轮结束</summary>
    DeviceDisconnected,
    /// <summary>DT302 继电器动作完成超时</summary>
    RelayTimeout,
    /// <summary>无法恢复的异常</summary>
    Error,
    /// <summary>操作取消</summary>
    Canceled
}
