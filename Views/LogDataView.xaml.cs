using CommunityToolkit.Mvvm.ComponentModel;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// 日志数据页面的代码隐藏 — 负责运行时动态生成 DataGrid 列。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要动态列：</b></para>
    /// <para>不同CSV文件有不同的检测项列。例如 E78 的 CSV 有 B4-B5、B5-B6、A6-A5，
    /// 而 GM5 的 CSV 有 A4-A5、A5-A6、A1-A2、B8-B9。
    /// 这些列在编译时无法确定，必须在运行时根据数据中的 DynamicHeaders 集合生成。</para>
    /// <para><b>列结构：</b>固定列（XAML静态定义，共6列）→ 动态列（代码生成）→ 日期、时间列（代码生成）</para>
    /// <para><b>触发时机：</b>DynamicHeaders 集合变化时（CollectionChanged）或页面首次加载时（Loaded）。</para>
    /// </remarks>
    [NavigationViewModel(typeof(LogDataViewModel))]
    public partial class LogDataView : UserControl
    {
        private readonly LogDataViewModel _viewModel;

        /// <summary>
        /// 构造器 — 绑定 ViewModel 并订阅动态列变化事件。
        /// </summary>
        /// <param name="viewModel">通过DI注入的 LogDataViewModel</param>
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
            _viewModel.DynamicHeaders.CollectionChanged += OnDynamicHeadersChanged;

            // 页面首次加载完成后生成动态列
            Loaded += (s, e) => GenerateDynamicColumns();
        }

        /// <summary>
        /// DynamicHeaders 集合内容变化时的回调。
        /// </summary>
        private void OnDynamicHeadersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            GenerateDynamicColumns();
        }

        /// <summary>
        /// 根据 ViewModel.DynamicHeaders 集合动态生成 DataGrid 列。
        /// </summary>
        /// <remarks>
        /// <para>列顺序：固定列（序号 ~ 综合判定）→ 动态列 → 日期 → 时间</para>
        /// <para>每次调用先移除之前生成的动态列（RemoveDynamicColumns），再重新添加，
        /// 确保列集合与当前的动态头集合完全一致。</para>
        /// <para>必须在UI线程调用；非UI线程会通过 Dispatcher.Invoke 转发。</para>
        /// </remarks>
        private void GenerateDynamicColumns()
        {
            // 确保在UI线程执行
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(GenerateDynamicColumns);
                return;
            }

            // 清除上一次生成的动态列和日期/时间列（保留XAML中定义的6个固定列）
            RemoveDynamicColumns();

            // 为每个动态列名创建一个 DataGridTextColumn，
            // 绑定路径为 DynamicItems[列名]（通过 LogDataModel 的字典索引器取值）
            foreach (var header in _viewModel.DynamicHeaders)
            {
                var column = new DataGridTextColumn
                {
                    Header = header,
                    Width = 90,
                    // 字典索引器绑定：DynamicItems["B4-B5"] 这样的路径
                    Binding = new Binding($"DynamicItems[{header}]")
                    {
                        Mode = BindingMode.OneWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    }
                };
                LogDataGrid.Columns.Add(column);
            }

            // 在所有动态列之后添加日期列
            LogDataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "日期",
                Width = 100,
                Binding = new Binding("Date")
            });

            // 在所有动态列之后添加时间列
            LogDataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "时间",
                Width = 80,
                Binding = new Binding("Time")
            });
        }

        /// <summary>
        /// 移除运行时生成的动态列和日期/时间列，仅保留XAML中定义的固定列。
        /// 固定列 = 序号、机种名称、序列号、方案名称、检查者、综合判定（共6列）。
        /// </summary>
        private void RemoveDynamicColumns()
        {
            // 固定列数量：序号、机种名称、序列号、方案名称、检查者、综合判定 = 6列
            int fixedColumnCount = 6;

            // 从第7列开始的所有列均由代码生成，逐一移除
            while (LogDataGrid.Columns.Count > fixedColumnCount)
            {
                LogDataGrid.Columns.RemoveAt(fixedColumnCount);
            }
        }
    }
}