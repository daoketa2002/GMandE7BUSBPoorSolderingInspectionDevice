using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
    /// <summary>
    /// 设备连接三态枚举转颜色：
    /// Connected → 绿色 (#27AE60)
    /// Connecting → 蓝色 (#3498DB)
    /// Disconnected → 红色 (#E74C3C)
    /// </summary>
    public class DeviceConnectionStatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DeviceConnectionStatus status)
            {
                return status switch
                {
                    DeviceConnectionStatus.Connected => Color.FromRgb(39, 174, 96),
                    DeviceConnectionStatus.Connecting => Color.FromRgb(52, 152, 219),
                    DeviceConnectionStatus.Disconnected => Color.FromRgb(231, 76, 60),
                    _ => Color.FromRgb(189, 195, 199)
                };
            }
            return Color.FromRgb(189, 195, 199);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
