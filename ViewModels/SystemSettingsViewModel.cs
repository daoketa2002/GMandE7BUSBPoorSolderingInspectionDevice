using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Serilog;
using System;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 系统设定页面的ViewModel
    /// 负责三个设备的参数配置展示、保存/恢复默认、PLC通信测试开关
    /// </summary>
    public partial class SystemSettingsViewModel : ObservableObject, INavigationAware
    {
        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IDeviceSettingsService _settingsService;
        private readonly Serilog.ILogger _logger;

        // ==================== 设备配置属性 ====================

        /// <summary>
        /// FP0H PLC配置
        /// </summary>
        [ObservableProperty]
        private FP0HCommunicationConfig _fp0hConfig = new FP0HCommunicationConfig();

        /// <summary>
        /// 扫描仪H1900配置
        /// </summary>
        [ObservableProperty]
        private ScannerSerialCommunicationConfig _scannerConfig = new ScannerSerialCommunicationConfig();

        /// <summary>
        /// GDM-9060万用表配置
        /// </summary>
        [ObservableProperty]
        private GDM9060CommunicationConfig _gdm9060Config = new GDM9060CommunicationConfig();

        /// <summary>
        /// 是否开启PLC通信测试功能
        /// 勾选后主菜单会显示"PLC通信测试"按钮
        /// </summary>
        [ObservableProperty]
        private bool _isPlcCommunicationTestEnabled = false;

        /// <summary>
        /// 当前选中的Tab页索引
        /// 0 = FP0H, 1 = 扫描仪, 2 = GDM-9060
        /// </summary>
        [ObservableProperty]
        private int _selectedTabIndex = 0;


        // ==================== 构造函数 ====================

        public SystemSettingsViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IDeviceSettingsService settingsService)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _logger = Log.ForContext<SystemSettingsViewModel>();

            // 初始化时加载保存的配置
            LoadExistingSettings();
        }

        // ==================== 命令 ====================

        /// <summary>
        /// 恢复所有设备配置为默认值
        /// </summary>
        [RelayCommand]
        private async Task ResetToDefaultAsync()
        {
            try
            {
                bool confirmed = await _notificationService.ConfirmAsync(
                    "确定要恢复所有设备参数为默认配置吗？\n当前修改将丢失。",
                    "恢复默认配置");

                if (!confirmed) return;

                // 恢复为默认值
                Fp0hConfig = new FP0HCommunicationConfig();
                ScannerConfig = new ScannerSerialCommunicationConfig();
                Gdm9060Config = new GDM9060CommunicationConfig();
                IsPlcCommunicationTestEnabled = false;

                _logger.Information("所有设备配置已恢复为默认值");
                await _notificationService.ShowInfoAsync("已恢复默认配置。");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "恢复默认配置失败");
                await _notificationService.ShowErrorAsync($"恢复默认配置失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 保存所有设备配置
        /// 三个设备的配置一起保存到 settings.json
        /// </summary>
        [RelayCommand]
        private async Task SaveAllConfigAsync()
        {
            try
            {
                var deviceSettings = new DeviceSettings
                {
                    // 映射 FP0H 配置
                    FP0HCommunication = Fp0hConfig,

                    // 映射扫描仪配置
                    ScannerSerialCommunication = ScannerConfig,

                    // 映射万用表配置
                    GDM9060Communication = Gdm9060Config,

                    // 保存PLC测试开关状态
                    IsPlcCommunicationTestEnabled = IsPlcCommunicationTestEnabled
                };

                // 直接保存 deviceSettings（它已经包含了所有需要的信息）
                _settingsService.SaveSettings(deviceSettings);

                _logger.Information("所有设备配置已保存成功");
                await _notificationService.ShowInfoAsync("所有配置已保存成功！");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "保存配置失败");
                await _notificationService.ShowErrorAsync($"保存配置失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 返回主菜单
        /// </summary>
        [RelayCommand]
        private async Task NavigateBackToMainMenuAsync()
        {
            try
            {
                _logger.Information("从系统设定返回主菜单");
                await _navigationService.NavigateToAsync<MainMenuView>("Main", null);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "返回主菜单失败");
                await _notificationService.ShowErrorAsync($"返回主菜单失败：{ex.Message}");
            }
        }

        // ==================== 方法 ====================

        /// <summary>
        /// 从现有的 settings.json 加载已保存的设备配置
        /// </summary>
        private void LoadExistingSettings()
        {
            try
            {
                var deviceSettings = _settingsService.LoadSettings();

                // 加载 FP0H 配置
                if (deviceSettings.FP0HCommunication != null)
                    Fp0hConfig = deviceSettings.FP0HCommunication;

                // 加载扫描仪配置
                if (deviceSettings.ScannerSerialCommunication != null)
                    ScannerConfig = deviceSettings.ScannerSerialCommunication;

                // 加载万用表配置
                if (deviceSettings.GDM9060Communication != null)
                    Gdm9060Config = deviceSettings.GDM9060Communication;

                // 加载PLC测试开关状态
                IsPlcCommunicationTestEnabled = deviceSettings.IsPlcCommunicationTestEnabled;

                _logger.Debug("设备配置加载完成");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "加载已有配置失败，使用默认值");
            }
        }

        // ==================== 导航生命周期 ====================

        /// <summary>
        /// 页面导航进入时调用
        /// 每次进入系统设定页面时重新加载最新配置
        /// </summary>
        public Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.Debug("进入系统设定页面");
            // 每次进入页面时，重新加载配置以确保显示最新值
            LoadExistingSettings();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 页面导航离开时调用
        /// </summary>
        public Task OnNavigatedFromAsync()
        {
            _logger.Debug("离开系统设定页面");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 是否允许导航离开
        /// </summary>
        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }
    }
}