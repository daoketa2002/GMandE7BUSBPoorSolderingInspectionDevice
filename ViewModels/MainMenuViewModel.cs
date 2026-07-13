using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly IServiceProvider _serviceProvider;
        private readonly Serilog.ILogger _logger;

        public MainMenuViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IOperatorStateService operatorStateService,
            IServiceProvider serviceProvider)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _operatorStateService = operatorStateService ?? throw new ArgumentNullException(nameof(operatorStateService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = Log.ForContext<MainMenuViewModel>();
        }

        [RelayCommand]
        private async Task NavigateToRunScreenAsync()
        {
            try
            {
                _logger.Information("[用户操作] 用户点击运行界面按钮");
                var operatorName = _operatorStateService.CurrentOperatorName;
                _logger.Information("[用户操作] 当前作业员: {Operator} (已选择: {HasOperator})",
                    operatorName, _operatorStateService.HasOperator);

                var dialog = _serviceProvider.GetRequiredService<OperatorSelectionDialog>();
                dialog.Owner = Application.Current.MainWindow;
                var confirmed = dialog.ShowDialog() == true;
                if (!confirmed || !_operatorStateService.HasOperator)
                {
                    _logger.Warning("[用户操作] 未确认有效作业员，已取消进入运行界面");
                    return;
                }

                _logger.Warning("[用户操作][审计] 进入运行界面前已选择作业员: {Operator}",
                    _operatorStateService.CurrentOperatorName);
                await _navigationService.NavigateToAsync<TestPageView>("Main", null);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[导航] 导航到运行界面失败");
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
                _logger.Information("[用户操作] 用户点击方案设定按钮");

                var mainWindow = Application.Current.MainWindow;
                if (mainWindow == null) return;

                bool isPasswordVerified = PasswordDialog.ShowPasswordDialog(mainWindow, PasswordDialogContext.PlanSettings);
                if (isPasswordVerified)
                {
                    _logger.Information("[审计] 密码验证通过，导航到方案设定界面");
                    await _navigationService.NavigateToAsync<PlanSettingView>();
                }
                else
                {
                    _logger.Information("[审计] 用户取消或密码验证失败，未进入方案设定");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[导航] 导航到方案设定界面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 系统设定导航命令（修改原有的 NavigateToSystemSettingsAsync）
        /// </summary>
        [RelayCommand]
        private async Task NavigateToSystemSettingsAsync()
        {
            try
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow == null) return;

                bool isPasswordVerified = PasswordDialog.ShowPasswordDialog(mainWindow, PasswordDialogContext.SystemSettings);
                if (isPasswordVerified)
                {
                    _logger.Information("[审计] 密码验证通过，导航到系统设置界面");
                    await _navigationService.NavigateToAsync<SystemSettingsView>("Main", null);
                }
                else
                {
                    _logger.Information("[审计] 用户取消或密码验证失败，未进入系统设置");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[导航] 导航到系统设置界面失败");
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
                    _logger.Information("[用户操作] 用户确认退出系统");
                    Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[用户操作] 退出操作失败");
            }
        }

        public Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.Debug("[导航] 进入主菜单");
            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            _logger.Debug("[导航] 离开主菜单");
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }
    }
}
