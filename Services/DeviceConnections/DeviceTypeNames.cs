namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 设备类型名称常量，避免连接管理代码中散落硬编码字符串。
/// </summary>
public static class DeviceTypeNames
{
    public const string Plc = "PLC";
    public const string Dmm = "DMM";
    public const string Scanner = "Scanner";

    public static bool IsKnown(string deviceType) =>
        deviceType is Plc or Dmm or Scanner;
}
