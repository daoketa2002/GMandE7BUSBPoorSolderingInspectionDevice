using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GMScannerRecoveryService;

/// <summary>使用 pnputil 重启已校验的扫描枪设备。</summary>
public sealed class PnpDeviceController
{
    private readonly ILogger<PnpDeviceController> _logger;
    private readonly ScannerDeviceLocator _deviceLocator;

    public PnpDeviceController(
        ILogger<PnpDeviceController> logger,
        ScannerDeviceLocator deviceLocator)
    {
        _logger = logger;
        _deviceLocator = deviceLocator;
    }

    public async Task<(bool Succeeded, string ResultCode, string Message)> RestartAsync(
        ScannerDeviceInfo device,
        CancellationToken cancellationToken)
    {
        var disableResult = await RunPnpUtilAsync(
            "/disable-device",
            device.DeviceInstanceId,
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "[扫描枪服务][PnP] 禁用返回码={ExitCode}, Port={Port}",
            disableResult.ExitCode,
            device.PortName);

        if (disableResult.ExitCode != 0)
            return (false, "DisableFailed", "禁用扫描枪设备失败");

        await Task.Delay(
            ScannerRecoveryConstants.DisableEnableGapMilliseconds,
            cancellationToken).ConfigureAwait(false);

        var enableResult = await RunPnpUtilAsync(
            "/enable-device",
            device.DeviceInstanceId,
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "[扫描枪服务][PnP] 首次启用返回码={ExitCode}, Port={Port}",
            enableResult.ExitCode,
            device.PortName);

        if (enableResult.ExitCode != 0)
        {
            enableResult = await RunPnpUtilAsync(
                "/enable-device",
                device.DeviceInstanceId,
                cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "[扫描枪服务][PnP] 首次启用失败，已执行第二次启用，返回码={ExitCode}, Port={Port}",
                enableResult.ExitCode,
                device.PortName);
        }

        if (enableResult.ExitCode != 0)
            return (false, "EnableFailed", "启用扫描枪设备失败，请到设备管理器手动启用");

        var waitStart = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(ScannerRecoveryConstants.DeviceReappearTimeoutSeconds);
        while (!_deviceLocator.IsPortPresent(device.PortName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(waitStart) >= timeout)
                return (false, "PortReappearTimeout", "扫描枪 COM 口未在规定时间内重新出现");

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        var elapsedMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        _logger.LogInformation(
            "[扫描枪服务][PnP] COM 口已重新出现，等待设备稳定: Port={Port}, DelayMs={DelayMs}, ElapsedMs={ElapsedMs}",
            device.PortName,
            ScannerRecoveryConstants.PostEnableStabilizationMilliseconds,
            elapsedMs);

        await Task.Delay(
            ScannerRecoveryConstants.PostEnableStabilizationMilliseconds,
            cancellationToken).ConfigureAwait(false);

        if (!_deviceLocator.IsPortPresent(device.PortName))
            return (false, "PortUnstable", "扫描枪 COM 口重新出现后再次消失");

        var confirmedDevice = _deviceLocator.FindScanner(device.PortName);
        if (confirmedDevice is null
            || !string.Equals(
                confirmedDevice.DeviceInstanceId,
                device.DeviceInstanceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return (false, "DeviceRecheckFailed", "扫描枪设备重新启用后身份确认失败");
        }

        _logger.LogInformation(
            "[扫描枪服务][PnP] 设备稳定确认完成: Port={Port}, DeviceInstanceId={DeviceInstanceId}",
            confirmedDevice.PortName,
            confirmedDevice.DeviceInstanceId);
        return (true, "Success", "扫描枪设备重启成功");
    }

    private static async Task<PnpUtilResult> RunPnpUtilAsync(
        string operation,
        string deviceInstanceId,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(operation);
        process.StartInfo.ArgumentList.Add(deviceInstanceId);

        try
        {
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return new PnpUtilResult(process.ExitCode, outputTask.Result, errorTask.Result);
        }
        catch (Win32Exception ex)
        {
            return new PnpUtilResult(-1, string.Empty, ex.Message);
        }
    }

    private sealed record PnpUtilResult(int ExitCode, string Output, string Error);
}
