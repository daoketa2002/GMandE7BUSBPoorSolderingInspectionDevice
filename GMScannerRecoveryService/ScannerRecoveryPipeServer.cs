using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using GMScannerRecoveryService.Models;
using Microsoft.Extensions.Logging;

namespace GMScannerRecoveryService;

/// <summary>单请求命名管道服务端，避免恢复请求并发操作同一设备。</summary>
public sealed class ScannerRecoveryPipeServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<ScannerRecoveryPipeServer> _logger;
    private readonly ScannerDeviceLocator _deviceLocator;
    private readonly PnpDeviceController _pnpDeviceController;
    private readonly SemaphoreSlim _recoveryLock = new(1, 1);

    public ScannerRecoveryPipeServer(
        ILogger<ScannerRecoveryPipeServer> logger,
        ScannerDeviceLocator deviceLocator,
        PnpDeviceController pnpDeviceController)
    {
        _logger = logger;
        _deviceLocator = deviceLocator;
        _pnpDeviceController = pnpDeviceController;
    }

    public async Task WaitForRequestAsync(CancellationToken cancellationToken)
    {
        await using var pipe = NamedPipeServerStreamAcl.Create(
            ScannerRecoveryConstants.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            CreatePipeSecurity(),
            HandleInheritability.None);

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        ScannerRecoveryResponse response;
        try
        {
            var requestJson = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var request = string.IsNullOrWhiteSpace(requestJson)
                ? null
                : JsonSerializer.Deserialize<ScannerRecoveryRequest>(requestJson, JsonOptions);
            response = request is null
                ? ScannerRecoveryResponse.Failure("InvalidRequest", "恢复请求为空")
                : await HandleRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            response = ScannerRecoveryResponse.Failure("InvalidRequest", "恢复请求格式无效");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[扫描枪服务][管道] 处理恢复请求异常");
            response = ScannerRecoveryResponse.Failure("ServiceError", "扫描枪恢复服务处理失败");
        }

        var responseJson = JsonSerializer.Serialize(response, JsonOptions);
        await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
    }

    /// <summary>
    /// 为管理员服务创建的命名管道授予本机普通用户必要的读写权限。
    /// 使用固定 SID，避免不同 Windows 中文化环境下的组名差异。
    /// </summary>
    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return security;
    }

    private async Task<ScannerRecoveryResponse> HandleRequestAsync(
        ScannerRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Command, ScannerRecoveryConstants.RestartCommand, StringComparison.Ordinal))
            return ScannerRecoveryResponse.Failure("UnsupportedCommand", "不支持的恢复命令", request.PortName);

        var startedAt = Stopwatch.GetTimestamp();
        if (!await _recoveryLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return ScannerRecoveryResponse.Failure("Busy", "已有扫描枪恢复请求正在执行", request.PortName);

        try
        {
            var device = _deviceLocator.FindScanner(request.PortName);
            if (device is null)
                return ScannerRecoveryResponse.Failure(
                    "ScannerNotMatched",
                    "配置的 COM 口不存在，或该端口不是受支持的 Honeywell 扫描枪",
                    request.PortName);

            _logger.LogWarning(
                "[扫描枪服务][审计] 开始恢复: Port={Port}, DeviceName={DeviceName}, DeviceInstanceId={DeviceInstanceId}, ExpectedVendorId={ExpectedVendorId}",
                device.PortName,
                device.DeviceName,
                device.DeviceInstanceId,
                ScannerRecoveryConstants.ExpectedVendorId);

            var result = await _pnpDeviceController
                .RestartAsync(device, cancellationToken)
                .ConfigureAwait(false);
            var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            _logger.LogWarning(
                "[扫描枪服务][审计] 恢复结束: Port={Port}, ResultCode={ResultCode}, ElapsedMs={ElapsedMs}",
                device.PortName,
                result.ResultCode,
                elapsedMs);

            return new ScannerRecoveryResponse
            {
                Succeeded = result.Succeeded,
                ResultCode = result.ResultCode,
                Message = result.Message,
                PortName = device.PortName,
                ElapsedMs = elapsedMs
            };
        }
        finally
        {
            _recoveryLock.Release();
        }
    }
}
