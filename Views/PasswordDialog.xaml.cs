using System;
using System.Windows;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// PasswordDialog.xaml 的交互逻辑
    /// 支持多上下文：根据 PasswordDialogContext 展示不同文案和强调色
    /// </summary>
    public partial class PasswordDialog : Window
    {
        private readonly PasswordDialogViewModel _viewModel;

        private PasswordDialog(PasswordDialogContext context)
        {
            InitializeComponent();
            _viewModel = new PasswordDialogViewModel(this, context);
            DataContext = _viewModel;

            // 绑定密码框的密码到 ViewModel
            PasswordBox.PasswordChanged += (s, e) =>
            {
                _viewModel.Password = PasswordBox.Password;
            };

            // 监听 ViewModel 的清空请求，同步清空密码框
            _viewModel.PasswordCleared += OnPasswordCleared;
        }

        private void OnPasswordCleared()
        {
            PasswordBox.Password = string.Empty;
            PasswordBox.Focus(); // 清空后自动获取焦点，方便重新输入
        }

        /// <summary>
        /// 显示密码验证弹窗
        /// </summary>
        /// <param name="owner">父窗口</param>
        /// <param name="context">弹窗上下文（决定文案和颜色）</param>
        /// <returns>true=验证通过，false=验证失败或取消</returns>
        public static bool ShowPasswordDialog(Window owner, PasswordDialogContext context = PasswordDialogContext.SystemSettings)
        {
            var dialog = new PasswordDialog(context)
            {
                Owner = owner
            };
            return dialog.ShowDialog() == true;
        }

        protected override void OnClosed(EventArgs e)
        {
            // 清理事件订阅，防止内存泄漏
            if (_viewModel != null)
            {
                _viewModel.PasswordCleared -= OnPasswordCleared;
            }
            base.OnClosed(e);
        }
    }
}
