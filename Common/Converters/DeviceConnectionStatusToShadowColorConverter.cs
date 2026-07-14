// ============================================================
// 文件: Common/Converters/DeviceConnectionStatusToShadowColorConverter.cs
// 描述: 设备连接状态枚举 → 底部阴影色
//       在原基础色上加深40%，用于按钮3D立体阴影效果
//       与 DeviceConnectionStatusToColorConverter 配套使用
//       - 前者输出基础色（用于 Background）
//       - 本转换器输出阴影色（用于 BorderBrush）
// ============================================================

using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
    /// <summary>
    /// 设备连接状态 → 按钮底部阴影色
    /// 
    /// 颜色对照表：
    ///   Connected    : 基础色 #27AE60 → 阴影色 #186A40
    ///   Connecting   : 基础色 #3498DB → 阴影色 #1F5B83
    ///   Disconnected : 基础色 #E74C3C → 阴影色 #8B1A1A
    ///   其他         : 基础色 #BDC3C7 → 阴影色 #717578
    /// 
    /// 阴影系数: 0.6（保留原色60%亮度，即加深40%）
    /// </summary>
    public class DeviceConnectionStatusToShadowColorConverter : IValueConverter
    {
        /// <summary>
        /// 阴影加深系数（0.0~1.0），值越小阴影越深
        /// 0.6 = 保留60%亮度，与终了按钮 #C0392B/#E74C3C≈0.83 相比更深，
        /// 因为绿/蓝色系需要更大的加深幅度才能产生明显立体感
        /// </summary>
        private const double ShadowFactor = 0.6;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 1. 根据连接状态获取基础色（与 DeviceConnectionStatusToColorConverter 保持一致）
            Color baseColor = value is DeviceConnectionStatus status
                ? status switch
                {
                    DeviceConnectionStatus.Connected => Color.FromRgb(39, 174, 96),   // #27AE60
                    DeviceConnectionStatus.Connecting => Color.FromRgb(52, 152, 219),  // #3498DB
                    DeviceConnectionStatus.Disconnected => Color.FromRgb(231, 76, 60),   // #E74C3C
                    _ => Color.FromRgb(189, 195, 199)  // #BDC3C7
                }
                : Color.FromRgb(189, 195, 199); // 非枚举值默认灰色

            // 2. 按阴影系数加深
            byte r = (byte)(baseColor.R * ShadowFactor);
            byte g = (byte)(baseColor.G * ShadowFactor);
            byte b = (byte)(baseColor.B * ShadowFactor);

            return Color.FromRgb(r, g, b);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 阴影色不需要反向转换
            return Binding.DoNothing;
        }
    }
}