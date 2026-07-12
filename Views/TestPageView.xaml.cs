// ============================================================
// 文件: Views/TestPageView.xaml.cs
// 描述: 运行界面代码后置
// 改动说明（方案需求变动）:
//   新增自定义合并表头列宽同步逻辑
//   "检查条件" 拆分为 "检查方式"+"下限"+"上限" 三个子列，
//   通过独立 Grid 绘制双行合并表头，DataGrid 隐藏原生表头
// ============================================================

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// 运行界面 - 工业检测工位界面
    /// 注意：开始测试按钮已移除，测试由PLC启动信号触发
    /// 终了按钮支持测试中终止（逻辑在ViewModel中处理）
    ///
    /// 表头列索引映射（与 XAML 中 DataGrid 列顺序一致）：
    ///   0-序号, 1-项目名称, 2-检查方式, 3-下限(Ω), 4-上限(Ω), 5-检查结果, 6-判定
    /// </summary>
    [NavigationViewModel(typeof(TestPageViewModel))]
    public partial class TestPageView : UserControl
    {
        private readonly TestPageViewModel _viewModel;
        private readonly ILogger<TestPageView> _logger;
        private bool _headerSynced = false;

        public TestPageView(TestPageViewModel viewModel, ILogger<TestPageView> logger)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _logger = logger;

            DataContext = _viewModel;

            // 订阅日志集合变化，自动滚动到底部
            _viewModel.LogMessages.CollectionChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (LogListBox.Items.Count > 0)
                    {
                        var lastItem = LogListBox.Items[LogListBox.Items.Count - 1];
                        LogListBox.ScrollIntoView(lastItem);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            };

            // LayoutUpdated 时同步表头列宽（首次渲染后触发）
            this.LayoutUpdated += OnLayoutUpdated;

            _logger.LogDebug("TestPageView 初始化完成");
        }

        /// <summary>
        /// 首次布局完成后同步自定义表头 Grid 的列宽与 DataGrid 列的实际渲染宽度
        /// 使用 LayoutUpdated 一次性完成同步，后续不再重复执行
        /// </summary>
        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (_headerSynced) return;

            try
            {
                // 获取 DataGrid 各列的实际渲染宽度
                var dgColumns = TestDataGrid.Columns;
                if (dgColumns.Count < 7) return;

                var widths = new double[7];
                bool allReady = true;
                for (int i = 0; i < 7; i++)
                {
                    widths[i] = dgColumns[i].ActualWidth;
                    if (widths[i] <= 0) { allReady = false; break; }
                }

                if (!allReady) return;

                // 同步到自定义表头 Grid 的列宽
                ApplyHeaderColumnWidths(widths);
                _headerSynced = true;

                _logger.LogDebug("自定义合并表头列宽已同步: [{Widths}]",
                    string.Join(", ", widths.Select(w => $"{w:F0}")));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "同步表头列宽异常");
            }
        }

        /// <summary>
        /// 将测量到的 DataGrid 列宽应用到自定义表头 Grid
        /// </summary>
        private void ApplyHeaderColumnWidths(double[] widths)
        {
            if (widths.Length < 7) return;

            // 自定义表头第1行
            HeaderCol0.Width = new GridLength(widths[0]);
            HeaderCol1.Width = new GridLength(widths[1]);
            HeaderCol2.Width = new GridLength(widths[2] + widths[3] + widths[4]); // 检查条件合并
            HeaderCol5.Width = new GridLength(widths[5]);
            HeaderCol6.Width = new GridLength(widths[6]);

            // 自定义表头第2行的子列
            HeaderSubCol2.Width = new GridLength(widths[2]);  // 检查方式
            HeaderSubCol3.Width = new GridLength(widths[3]);  // 下限(Ω)
            HeaderSubCol4.Width = new GridLength(widths[4]);  // 上限(Ω)
        }

        /// <summary>
        /// 输入框回车键自动跳转焦点
        /// 机种名称 → 序列号。作业员由选择弹窗维护。
        /// </summary>
        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;

                if (sender == ModelNameTextBox)
                {
                    // 机种名称 → 跳转到序列号
                    SerialNumberTextBox.Focus();
                    SerialNumberTextBox.SelectAll();
                }
                else if (sender == SerialNumberTextBox)
                {
                    SerialNumberTextBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
            }
        }
    }
}
