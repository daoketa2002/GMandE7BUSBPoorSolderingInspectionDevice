using Microsoft.Extensions.Logging;

/// <summary>
/// 用于标记 PLC 请求来源的日志作用域。
/// DIAG-TEMP: 阶段 C 新增，用于超时根因排查时区分请求来源。
/// 用法：using (new PlcCallerScope(logger, "UiPolling")) { ... }
/// </summary>
sealed class PlcCallerScope : IDisposable
{
    private readonly IDisposable _scope;

    public PlcCallerScope(ILogger logger, string caller)
    {
        _scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["PlcCaller"] = caller
        });
    }

    public void Dispose()
    {
        _scope?.Dispose();
    }
}
