using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
    /// <summary>
    /// 判定结果转字体颜色
    /// OK/NG → 白色, 空 → 灰色
    /// </summary>
    public class JudgmentToForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                "OK" => Color.FromRgb(255, 255, 255),      // 白色
                "NG" => Color.FromRgb(255, 255, 255),      // 白色
                _ => Color.FromRgb(173, 181, 189)           // 灰色
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
