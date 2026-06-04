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
        /// </summary>
        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;

                if (sender == ModelNameTextBox)
                {
                    SerialNumberTextBox.Focus();
                    SerialNumberTextBox.SelectAll();
                }
                else if (sender == SerialNumberTextBox)
                {
                    OperatorTextBox.Focus();
                    OperatorTextBox.SelectAll();
                }
                else if (sender == OperatorTextBox)
                {
                    // 最后一个输入框，触发开始测试
                    if (_viewModel.StartTestCommand.CanExecute(null))
                    {
                        _viewModel.StartTestCommand.Execute(null);
                    }
                }
            }
        }
    }
}