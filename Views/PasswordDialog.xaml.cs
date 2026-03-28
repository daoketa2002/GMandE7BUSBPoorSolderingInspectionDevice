
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WPFStandardFramework.ViewModels;

namespace WPFStandardFramework.Views
{
    /// <summary>
    /// PasswordDialog.xaml 的交互逻辑
    /// </summary>
    public partial class PasswordDialog : Window
    {
        private readonly PasswordDialogViewModel _viewModel;

        public PasswordDialog()
        {
            InitializeComponent();
            _viewModel = new PasswordDialogViewModel(this);
            DataContext = _viewModel;

            // 绑定密码框的密码到 ViewModel
            PasswordBox.PasswordChanged += (s, e) =>
            {
                _viewModel.Password = PasswordBox.Password;
            };

            // ✅ 监听 ViewModel 的清空请求，同步清空密码框
            _viewModel.PasswordCleared += OnPasswordCleared;
        }

        private void OnPasswordCleared()
        {
            PasswordBox.Password = string.Empty;
            PasswordBox.Focus(); // 清空后自动获取焦点，方便重新输入
        }

        /// <summary>
        /// 静态方法：显示密码验证弹窗
        /// </summary>
        /// <returns>true=验证通过，false=验证失败或取消</returns>
        public static bool ShowPasswordDialog(Window owner)
        {
            var dialog = new PasswordDialog
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
