using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 密码弹窗上下文 —— 用于区分从哪个功能入口打开
    /// 不同上下文显示不同的图标、标题、说明文字和强调色
    /// </summary>
    public enum PasswordDialogContext
    {
        /// <summary>系统设置入口</summary>
        SystemSettings,
        /// <summary>方案设定入口</summary>
        PlanSettings
    }

    /// <summary>
    /// 密码输入弹窗的 ViewModel
    /// 支持多上下文：根据 PasswordDialogContext 动态展示不同文案和强调色
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

        // ══════════════════════════════════════════════════════════
        //  上下文相关属性 —— 不同入口显示不同文案和颜色
        // ══════════════════════════════════════════════════════════

        /// <summary>对话框标题（如 "系统设置"、"方案设定"）</summary>
        public string DialogTitle { get; }

        /// <summary>图标文字（如 "⚙"、"📋"）</summary>
        public string IconText { get; }

        /// <summary>说明文字（如 "需要管理员权限才能访问系统设置"）</summary>
        public string DescriptionText { get; }

        /// <summary>强调色（确定按钮背景、密码框焦点边框等）</summary>
        public Brush AccentBrush { get; }

        public PasswordDialogViewModel(Window dialog, PasswordDialogContext context = PasswordDialogContext.SystemSettings)
        {
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _logger = Log.ForContext<PasswordDialogViewModel>();

            // 根据上下文初始化文案和颜色
            (DialogTitle, IconText, DescriptionText, AccentBrush) = context switch
            {
                PasswordDialogContext.PlanSettings => (
                    "方案设定",
                    "📋",
                    "需要管理员权限才能访问方案设定",
                    new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB)) // 蓝色 #3498DB
                ),
                _ => (
                    "系统设置",
                    "⚙",
                    "需要管理员权限才能访问系统设置",
                    new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)) // 橙色 #E67E22
                )
            };

            _dialog.Title = DialogTitle;
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
                    // ★ 操作审计：密码验证成功（Warning 级别确保持久化记录）
                    _logger.Warning("【审计】密码验证成功（操作时间: {Time}）", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    _dialog.DialogResult = true;
                    _dialog.Close();
                }
                else
                {
                    // ★ 操作审计：密码验证失败（记录尝试行为）
                    _logger.Warning("【审计】密码验证失败（操作时间: {Time}，输入值已清空，不可恢复）",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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
