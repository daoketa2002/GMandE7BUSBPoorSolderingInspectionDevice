using System.Windows;
using System.Windows.Threading;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services;

/// <summary>
/// 统一管理普通提示、确认框和错误框，保证所有普通弹窗经过同一协调器。
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly IDialogCoordinator _dialogCoordinator;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IDialogCoordinator dialogCoordinator,
        ILogger<NotificationService> logger)
    {
        _dialogCoordinator = dialogCoordinator;
        _logger = logger;
    }

    public void ShowInfo(string message, string title = "信息")
    {
        _ = ObserveNotificationAsync(ShowInfoAsync(message, title), "信息");
    }

    public void ShowWarning(string message, string title = "警告")
    {
        _ = ObserveNotificationAsync(ShowWarningAsync(message, title), "警告");
    }

    public void ShowError(string message, string title = "错误")
    {
        _ = ObserveNotificationAsync(ShowErrorAsync(message, title), "错误");
    }

    public async Task<bool> ConfirmAsync(string message, string title = "确认")
    {
        var dialogLease = await _dialogCoordinator
            .AcquireOrdinaryDialogAsync()
            .ConfigureAwait(false);

        try
        {
            var result = await GetDispatcher()
                .InvokeAsync(() => MessageBox.Show(
                    message,
                    title,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question))
                .Task
                .ConfigureAwait(false);

            return result == MessageBoxResult.Yes;
        }
        finally
        {
            await dialogLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task ShowInfoAsync(string message, string title = "信息")
    {
        return ShowMessageAsync(message, title, MessageBoxImage.Information);
    }

    public Task ShowWarningAsync(string message, string title = "警告")
    {
        return ShowMessageAsync(message, title, MessageBoxImage.Warning);
    }

    public Task ShowErrorAsync(string message, string title = "错误")
    {
        return ShowMessageAsync(message, title, MessageBoxImage.Error);
    }

    private async Task ShowMessageAsync(
        string displayMessage,
        string title,
        MessageBoxImage image)
    {
        var dialogLease = await _dialogCoordinator
            .AcquireOrdinaryDialogAsync()
            .ConfigureAwait(false);

        try
        {
            await GetDispatcher()
                .InvokeAsync(() => MessageBox.Show(
                    displayMessage,
                    title,
                    MessageBoxButton.OK,
                    image))
                .Task
                .ConfigureAwait(false);
        }
        finally
        {
            await dialogLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ObserveNotificationAsync(Task notificationTask, string notificationType)
    {
        try
        {
            await notificationTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[普通弹窗][{NotificationType}] 显示失败", notificationType);
        }
    }

    private static Dispatcher GetDispatcher()
    {
        return Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("WPF 应用尚未初始化，无法显示弹窗");
    }
}
