using System;
using System.IO;
using System.Linq;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;

/// <summary>
/// 校验参与文件路径和 CSV 文件名协议的业务名称。
/// </summary>
public static class NameValidationHelper
{
    public static string? ValidateMachineType(string value)
    {
        return ValidateName(value, "机种名称", InputValidationHelper.MaxMachineTypeLength);
    }

    public static string? ValidatePlanName(string value)
    {
        return ValidateName(value, "方案名称", InputValidationHelper.MaxPlanNameLength);
    }

    /// <summary>校验系列名称，系列名称允许下划线但不能破坏 Windows 目录。</summary>
    public static string? ValidateSeriesName(string value)
    {
        var trimmedValue = value?.Trim() ?? string.Empty;
        if (trimmedValue.Length == 0)
            return "系列名称不能为空。";

        if (trimmedValue.Length > 30)
            return "系列名称不能超过30个字符。";

        if (trimmedValue.Any(char.IsControl))
            return "系列名称不能包含控制字符。";

        if (trimmedValue.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "系列名称不能包含 Windows 文件名非法字符。";

        return null;
    }

    private static string? ValidateName(string value, string displayName, int maxLength)
    {
        var trimmedValue = value?.Trim() ?? string.Empty;
        if (trimmedValue.Length == 0)
            return $"{displayName}不能为空。";

        if (trimmedValue.Length > maxLength)
            return $"{displayName}不能超过{maxLength}个字符。";

        if (trimmedValue.Contains('_'))
            return $"{displayName}不能包含下划线“_”，该字符用于测试数据文件名分隔。";

        if (trimmedValue.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return $"{displayName}不能包含 Windows 文件名非法字符。";

        return null;
    }
}
