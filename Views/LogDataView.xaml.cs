// ============================================================
// 文件: Views/LogDataView.xaml.cs
// 描述: 日志数据页面的代码隐藏 —— 负责运行时动态生成 DataGrid 列
// 修改:
//   - 日期和时间列改为动态生成，放在动态Pin列之后
// ============================================================

using CommunityToolkit.Mvvm.ComponentModel;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// 日志数据页面的代码隐藏
    /// 负责运行时根据 DynamicHeaders 集合动态生成 DataGrid 列
    /// 
    /// 列结构：固定列（XAML静态定义）→ 动态Pin列（代码生成）→ 日期列 → 时间列
    /// 固定列：序号、机种名称、序列号、方案名称、操作员、综合判定（共6列）
    /// </remarks>
    [NavigationViewModel(typeof(LogDataViewModel))]
    public partial class LogDataView : UserControl
    {
        private readonly LogDataViewModel _viewModel;

        /// <summary>
        /// ⭐ 固定列数量改为6：序号、机种名称、序列号、方案名称、操作员、综合判定
        /// （日期和时间列移到动态生成区域）
        /// </summary>
        private const int FIXED_COLUMN_COUNT = 6;

        public LogDataView(LogDataViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;

            // 监听 DynamicHeaders 属性变化 → 触发列重建
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LogDataViewModel.DynamicHeaders))
                {
                    GenerateDynamicColumns();
                }
            };

            // 监听 DynamicHeaders 集合内容的增删改 → 触发列重建
            _viewModel.DynamicHeaders.CollectionChanged += (s, e) =>
            {
                GenerateDynamicColumns();
            };

            // 页面首次加载完成后生成动态列
            Loaded += (s, e) => GenerateDynamicColumns();
        }

        /// <summary>
        /// 根据 ViewModel.DynamicHeaders 集合动态生成 DataGrid 列
        /// 
        /// 列顺序：动态Pin列 → 日期列 → 时间列
        /// </summary>
        private void GenerateDynamicColumns()
        {
            // 确保在UI线程执行
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(GenerateDynamicColumns);
                return;
            }

            // 清除上一次生成的动态列（保留XAML中定义的固定列）
            RemoveDynamicColumns();

            // ⭐ 第一步：为每个动态Pin列名创建一个 DataGridTextColumn
            foreach (var header in _viewModel.DynamicHeaders)
            {
                var column = new DataGridTextColumn
                {
                    Header = header,
                    Width = 100,
                    Binding = new Binding("PinResults")
                    {
                        Converter = new PinResultValueConverter(),
                        ConverterParameter = header,
                        Mode = BindingMode.OneWay
                    }
                };
                LogDataGrid.Columns.Add(column);
            }

            // ⭐ 第二步：添加日期列（放在动态Pin列之后）
            var dateColumn = new DataGridTextColumn
            {
                Header = "日期",
                Width = 130,
                Binding = new Binding("Timestamp")
                {
                    StringFormat = "yyyy年MM月dd日",
                    Mode = BindingMode.OneWay
                }
            };
            LogDataGrid.Columns.Add(dateColumn);

            // ⭐ 第三步：添加时间列（放在日期列之后）
            var timeColumn = new DataGridTextColumn
            {
                Header = "时间",
                Width = 120,
                Binding = new Binding("Timestamp")
                {
                    StringFormat = "HH时mm分ss秒",
                    Mode = BindingMode.OneWay
                }
            };
            LogDataGrid.Columns.Add(timeColumn);
        }

        /// <summary>
        /// 移除运行时生成的动态列，仅保留XAML中定义的固定列
        /// ⭐ 固定列 = 序号、机种名称、序列号、方案名称、操作员、综合判定（共6列）
        /// </summary>
        private void RemoveDynamicColumns()
        {
            while (LogDataGrid.Columns.Count > FIXED_COLUMN_COUNT)
            {
                LogDataGrid.Columns.RemoveAt(FIXED_COLUMN_COUNT);
            }
        }
    }

    /// <summary>
    /// Pin结果值转换器
    /// 从 List&lt;PinResult&gt; 集合中查找指定 PinName 对应的 Result 值
    /// 如果集合为 null 或找不到指定 PinName，返回 "-"
    /// </summary>
    public class PinResultValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter,
            System.Globalization.CultureInfo culture)
        {
            var pinName = parameter as string;
            if (string.IsNullOrWhiteSpace(pinName))
                return "-";

            if (value is System.Collections.IList pinResults)
            {
                foreach (var item in pinResults)
                {
                    var itemType = item.GetType();
                    var nameProp = itemType.GetProperty("PinName");
                    var resultProp = itemType.GetProperty("Result");

                    if (nameProp != null && resultProp != null)
                    {
                        var name = nameProp.GetValue(item) as string;
                        if (string.Equals(name, pinName, StringComparison.OrdinalIgnoreCase))
                        {
                            return resultProp.GetValue(item) ?? "-";
                        }
                    }
                }
            }

            return "-";
        }

        public object ConvertBack(object value, Type targetType, object parameter,
            System.Globalization.CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}