using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
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
}
