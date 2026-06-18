using System;
using System.Globalization;
using System.Windows.Data;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
    /// <summary>
    /// bool 值转 Opacity 转换器
    /// true（启用）→ 1.0 完全不透明
    /// false（禁用）→ 0.5 半透明
    /// 用于分页按钮等需要禁用态视觉反馈的场景
    /// </summary>
    public class BoolToOpacityConverter : IValueConverter
    {
        /// <summary>
        /// 启用状态的透明度
        /// </summary>
        public double EnabledOpacity { get; set; } = 1.0;

        /// <summary>
        /// 禁用状态的透明度
        /// </summary>
        public double DisabledOpacity { get; set; } = 0.5;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isEnabled)
            {
                return isEnabled ? EnabledOpacity : DisabledOpacity;
            }
            return EnabledOpacity;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}