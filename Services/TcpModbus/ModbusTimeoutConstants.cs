namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;

/// <summary>
/// Phase E3 Timeout 分层：集中管理 Modbus 请求超时，避免魔法数字散落。
/// 所有普通 Modbus 请求的超时配置集中在此，业务等待超时不在此列。
/// </summary>
public static class ModbusTimeoutConstants
{
    /// <summary>
    /// 控制信号轮询读取超时（UiPolling DT120~123）。
    /// 推荐值 300~500ms，单次失败快速返回避免卡死控制响应。
    /// Phase E3 候选值 = 500ms。
    /// </summary>
    public const int ControlReadMs = 500;

    /// <summary>
    /// 普通 Modbus 请求超时（WritePins / ClearPins / DT234 / DT302 清除 / DT304~305 / 单寄存器写入等）。
    /// 推荐值 500~1000ms，Phase E3 候选值 = 1000ms。
    /// </summary>
    public const int NormalRequestMs = 1000;

    /// <summary>
    /// 业务等待 DT302 继电器动作完成的总超时（不是单个 Modbus 请求超时，而是轮询循环的截止时间）。
    /// 保持 3000ms，由 PLC 继电器实际动作时间确定。
    /// </summary>
    public const int RelayBusinessWaitMs = 3000;
}
