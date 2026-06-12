using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
    /// <summary>
    /// 测试状态转颜色
    /// 待机→蓝, 测试中→橙, 良品→绿, 不良/报错→红, 其他→灰
    /// </summary>
    public class TestStatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "待机" => Color.FromRgb(52, 152, 219),         // #3498DB 蓝色
                    "测试中" => Color.FromRgb(243, 156, 18),       // #F39C12 橙色
                    "测试完成 - 良品" => Color.FromRgb(39, 174, 96), // #27AE60 绿色
                    "测试完成 - 不良" => Color.FromRgb(231, 76, 60), // #E74C3C 红色
                    "报错" => Color.FromRgb(231, 76, 60),          // #E74C3C 红色
                    _ => Color.FromRgb(149, 165, 166)               // #95A5A6 灰色
                };
            }
            return Color.FromRgb(149, 165, 166);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
