using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
    /// <summary>
    /// 判定结果转背景色
    /// OK → 绿色, NG → 红色, 空 → 浅灰
    /// </summary>
    public class JudgmentToBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                "OK" => Color.FromRgb(39, 174, 96),       // #27AE60 绿色
                "NG" => Color.FromRgb(231, 76, 60),       // #E74C3C 红色
                _ => Color.FromRgb(233, 236, 239)          // #E9ECEF 浅灰
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// 判定结果转字体颜色
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

    /// <summary>
    /// 连接状态转颜色
    /// true(已连接) → 绿色, false(断开) → 红色
    /// </summary>
    public class BoolToConnectionColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isConnected)
            {
                return isConnected ? Color.FromRgb(39, 174, 96) : Color.FromRgb(231, 76, 60);
            }
            return Color.FromRgb(189, 195, 199);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// 测试状态转颜色
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