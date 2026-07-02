using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views;

/// <summary>
/// 急停锁定弹窗。模态显示，禁止关闭按钮/Esc/Alt+F4。
/// 只能通过程序调用 AllowClose() → DialogResult=true → Close() 关闭。
/// DataContext 由 TestPageViewModel 创建时注入 EmergencyStopDialogViewModel。
/// </summary>
public partial class EmergencyStopDialog : Window
{
    /// <summary>程序主动关闭令牌。true 时允许关闭，false 时拦截所有关闭操作。</summary>
    private bool _allowProgrammaticClose;

    public EmergencyStopDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 标记允许程序主动关闭。调用后 OnClosing 不再拦截。
    /// </summary>
    public void AllowClose()
    {
        _allowProgrammaticClose = true;
    }

    /// <summary>
    /// 阻止所有非程序主动的关闭操作（右上角 X、Alt+F4、系统菜单关闭）。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowProgrammaticClose)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// 拦截键盘事件，阻止 Esc 和 Alt+F4 关闭弹窗。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 阻止 Esc
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            return;
        }

        // 阻止 Alt+F4
        if (e.Key == Key.F4 && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            e.Handled = true;
            return;
        }
    }
}
