using Microsoft.Extensions.Configuration;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Logging;

/// <summary>
/// 根据现有硬件配置解析应用程序运行模式。
/// Fake 优先于半实物，两个开关都关闭时按真实模式处理。
/// </summary>
public static class ApplicationRunModeResolver
{
    /// <summary>解析日志使用的运行模式名称。</summary>
    public static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.GetValue<bool>("Hardware:UseFakeInspectionHardware"))
        {
            return "Fake";
        }

        return configuration.GetValue<bool>("Hardware:SemiPhysicalDebug")
            ? "SemiPhysical"
            : "Real";
    }
}
