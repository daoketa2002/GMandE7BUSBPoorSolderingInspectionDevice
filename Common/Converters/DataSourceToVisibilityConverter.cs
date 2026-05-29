using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
    /// <summary>
    /// 数据源到可见性的转换器
    /// </summary>
    public class DataSourceToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return Visibility.Collapsed;

            string currentSource = value.ToString();
            string targetSource = parameter.ToString();

            // 马达特性履历（前工程）→ ProcessHistory 表
            if (targetSource == "马达特性履历" && currentSource == "马达特性履历")
                return Visibility.Visible;

            // 风扇压入机履历（后工程）→ InspectionHistory 表
            if (targetSource == "风扇压入机履历" && currentSource == "风扇压入机履历")
                return Visibility.Visible;

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

}
