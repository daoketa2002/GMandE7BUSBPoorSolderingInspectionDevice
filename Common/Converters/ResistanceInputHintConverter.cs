using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using System;
using System.Globalization;
using System.Windows.Data;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters;

/// <summary>
/// 将电阻输入框中的文本实时转换为量程/分辨率提示。
/// 保存值仍由 ViewModel 的 double? 属性接收，这里只服务界面智能提醒，不改动用户输入。
/// </summary>
public sealed class ResistanceInputHintConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return InputValidationHelper.GetResistanceInputHint(null);

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ohms)
            && !double.TryParse(text, NumberStyles.Float, culture, out ohms))
        {
            return "提示：请输入有效的电阻数值。";
        }

        return InputValidationHelper.GetResistanceInputHint(ohms);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
