using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GMScannerRecoveryService;

/// <summary>
/// 根据当前请求的 COM 口动态定位扫描枪设备。
/// 不持久化完整设备实例 ID，也不把具体端口写入服务逻辑。
/// </summary>
public sealed class ScannerDeviceLocator
{
    private static readonly Regex ComPortRegex = new(
        "^COM(?<number>[0-9]{1,3})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ILogger<ScannerDeviceLocator> _logger;

    public ScannerDeviceLocator(ILogger<ScannerDeviceLocator> logger)
    {
        _logger = logger;
    }

    public ScannerDeviceInfo? FindScanner(string portName)
    {
        if (!TryNormalizeComPort(portName, out var normalizedPort))
        {
            _logger.LogWarning("[扫描枪服务][拒绝] COM 口格式无效: Port={Port}", portName);
            return null;
        }

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, PNPDeviceID, Name FROM Win32_SerialPort");

        foreach (ManagementObject device in searcher.Get())
        {
            using (device)
            {
                var deviceId = device["DeviceID"]?.ToString();
                if (!string.Equals(deviceId, normalizedPort, StringComparison.OrdinalIgnoreCase))
                    continue;

                var pnpDeviceId = device["PNPDeviceID"]?.ToString() ?? string.Empty;
                var deviceName = device["Name"]?.ToString() ?? string.Empty;
                var vidPidMatched = pnpDeviceId.Contains(
                    ScannerRecoveryConstants.ExpectedVidPid,
                    StringComparison.OrdinalIgnoreCase);

                _logger.LogInformation(
                    "[扫描枪服务][定位] Port={Port}, DeviceName={DeviceName}, DeviceInstanceId={DeviceInstanceId}, VidPidMatched={VidPidMatched}",
                    normalizedPort,
                    deviceName,
                    pnpDeviceId,
                    vidPidMatched);

                if (!vidPidMatched)
                    return null;

                return new ScannerDeviceInfo(normalizedPort, deviceName, pnpDeviceId);
            }
        }

        _logger.LogWarning("[扫描枪服务][拒绝] 未找到 COM 口对应的 Win32_SerialPort: Port={Port}", normalizedPort);
        return null;
    }

    public bool IsPortPresent(string portName)
    {
        if (!TryNormalizeComPort(portName, out var normalizedPort))
            return false;

        return SerialPort.GetPortNames()
            .Any(port => string.Equals(port, normalizedPort, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryNormalizeComPort(string? portName, out string normalizedPort)
    {
        normalizedPort = string.Empty;
        var match = ComPortRegex.Match(portName?.Trim() ?? string.Empty);
        if (!match.Success
            || !int.TryParse(match.Groups["number"].Value, out var portNumber)
            || portNumber is < 1 or > ScannerRecoveryConstants.MaxComPortNumber)
        {
            return false;
        }

        normalizedPort = $"COM{portNumber}";
        return true;
    }
}

public sealed record ScannerDeviceInfo(
    string PortName,
    string DeviceName,
    string DeviceInstanceId);
