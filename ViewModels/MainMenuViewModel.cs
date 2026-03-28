using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using WPFStandardFramework.Interfaces;
using WPFStandardFramework.Views;

namespace WPFStandardFramework.ViewModels
{

    public partial class MainMenuViewModel : ObservableObject, INavigationAware
    {
        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly Serilog.ILogger _logger;

        public MainMenuViewModel(
            INavigationService navigationService,
            INotificationService notificationService)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _logger = Log.ForContext<MainMenuViewModel>();
        }

        [RelayCommand]
        private async Task NavigateToRunScreenAsync()
        {
            try
            {
                _logger.Information("导航到运行界面");
               // await _navigationService.NavigateToAsync<RunScreenView>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到运行界面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        [RelayCommand]
        private async Task NavigateToDataExtractAsync()
        {
            try
            {
                _logger.Information("导航到数据提取界面");
                //await _navigationService.NavigateToAsync<DataExtractionView>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到数据提取界面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        [RelayCommand]
        private async Task NavigateToSystemSettingsAsync()
        {
            try
            {
                // ⭐ 添加密码验证
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow == null)
                {
                   // await _navigationService.NavigateToAsync<SystemSettingsView>();
                    return;
                }

                bool isPasswordVerified = PasswordDialog.ShowPasswordDialog(mainWindow);

                if (isPasswordVerified)
                {
                    _logger.Information("密码验证通过，导航到系统设置界面");
                   // await _navigationService.NavigateToAsync<SystemSettingsView>();
                }
                else
                {
                    _logger.Information("用户取消或密码验证失败，未进入系统设置");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到系统设置界面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ExitApplicationAsync()
        {
            try
            {
                var result = await _notificationService.ConfirmAsync("确定要退出系统吗？", "确认退出");
                if (result)
                {
                    _logger.Information("用户确认退出系统");
                    Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "退出操作失败");
            }
        }

        public Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.Debug("进入主菜单");
            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            _logger.Debug("离开主菜单");
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }
    }
}
