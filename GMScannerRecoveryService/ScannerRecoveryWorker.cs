using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GMScannerRecoveryService;

/// <summary>Windows 服务后台入口，持续监听本机恢复命名管道。</summary>
public sealed class ScannerRecoveryWorker : BackgroundService
{
    private readonly ILogger<ScannerRecoveryWorker> _logger;
    private readonly ScannerRecoveryPipeServer _pipeServer;

    public ScannerRecoveryWorker(
        ILogger<ScannerRecoveryWorker> logger,
        ScannerRecoveryPipeServer pipeServer)
    {
        _logger = logger;
        _pipeServer = pipeServer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[扫描枪服务] 已启动: ServiceName={ServiceName}, PipeName={PipeName}, ExpectedVidPid={VidPid}",
            ScannerRecoveryConstants.ServiceName,
            ScannerRecoveryConstants.PipeName,
            ScannerRecoveryConstants.ExpectedVidPid);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _pipeServer.WaitForRequestAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[扫描枪服务][管道] 监听循环异常，将继续监听");
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("[扫描枪服务] 正在停止");
    }
}
