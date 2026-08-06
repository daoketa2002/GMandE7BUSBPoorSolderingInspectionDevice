using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services;

/// <summary>通过本机命名管道调用扫描枪恢复服务。</summary>
public sealed class ScannerRecoveryPipeClient : IScannerRecoveryClient
{
    private const string PipeName = "GM.ScannerRecovery.v1";
    private const string RestartCommand = "RestartScanner";
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ScannerRecoveryPipeClient> _logger;

    public ScannerRecoveryPipeClient(ILogger<ScannerRecoveryPipeClient> logger)
    {
        _logger = logger;
    }

    public async Task<ScannerRecoveryResult> RestartAsync(
        string portName,
        CancellationToken cancellationToken = default)
    {
        var normalizedPort = NormalizeComPort(portName);
        if (normalizedPort is null)
        {
            _logger.LogWarning("[扫描枪恢复客户端][拒绝] COM 口格式无效: Port={Port}", portName);
            return ScannerRecoveryResult.Failure("InvalidPort", "扫描枪 COM 口配置无效", portName);
        }

        var startedAt = Stopwatch.GetTimestamp();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(PipeConnectTimeout, timeoutCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };

            var request = new ScannerRecoveryRequest
            {
                Command = RestartCommand,
                PortName = normalizedPort
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions))
                .ConfigureAwait(false);

            var responseJson = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return ScannerRecoveryResult.Failure(
                    "EmptyResponse",
                    "扫描枪恢复服务未返回结果",
                    normalizedPort);
            }

            var result = JsonSerializer.Deserialize<ScannerRecoveryResult>(responseJson, JsonOptions);
            if (result is null)
            {
                return ScannerRecoveryResult.Failure(
                    "InvalidResponse",
                    "扫描枪恢复服务返回结果无效",
                    normalizedPort);
            }

            _logger.LogInformation(
                "[扫描枪恢复客户端] 请求完成: Port={Port}, ResultCode={ResultCode}, Succeeded={Succeeded}, ElapsedMs={ElapsedMs}",
                normalizedPort,
                result.ResultCode,
                result.Succeeded,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("[扫描枪恢复客户端] 请求被取消: Port={Port}", normalizedPort);
            return ScannerRecoveryResult.Failure("Canceled", "扫描枪恢复已取消", normalizedPort);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[扫描枪恢复客户端] 请求超时: Port={Port}", normalizedPort);
            return ScannerRecoveryResult.Failure("Timeout", "扫描枪深度恢复服务响应超时", normalizedPort);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "[扫描枪恢复客户端] 服务未在连接时限内响应: Port={Port}", normalizedPort);
            return ScannerRecoveryResult.Failure(
                "ServiceUnavailable",
                "扫描枪深度恢复服务未安装或未运行，请联系维护人员处理。",
                normalizedPort);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[扫描枪恢复客户端] 命名管道连接失败: Port={Port}", normalizedPort);
            return ScannerRecoveryResult.Failure(
                "ServiceUnavailable",
                "扫描枪深度恢复服务未安装或未运行，请联系维护人员处理。",
                normalizedPort);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[扫描枪恢复客户端] 服务响应解析失败: Port={Port}", normalizedPort);
            return ScannerRecoveryResult.Failure("InvalidResponse", "扫描枪恢复服务返回结果无效", normalizedPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[扫描枪恢复客户端] 请求异常: Port={Port}", normalizedPort);
            return ScannerRecoveryResult.Failure("ClientError", "扫描枪深度恢复请求失败", normalizedPort);
        }
    }

    private static string? NormalizeComPort(string? portName)
    {
        if (!InputValidationHelper.IsValidComPort(portName))
            return null;

        var number = int.Parse(portName!.Trim()[3..]);
        return $"COM{number}";
    }
}
