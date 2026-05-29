using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    public interface INotificationService
    {
        void ShowInfo(string message, string title = "信息");
        void ShowWarning(string message, string title = "警告");
        void ShowError(string message, string title = "错误");
        Task<bool> ConfirmAsync(string message, string title = "确认");

        // 添加异步版本，方便在ViewModel中使用
        Task ShowInfoAsync(string message, string title = "信息");
        Task ShowWarningAsync(string message, string title = "警告");
        Task ShowErrorAsync(string message, string title = "错误");
    }
}
