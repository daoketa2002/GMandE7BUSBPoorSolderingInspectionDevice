// ============================================================
// 文件: Views/LogDataView.xaml.cs
// 描述: 日志数据页面的代码隐藏 —— 负责运行时动态生成 DataGrid 列
// 修改:
//   - 数据源从 LogDataModel 改为 LogRecord
//   - 动态列绑定从 Dictionary 索引器改为 PinResults 集合查找
//   - 方案筛选器联动：全部方案=Pin并集，具体方案=方案定义顺序列
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
    /// 列结构：固定列（XAML静态定义）→ 动态列（代码生成）
    /// 固定列：检测时间、机种名称、序列号、方案名称、操作员、综合判定（共6列）
    /// 
    /// 动态列绑定：
    ///   PinResults 是 List&lt;PinResult&gt; 集合，每个 PinResult 有 PinName 和 Result 属性
    ///   使用 IValueConverter 根据列名从集合中查找对应的 Result 值
    /// </remarks>
    [NavigationViewModel(typeof(LogDataViewModel))]
    public partial class LogDataView : UserControl
    {
        private readonly LogDataViewModel _viewModel;

        /// <summary>
        /// 固定列数量：检测时间、机种名称、序列号、方案名称、操作员、综合判定 = 6列
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
        /// 绑定原理：
        ///   由于 LogRecord.PinResults 是 List&lt;PinResult&gt; 集合，
        ///   不能直接用索引器绑定。使用自定义的 PinResultValueConverter 转换器，
        ///   将列名作为 ConverterParameter 传入，在集合中查找对应的 Result 值。
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

            // 为每个动态列名创建一个 DataGridTextColumn
            foreach (var header in _viewModel.DynamicHeaders)
            {
                var column = new DataGridTextColumn
                {
                    Header = header,
                    Width = 100,
                    // 使用 Converter 从 PinResults 集合中查找对应 PinName 的 Result
                    Binding = new Binding("PinResults")
                    {
                        Converter = new PinResultValueConverter(),
                        ConverterParameter = header,
                        Mode = BindingMode.OneWay
                    }
                };
                LogDataGrid.Columns.Add(column);
            }
        }

        /// <summary>
        /// 移除运行时生成的动态列，仅保留XAML中定义的固定列
        /// 固定列 = 检测时间、机种名称、序列号、方案名称、操作员、综合判定（共6列）
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
        /// <summary>
        /// 从 PinResults 集合中查找指定 PinName 的 Result
        /// </summary>
        /// <param name="value">PinResults 集合（List&lt;PinResult&gt;）</param>
        /// <param name="targetType">目标类型（忽略）</param>
        /// <param name="parameter">PinName（列名，如 "A4-A5"）</param>
        /// <param name="culture">区域性信息</param>
        /// <returns>找到的 Result 值，或 "-"</returns>
        public object Convert(object value, Type targetType, object parameter,
            System.Globalization.CultureInfo culture)
        {
            var pinName = parameter as string;
            if (string.IsNullOrWhiteSpace(pinName))
                return "-";

            // value 是 List<PinResult> 集合
            if (value is System.Collections.IList pinResults)
            {
                foreach (var item in pinResults)
                {
                    // 使用反射获取 PinName 和 Result 属性
                    // 这样做是为了避免在 Views 层直接引用 Models 命名空间
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

        /// <summary>
        /// 反向转换（不需要）
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter,
            System.Globalization.CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}