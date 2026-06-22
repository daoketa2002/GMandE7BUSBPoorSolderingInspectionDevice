using System;
using System.Globalization;
using System.Windows.Data;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Converters
{
    /// <summary>
    /// 整数 +1 转换器
    /// 用于 DataGrid AlternationIndex 从0开始改为从1开始显示
    /// </summary>
    public class IntPlusOneConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue)
                return intValue + 1;
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue)
                return intValue - 1;
            return value;
        }
    }
}