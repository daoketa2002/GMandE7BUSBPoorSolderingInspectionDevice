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
    /// 日志数据页面 - 支持动态列生成
    /// </summary>
    [NavigationViewModel(typeof(LogDataViewModel))]
    public partial class LogDataView : UserControl
    {
        private readonly LogDataViewModel _viewModel;
        private bool _dynamicColumnsGenerated = false;

        public LogDataView(LogDataViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;

            // 监听动态列集合变化，重新生成列
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LogDataViewModel.DynamicHeaders))
                {
                    GenerateDynamicColumns();
                }
            };

            // 监听DynamicHeaders集合的增删改
            _viewModel.DynamicHeaders.CollectionChanged += OnDynamicHeadersChanged;

            // 页面加载完成后生成动态列
            Loaded += (s, e) => GenerateDynamicColumns();
        }

        private void OnDynamicHeadersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            GenerateDynamicColumns();
        }

        /// <summary>
        /// 根据DynamicHeaders集合动态生成DataGrid列
        /// </summary>
        /// <summary>
        /// 根据DynamicHeaders集合动态生成DataGrid列
        /// </summary>
        private void GenerateDynamicColumns()
        {
            // 必须在UI线程执行
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(GenerateDynamicColumns);
                return;
            }

            // 清除之前生成的动态列（保留固定列）
            RemoveDynamicColumns();

            // 按照正确的顺序添加列
            // 添加固定列
            var fixedColumns = new[]
            {
                ("序号", "Index"),
                ("机种名称", "MachineType"),
                ("序列号", "SerialNumber"),
                ("方案名称", "PlanName"),
                ("检查者", "Inspector"),
                ("综合判定", "Judgment")
             };

            // 添加动态列
            foreach (var header in _viewModel.DynamicHeaders)
            {
                var column = new DataGridTextColumn
                {
                    Header = header,
                    Width = 90,
                    Binding = new Binding($"DynamicItems[{header}]")
                    {
                        Mode = BindingMode.OneWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    }
                };
                // 在固定列之后插入
                LogDataGrid.Columns.Add(column);
            }

            // 添加日期和时间列
            LogDataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "日期",
                Width = 100,
                Binding = new Binding("Date")
            });

            LogDataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "时间",
                Width = 80,
                Binding = new Binding("Time")
            });

            _dynamicColumnsGenerated = true;
        }

        /// <summary>
        /// 移除之前生成的动态列（保留固定列）
        /// </summary>
        private void RemoveDynamicColumns()
        {
            // 固定列数量：序号、机种名称、序列号、方案名称、检查者、综合判定 = 7列
            int fixedColumnCount = 7;

            while (LogDataGrid.Columns.Count > fixedColumnCount)
            {
                LogDataGrid.Columns.RemoveAt(fixedColumnCount);
            }

            _dynamicColumnsGenerated = false;
        }
    }
}