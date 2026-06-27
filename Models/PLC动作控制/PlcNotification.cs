namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

/// <summary>
/// PLC 通信通知模型，用于表达连接失败、超时、写入失败、重连成功等事件。
/// 与 CommunicationNotification 互补：CommunicationNotification 用于通用串口事件，
/// 本模型专用于 PLC 层面的操作事件，包含操作名称以支持诊断。
/// </summary>
public class PlcNotification
{
    /// <summary>通知类型（如 "ConnectionLost", "ReconnectSucceeded", "WriteFailed"）</summary>
    public string Type { get; }

    /// <summary>通知消息内容</summary>
    public string Message { get; }

    /// <summary>触发此通知的操作名称（可选）</summary>
    public string? OperationName { get; }

    /// <summary>通知生成时间戳</summary>
    public DateTime Timestamp { get; } = DateTime.Now;

    public PlcNotification(string type, string message, string? operationName = null)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        OperationName = operationName;
    }

    public override string ToString()
        => $"[{Type}] {Timestamp:HH:mm:ss} ({OperationName}) {Message}";
}
