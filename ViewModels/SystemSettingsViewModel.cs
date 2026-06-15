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
        private readonly ISettingsService _settingsService;
        private readonly Serilog.ILogger _logger;

        // ==================== 设备配置属性 ====================

        /// <summary>
        /// FP0H PLC配置
        /// </summary>
        [ObservableProperty]
        private FP0HConfig _fp0hConfig = new FP0HConfig();

        /// <summary>
        /// 扫描仪H1900配置
        /// </summary>
        [ObservableProperty]
        private ScannerConfig _scannerConfig = new ScannerConfig();

        /// <summary>
        /// GDM-9060万用表配置
        /// </summary>
        [ObservableProperty]
        private GDM9060Config _gdm9060Config = new GDM9060Config();

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

        // ==================== 默认值（后续从appsettings.json读取后删除此区域） ====================
        // TODO: 以下默认值仅作占位使用，后续改为从 appsettings.json 中读取
        // 实际读取方式参考下方 GetDefaultSettings() 方法中的注释

        /// <summary>
        /// 获取FP0H默认配置（占位版本 - 后续可删除）
        /// </summary>
        private FP0HConfig GetDefaultFP0HConfig_Placeholder()
        {
            return new FP0HConfig
            {
                IpAddress = "192.168.1.3",
                Port = 502,
                SlaveId = 1,
                ReceiveTimeoutMs = 5000,
                SendTimeoutMs = 5000,
                ReconnectDelayMs = 2000,
                MaxReconnectAttempts = 12,
                HealthCheckIntervalSeconds = 5
            };
        }

        /// <summary>
        /// 获取扫描仪默认配置（占位版本 - 后续可删除）
        /// </summary>
        private ScannerConfig GetDefaultScannerConfig_Placeholder()
        {
            return new ScannerConfig
            {
                SerialNumber = "COM9",
                BaudRate = 115200,
                Parity = "None",
                DataBits = 8,
                StopBits = "1",
                FlowControl = "None",
                HealthCheckIntervalSeconds = 5,
                LastDataTimeoutSeconds = 30
            };
        }

        /// <summary>
        /// 获取GDM-9060默认配置（占位版本 - 后续可删除）
        /// </summary>
        private GDM9060Config GetDefaultGDM9060Config_Placeholder()
        {
            return new GDM9060Config
            {
                IpAddress = "192.168.1.4",
                Port = 5025,
                ReceiveTimeoutMs = 5000,
                SendTimeoutMs = 5000,
                HealthCheckIntervalSeconds = 5,
                LastDataTimeoutSeconds = 30
            };
        }

        // ==================== 第二套实现：从现有配置类获取默认值（占位版本可删除时启用此方法） ====================
        // TODO: 当上面的占位默认值不再需要时，删除上面的三个Placeholder方法，
        //       启用下面三个方法（取消注释），它们从现有的 设置相关类\ApplicationSettings.cs 中获取默认值。

        /*
        /// <summary>
        /// 从现有配置获取FP0H默认值（第二套实现）
        /// 读取 设置相关类\ApplicationSettings 中的 TcpClientPLCMotionControlSettings
        /// </summary>
        private FP0HConfig GetDefaultFP0HConfig_FromExisting()
        {
            var existingSettings = _settingsService.LoadSettings();
            var tcpClient = existingSettings.TcpClientPLCMotion;
            return new FP0HConfig
            {
                IpAddress = tcpClient.Host ?? "192.168.1.3",
                Port = tcpClient.Port,
                SlaveId = 1, // 默认Modbus从站ID
                ReceiveTimeoutMs = tcpClient.ReceiveTimeoutMs ?? 5000,
                SendTimeoutMs = tcpClient.SendTimeoutMs ?? 5000,
                ReconnectDelayMs = tcpClient.ReconnectDelayMs ?? 2000,
                MaxReconnectAttempts = tcpClient.MaxReconnectAttempts ?? 12,
                HealthCheckIntervalSeconds = tcpClient.HealthCheckIntervalSeconds
            };
        }

        /// <summary>
        /// 从现有配置获取扫描仪默认值（第二套实现）
        /// 读取 设置相关类\ApplicationSettings 中的 ScannerSerialCommunicationSettings
        /// </summary>
        private ScannerConfig GetDefaultScannerConfig_FromExisting()
        {
            var existingSettings = _settingsService.LoadSettings();
            var scanner = existingSettings.ScannerSerialCommunication;
            return new ScannerConfig
            {
                SerialNumber = scanner.SerialNumber ?? "COM9",
                BaudRate = scanner.BaudRate,
                Parity = scanner.Parity ?? "None",
                DataBits = scanner.DataBits,
                StopBits = scanner.StopBits ?? "1",
                FlowControl = scanner.FlowControl ?? "None",
                HealthCheckIntervalSeconds = scanner.HealthCheckIntervalSeconds,
                LastDataTimeoutSeconds = scanner.LastDataTimeoutSeconds ?? 30
            };
        }

        /// <summary>
        /// 从现有配置获取GDM-9060默认值（第二套实现）
        /// 读取 设置相关类\ApplicationSettings 中的 TcpClientGWInstekSettings
        /// </summary>
        private GDM9060Config GetDefaultGDM9060Config_FromExisting()
        {
            var existingSettings = _settingsService.LoadSettings();
            var gwInstek = existingSettings.TcpClientGWInstek;
            return new GDM9060Config
            {
                IpAddress = gwInstek.Host ?? "192.168.1.4",
                Port = gwInstek.Port,
                ReceiveTimeoutMs = 5000,    // 现有配置中未定义，使用默认值
                SendTimeoutMs = 5000,       // 现有配置中未定义，使用默认值
                HealthCheckIntervalSeconds = gwInstek.HealthCheckIntervalSeconds,
                LastDataTimeoutSeconds = gwInstek.LastDataTimeoutSeconds ?? 30
            };
        }
        */

        // ==================== 构造函数 ====================

        public SystemSettingsViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            ISettingsService settingsService)
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

                // TODO: 切换实现时，将下面三行的 Placeholder 方法替换为 FromExisting 方法
                // 当前使用占位默认值版本
                Fp0hConfig = GetDefaultFP0HConfig_Placeholder();
                ScannerConfig = GetDefaultScannerConfig_Placeholder();
                Gdm9060Config = GetDefaultGDM9060Config_Placeholder();
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
                // 构建系统设备配置对象
                var deviceSettings = new SystemDeviceSettings
                {
                    FP0HConfig = Fp0hConfig,
                    ScannerConfig = ScannerConfig,
                    GDM9060Config = Gdm9060Config,
                    IsPlcCommunicationTestEnabled = IsPlcCommunicationTestEnabled
                };

                // TODO: 实际保存逻辑 - 将配置写入 settings.json
                // 获取现有全部配置，更新其中的设备部分
                var appSettings = _settingsService.LoadSettings();

                // TODO: 将 deviceSettings 的值映射到 appSettings 中对应的属性
                // 例如：
                // appSettings.TcpClientPLCMotion.Host = deviceSettings.FP0HConfig.IpAddress;
                // appSettings.TcpClientPLCMotion.Port = deviceSettings.FP0HConfig.Port;
                // ... 其他映射
                // appSettings.ScannerSerialCommunication.SerialNumber = deviceSettings.ScannerConfig.SerialNumber;
                // ... 其他映射
                // appSettings.TcpClientGWInstek.Host = deviceSettings.GDM9060Config.IpAddress;
                // ... 其他映射

                _settingsService.SaveSettings(appSettings);

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
                var appSettings = _settingsService.LoadSettings();

                // TODO: 从 appsettings.json 加载已保存的配置值
                // 当前使用默认值占位，后续补充实际读取逻辑

                // 示例：从现有配置加载PLC设置
                // Fp0hConfig.IpAddress = appSettings.TcpClientPLCMotion.Host ?? "192.168.1.3";
                // Fp0hConfig.Port = appSettings.TcpClientPLCMotion.Port;
                // ... 其他属性的加载

                _logger.Debug("设备配置加载完成（当前使用默认值占位）");
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