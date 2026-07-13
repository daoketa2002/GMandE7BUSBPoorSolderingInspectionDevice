namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>
/// 单个检测项目的引脚名称和判定结果，最终作为 CSV 动态列保存。
/// </summary>
public class PinResult
{
    /// <summary>引脚名称，例如 A4-A5。</summary>
    public string PinName { get; set; } = string.Empty;

    /// <summary>测量值或判定结果。</summary>
    public string Result { get; set; } = string.Empty;
}
