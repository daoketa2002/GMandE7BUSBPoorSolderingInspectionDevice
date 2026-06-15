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
        private readonly IDeviceSettingsService _settingsService;
        private readonly Serilog.ILogger _logger;

        public MainMenuViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IOperatorStateService operatorStateService,
            IDeviceSettingsService settingsService)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _operatorStateService = operatorStateService ?? throw new ArgumentNullException(nameof(operatorStateService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _logger = Log.ForContext<MainMenuViewModel>();
        }

        /// <summary>
        /// 是否显示PLC通信测试按钮
        /// 从系统设备配置中读取，由系统设定页面的勾选框控制
        /// </summary>
        [ObservableProperty]
        private bool _isPlcCommunicationTestVisible = false;

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
                _logger.Information("导航到日志数据界面");
                
                _logger.Information("用户点击日志数据按钮");
                await _navigationService.NavigateToAsync<LogDataView>("Main", null);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到日志数据界面失败");
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

                bool isPasswordVerified = PasswordDialog.ShowPasswordDialog(mainWindow);
                if (isPasswordVerified)
                {
                    _logger.Information("密码验证通过，导航到系统设置界面");
                    await _navigationService.NavigateToAsync<SystemSettingsView>("Main", null);
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

        /// <summary>
        /// PLC通信测试按钮点击命令
        /// TODO: 实际PLC通信测试逻辑待实现
        /// </summary>
        [RelayCommand]
        private async Task NavigateToPlcCommunicationTestAsync()
        {
            try
            {
                _logger.Information("用户点击PLC通信测试按钮");
                // TODO: 跳转到PLC通信测试页面或直接执行测试
                await _notificationService.ShowInfoAsync("PLC通信测试功能开发中...\n该功能将在后续版本实现。", "提示");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PLC通信测试操作失败");
                await _notificationService.ShowErrorAsync($"操作失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 刷新PLC通信测试按钮的可见性
        /// 每次进入主菜单时从设备配置文件中读取最新状态
        /// </summary>
        private void RefreshPlcTestButtonVisibility()
        {
            try
            {
                var deviceSettings = _settingsService.LoadSettings();
                IsPlcCommunicationTestVisible = deviceSettings.IsPlcCommunicationTestEnabled;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "读取PLC通信测试配置失败，默认隐藏按钮");
                IsPlcCommunicationTestVisible = false;
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
            RefreshPlcTestButtonVisibility();   // 刷新PLC通信测试按钮状态
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