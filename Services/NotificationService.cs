using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;


namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
 public class NotificationService : INotificationService
    {
        public void ShowInfo(string message, string title = "信息")
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void ShowWarning(string message, string title = "警告")
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void ShowError(string message, string title = "错误")
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public async Task<bool> ConfirmAsync(string message, string title = "确认")
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var result = MessageBox.Show(message, title, 
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                return result == MessageBoxResult.Yes;
            }).Task;
        }

        public async Task ShowInfoAsync(string message, string title = "信息")
        {
            await Application.Current.Dispatcher.InvokeAsync(() => 
                ShowInfo(message, title));
        }

        public async Task ShowWarningAsync(string message, string title = "警告")
        {
            await Application.Current.Dispatcher.InvokeAsync(() => 
                ShowWarning(message, title));
        }

        public async Task ShowErrorAsync(string message, string title = "错误")
        {
            await Application.Current.Dispatcher.InvokeAsync(() => 
                ShowError(message, title));
        }
    }
}
