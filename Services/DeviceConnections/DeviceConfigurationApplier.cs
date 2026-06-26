using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;

/// <summary>
/// 将设备设置应用到具体硬件驱动，隔离配置模型与连接流程。
/// </summary>
public sealed class DeviceConfigurationApplier
{
    private readonly ILogger<DeviceConfigurationApplier> _logger;

    public DeviceConfigurationApplier(ILogger<DeviceConfigurationApplier> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 将 DeviceSettings 中的配置分别注入 PLC、万用表、扫描枪驱动。
    /// 注入失败时记录 Warning 日志，不抛出异常。
    /// </summary>
    public void Apply(DeviceSettings settings, IPlcDevice plcDevice, IMultimeterDevice dmmDevice, IScannerDevice scannerDevice)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplyPlc(settings, plcDevice);
        ApplyDmm(settings, dmmDevice);
        ApplyScanner(settings, scannerDevice);
    }

    private void ApplyPlc(DeviceSettings settings, IPlcDevice plcDevice)
    {
        if (settings.FP0HCommunication is null || plcDevice is not PlcCommunicationAdapter plcAdapter)
        {
            _logger.LogWarning("PLC配置注入失败，Settings={HasSettings}, Device={DeviceType}",
                settings.FP0HCommunication is not null, plcDevice.GetType().Name);
            return;
        }

        plcAdapter.ApplyConfig(settings.FP0HCommunication);
        _logger.LogDebug("已注入PLC配置: {Host}:{Port}",
            settings.FP0HCommunication.IpAddress, settings.FP0HCommunication.Port);
    }

    private void ApplyDmm(DeviceSettings settings, IMultimeterDevice dmmDevice)
    {
        if (settings.GDM9060Communication is null || dmmDevice is not GwInstekGDM9060Driver dmmDriver)
        {
            _logger.LogWarning("万用表配置注入失败，Settings={HasSettings}, Device={DeviceType}",
                settings.GDM9060Communication is not null, dmmDevice.GetType().Name);
            return;
        }

        dmmDriver.Host = settings.GDM9060Communication.IpAddress;
        dmmDriver.Port = settings.GDM9060Communication.Port;
        dmmDriver.TimeoutMs = settings.GDM9060Communication.ReceiveTimeoutMs;
        _logger.LogDebug("已注入万用表配置: {Host}:{Port}", dmmDriver.Host, dmmDriver.Port);
    }

    private void ApplyScanner(DeviceSettings settings, IScannerDevice scannerDevice)
    {
        if (settings.ScannerSerialCommunication is null || scannerDevice is not HoneywellH1900Scanner scanner)
        {
            _logger.LogWarning("扫描枪配置注入失败，Settings={HasSettings}, Device={DeviceType}",
                settings.ScannerSerialCommunication is not null, scannerDevice.GetType().Name);
            return;
        }

        scanner.PortName = settings.ScannerSerialCommunication.SerialNumber;
        scanner.BaudRate = settings.ScannerSerialCommunication.BaudRate;
        _logger.LogInformation("已注入扫描枪配置: Port={Port}, BaudRate={BaudRate}",
            scanner.PortName, scanner.BaudRate);
    }
}
