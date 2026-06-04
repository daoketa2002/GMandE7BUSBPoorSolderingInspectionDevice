using System.Windows;
using System.Windows.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// 作业员选择窗口
    /// </summary>
    public partial class OperatorSelectionDialog : Window
    {
        private readonly OperatorSelectionViewModel _viewModel;

        public OperatorSelectionDialog()
        {
            InitializeComponent();
            _viewModel = new OperatorSelectionViewModel(this);
            DataContext = _viewModel;

            // 窗口加载后聚焦到输入框
            Loaded += (s, e) =>
            {
                InputNameTextBox.Focus();
            };

            // 支持 Enter 键快速确认
            InputNameTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    _viewModel.ConfirmCommand.Execute(null);
                }
            };

            // 支持 Escape 键退出
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    _viewModel.CancelCommand.Execute(null);
                }
            };

            // 左侧列表双击快速确认
            OperatorListBox.MouseDoubleClick += (s, e) =>
            {
                if (_viewModel.SelectedOperator != null)
                {
                    _viewModel.ConfirmCommand.Execute(null);
                }
            };
        }

        /// <summary>
        /// 静态方法：显示作业员选择窗口并返回结果
        /// </summary>
        /// <param name="owner">父窗口</param>
        /// <returns>选中的作业员，取消时返回 null</returns>
        public static OperatorModel? ShowDialog(Window owner)
        {
            var dialog = new OperatorSelectionDialog
            {
                Owner = owner
            };

            var result = dialog.ShowDialog();
            return result == true ? dialog._viewModel.ResultOperator : null;
        }
    }
}