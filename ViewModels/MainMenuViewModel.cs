using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Serilog;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    public partial class MainMenuViewModel : ObservableObject, INavigationAware
    {
        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IOperatorStateService _operatorStateService;
        private readonly Serilog.ILogger _logger;

        public MainMenuViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IOperatorStateService operatorStateService)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _operatorStateService = operatorStateService ?? throw new ArgumentNullException(nameof(operatorStateService));
            _logger = Log.ForContext<MainMenuViewModel>();
        }

        [RelayCommand]
        private async Task NavigateToRunScreenAsync()
        {
            try
            {
                _logger.Information("用户点击运行界面按钮");
                var operatorName = _operatorStateService.CurrentOperatorName;
                _logger.Information("当前作业员: {Operator} (已选择: {HasOperator})",
                    operatorName, _operatorStateService.HasOperator);
                if (!_operatorStateService.HasOperator)
                {
                    _logger.Information("未选择作业员，使用默认作业员: {Default}", operatorName);
                }
                await _navigationService.NavigateToAsync<TestPageView>("Main", null);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到运行界面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        [RelayCommand]
        private async Task NavigateToOperatorSettingsAsync()
        {
            try
            {
                _logger.Information("用户点击作业员设定按钮");
                await _navigationService.NavigateToAsync<OperatorSettingsView>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到作业员设定界面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 🆕 方案设定 - 需要密码保护
        /// </summary>
        [RelayCommand]
        private async Task NavigateToPlanSettingAsync()
        {
            try
            {
                _logger.Information("用户点击方案设定按钮");

                var mainWindow = Application.Current.MainWindow;
                if (mainWindow == null) return;

                bool isPasswordVerified = PasswordDialog.ShowPasswordDialog(mainWindow);
                if (isPasswordVerified)
                {
                    _logger.Information("密码验证通过，导航到方案设定界面");
                    await _navigationService.NavigateToAsync<PlanSettingView>();
                }
                else
                {
                    _logger.Information("用户取消或密码验证失败，未进入方案设定");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到方案设定界面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        [RelayCommand]
        private async Task NavigateToDataExtractAsync()
        {
            try
            {
                _logger.Information("导航到数据提取界面");
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
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow == null) return;

                bool isPasswordVerified = PasswordDialog.ShowPasswordDialog(mainWindow);
                if (isPasswordVerified)
                {
                    _logger.Information("密码验证通过，导航到系统设置界面");
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