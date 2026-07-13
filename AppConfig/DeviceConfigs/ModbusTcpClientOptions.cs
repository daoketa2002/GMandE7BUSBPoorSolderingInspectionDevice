namespace GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;

/// <summary>
/// Modbus TCP 客户端连接选项（不可变模型）。
/// 从 FP0HCommunicationConfig 映射而来，供 IModbusTcpClient 使用。
/// 与 FP0HCommunicationConfig 分离的目的是让 Modbus 通信层不依赖具体 PLC 型号的配置模型。
/// </summary>
public class ModbusTcpClientOptions
{
    public string Host { get; init; } = "192.168.1.3";
    public int Port { get; init; } = 502;
    public byte UnitId { get; init; } = 1;

    /// <summary>
    /// 从 FP0HCommunicationConfig 创建客户端选项。
    /// 两个模型的属性一一映射，确保配置不丢失。
    /// </summary>
    public static ModbusTcpClientOptions From(FP0HCommunicationConfig config)
        => new()
        {
            Host = config.IpAddress,
            Port = config.Port,
            UnitId = checked((byte)config.SlaveId)
        };
}
