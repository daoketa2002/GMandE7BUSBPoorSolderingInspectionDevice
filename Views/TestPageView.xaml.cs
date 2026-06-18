// ============================================================
// 文件: Views/TestPageView.xaml.cs
// 修改: 移除开始测试按钮相关代码
//      终了按钮逻辑已在ViewModel中实现，无需后台代码
//      保留回车键焦点跳转逻辑
// ============================================================

using System;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// 测试页 - 工业检测工位界面
    /// 注意：开始测试按钮已移除，测试由PLC启动信号触发
    /// 终了按钮支持测试中终止（逻辑在ViewModel中处理）
    /// </summary>
    [NavigationViewModel(typeof(TestPageViewModel))]
    public partial class TestPageView : UserControl
    {
        private readonly TestPageViewModel _viewModel;
        private readonly ILogger<TestPageView> _logger;

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

            _logger.LogDebug("TestPageView 初始化完成");
        }

        /// <summary>
        /// 输入框回车键自动跳转焦点
        /// 机种名称 → 序列号 → 作业员
        /// 在作业员输入框按回车不做特殊处理（测试由PLC信号触发）
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
                    // 序列号 → 跳转到作业员
                    OperatorTextBox.Focus();
                    OperatorTextBox.SelectAll();
                }
                // 作业员输入框按回车不自动触发测试（由PLC信号触发）
            }
        }
    }
}