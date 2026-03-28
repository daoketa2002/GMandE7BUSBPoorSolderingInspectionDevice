using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;

namespace WPFStandardFramework.ViewModels
{
    /// <summary>
    /// 密码输入弹窗的 ViewModel
    /// </summary>
    public partial class PasswordDialogViewModel : ObservableObject
    {
        private readonly ILogger _logger;
        private readonly Window _dialog;

        [ObservableProperty]
        private string _password = string.Empty;

        [ObservableProperty]
        private string _errorMessage = string.Empty;

        [ObservableProperty]
        private bool _hasError;

        //  清空密码框的事件
        public event Action? PasswordCleared;

        // 允许的密码列表（不区分大小写）
        private static readonly HashSet<string> _validPasswords = new(StringComparer.OrdinalIgnoreCase)
        {
            "33445566",
            "ROOT"
        };

        public PasswordDialogViewModel(Window dialog)
        {
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _logger = Log.ForContext<PasswordDialogViewModel>();
        }

        /// <summary>
        /// 确认按钮命令
        /// </summary>
        [RelayCommand]
        private void Confirm()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Password))
                {
                    ShowError("请输入密码");
                    return;
                }

                // 验证密码（不区分大小写）
                if (_validPasswords.Contains(Password.Trim()))
                {
                    _logger.Information("密码验证成功");
                    _dialog.DialogResult = true;
                    _dialog.Close();
                }
                else
                {
                    _logger.Warning("密码验证失败: 输入了错误密码");
                    ShowError("密码错误，请重试");

                    // 清空 ViewModel 中的密码
                    Password = string.Empty;

                    // 触发事件通知 View 清空密码框
                    PasswordCleared?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "密码验证异常");
                ShowError("验证失败，请重试");

                Password = string.Empty;
                PasswordCleared?.Invoke();
            }
        }

        /// <summary>
        /// 取消按钮命令
        /// </summary>
        [RelayCommand]
        private void Cancel()
        {
            try
            {
                _logger.Debug("用户取消密码输入");
                _dialog.DialogResult = false;
                _dialog.Close();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "取消操作异常");
            }
        }

        private void ShowError(string message)
        {
            ErrorMessage = message;
            HasError = true;
        }

        // 当用户开始输入时清除错误提示
        partial void OnPasswordChanged(string value)
        {
            if (HasError && !string.IsNullOrWhiteSpace(value))
            {
                HasError = false;
                ErrorMessage = string.Empty;
            }
        }
    }
}
