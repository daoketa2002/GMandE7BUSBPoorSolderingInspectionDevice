using System;
using System.IO;

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
