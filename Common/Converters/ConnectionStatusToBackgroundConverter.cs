using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using System.Windows.Media;

namespace WPFStandardFramework.Common.Converters
{
    /// <summary>
    /// 连接状态转背景色转换器
    /// 输入: "Green" / "Red" / "Gray"
    /// 输出: 对应的背景色
    /// </summary>
    public class ConnectionStatusToBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "Green" => new SolidColorBrush(Color.FromRgb(232, 245, 233)), // 浅绿色背景
                    "Red" => new SolidColorBrush(Color.FromRgb(255, 235, 238)),   // 浅红色背景
                    _ => new SolidColorBrush(Color.FromRgb(248, 249, 250))         // 浅灰色背景
                };
            }
            return new SolidColorBrush(Color.FromRgb(248, 249, 250));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// 连接状态转字体颜色转换器
    /// 输入: "Green" / "Red" / "Gray"
    /// 输出: 对应的字体颜色
    /// </summary>
    public class ConnectionStatusToForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "Green" => new SolidColorBrush(Color.FromRgb(46, 204, 113)), // 绿色
                    "Red" => new SolidColorBrush(Color.FromRgb(231, 76, 60)),    // 红色
                    _ => new SolidColorBrush(Color.FromRgb(127, 140, 141))        // 灰色
                };
            }
            return new SolidColorBrush(Color.FromRgb(127, 140, 141));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
