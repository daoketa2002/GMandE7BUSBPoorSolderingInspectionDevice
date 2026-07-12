using GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>
/// 配置保存后生产设备重连的结果摘要，用于区分“保存成功”和“连接成功”。
/// </summary>
public sealed record DeviceReconnectResult(
    string DeviceType,
    bool Requested,
    bool IsConnected,
    string StatusText)
{
    public static DeviceReconnectResult NotRequested(string deviceType, bool isConnected, string statusText)
        => new(deviceType, false, isConnected, statusText);
}

public sealed record DeviceReconnectSummary(
    DeviceReconnectResult Plc,
    DeviceReconnectResult Dmm,
    DeviceReconnectResult Scanner)
{
    public bool HasRequestedReconnect => Plc.Requested || Dmm.Requested || Scanner.Requested;

    public IEnumerable<DeviceReconnectResult> Results
    {
        get
        {
            yield return Plc;
            yield return Dmm;
            yield return Scanner;
        }
    }

    public static DeviceReconnectSummary NoChanges(
        bool plcConnected,
        bool dmmConnected,
        bool scannerConnected,
        string plcStatusText,
        string dmmStatusText,
        string scannerStatusText)
        => new(
            DeviceReconnectResult.NotRequested(DeviceTypeNames.Plc, plcConnected, plcStatusText),
            DeviceReconnectResult.NotRequested(DeviceTypeNames.Dmm, dmmConnected, dmmStatusText),
            DeviceReconnectResult.NotRequested(DeviceTypeNames.Scanner, scannerConnected, scannerStatusText));
}
