using Serilog.Core;
using Serilog.Events;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Logging;

/// <summary>
/// 将 Serilog 的完整 SourceContext 转成短类名，避免日志中出现过长命名空间。
/// </summary>
public sealed class SourceContextShortNameEnricher : ILogEventEnricher
{
    public const string PropertyName = "SourceContextShortName";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var shortName = GetShortName(logEvent);
        var property = propertyFactory.CreateProperty(PropertyName, shortName);
        logEvent.AddOrUpdateProperty(property);
    }

    private static string GetShortName(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out var sourceContextProperty))
            return "-";

        if (sourceContextProperty is not ScalarValue { Value: string sourceContext }
            || string.IsNullOrWhiteSpace(sourceContext))
            return "-";

        var lastDotIndex = sourceContext.LastIndexOf('.');
        return lastDotIndex >= 0 && lastDotIndex < sourceContext.Length - 1
            ? sourceContext[(lastDotIndex + 1)..]
            : sourceContext;
    }
}
