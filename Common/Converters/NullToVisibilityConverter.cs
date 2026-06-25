// ============================================================
// 文件: Common/Converters/NullToVisibilityConverter.cs
// 描述: 将 null 值转换为 Collapsed，非 null 转换为 Visible
// 用于控制 ×清除按钮 等需要根据引用类型是否为null来显隐的场景
// ============================================================

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
    /// <summary>
    /// null → Collapsed，非null → Visible 的转换器
    /// 典型用途：DatePicker 的 ×清除按钮显隐控制
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        /// <summary>
        /// value为null → Collapsed；value不为null → Visible
        /// </summary>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// 不需要反向转换
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}