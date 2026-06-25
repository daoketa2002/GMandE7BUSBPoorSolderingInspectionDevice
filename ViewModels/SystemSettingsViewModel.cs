using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.IO;
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
        private readonly IConfiguration _configuration;
        private readonly Serilog.ILogger _logger;

        private readonly CsvStorageSettings _csvStorageSettings;
        private readonly CsvStoragePathManager _csvPathManager;

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

        // ============================================================
        // 新增属性：存储路径设置
        // ============================================================

        /// <summary>
        /// 当前 CSV 存储根路径（显示用）
        /// </summary>
        [ObservableProperty]
        private string _csvStorageRootPath = string.Empty;

        /// <summary>
        /// 用户选择的自定义存储路径
        /// </summary>
        [ObservableProperty]
        private string _customStoragePath = string.Empty;

        /// <summary>
        /// 是否使用默认存储路径（程序目录）
        /// </summary>
        [ObservableProperty]
        private bool _useDefaultStoragePath = true;

        /// <summary>
        /// 当前的完整 TestLog 路径（只读显示）
        /// </summary>
        [ObservableProperty]
        private string _currentTestLogPath = string.Empty;

        /// <summary>
        /// 当"使用默认路径"勾选状态变化时，自动切换自定义路径输入框的可用状态
        /// </summary>
        partial void OnUseDefaultStoragePathChanged(bool value)
        {
            // 勾选默认路径时，清空自定义路径输入
            if (value)
            {
                CustomStoragePath = string.Empty;
            }
            // ⭐ 通知 UI 刷新 CanCustomizePath 绑定
            OnPropertyChanged(nameof(CanCustomizePath));
        }

        /// <summary>
        /// 自定义路径输入框是否可用（取反 UseDefaultStoragePath）
        /// </summary>
        public bool CanCustomizePath => !UseDefaultStoragePath;


        // ==================== 构造函数 ====================

        public SystemSettingsViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IDeviceSettingsService settingsService,
            CsvStorageSettings csvStorageSettings,        
            CsvStoragePathManager csvPathManager,
            IConfiguration configuration)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _csvStorageSettings = csvStorageSettings ?? throw new ArgumentNullException(nameof(csvStorageSettings));  
            _csvPathManager = csvPathManager ?? throw new ArgumentNullException(nameof(csvPathManager));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
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
                // ========== 保存前全面校验各设备参数 ==========
                var errors = ValidateAllConfigs();
                if (errors.Count > 0)
                {
                    string errorMessage = "以下参数设置有误，请修正后再保存：\n\n"
                                        + string.Join("\n", errors);
                    await _notificationService.ShowWarningAsync(errorMessage, "参数校验失败");
                    _logger.Warning("系统设置保存被拒绝，校验错误 {Count} 项", errors.Count);
                    return;
                }

                var deviceSettings = new DeviceSettings
                {
                    FP0HCommunication = Fp0hConfig,
                    ScannerSerialCommunication = ScannerConfig,
                    GDM9060Communication = Gdm9060Config,
                    IsPlcCommunicationTestEnabled = IsPlcCommunicationTestEnabled
                };

                _settingsService.SaveSettings(deviceSettings);

                // 保存 CSV 存储路径（立即生效）
                await SaveCsvStoragePathAsync();

                _logger.Information("所有配置已保存成功");
                await _notificationService.ShowInfoAsync("所有配置已保存成功！存储路径修改立即生效，无需重启。");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "保存配置失败");
                await _notificationService.ShowErrorAsync($"保存配置失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 全面校验三个设备的所有配置参数
        /// 包括：IP格式、端口范围、串口格式、超时值范围、心跳间隔范围等
        /// </summary>
        /// <returns>错误列表（空列表表示全部通过校验）</returns>
        private List<string> ValidateAllConfigs()
        {
            var errors = new List<string>();

            // ─── FP0H PLC 校验 ───
            if (!Common.Validators.InputValidationHelper.IsValidIpAddress(Fp0hConfig.IpAddress))
                errors.Add($"PLC IP地址 \"{Fp0hConfig.IpAddress}\" 格式不正确（应为 xxx.xxx.xxx.xxx）");

            if (!Common.Validators.InputValidationHelper.IsValidPort(Fp0hConfig.Port))
                errors.Add($"PLC 端口号 {Fp0hConfig.Port} 超出范围（应为 {Common.Validators.InputValidationHelper.PortMinValue}~{Common.Validators.InputValidationHelper.PortMaxValue}）");

            if (!Common.Validators.InputValidationHelper.IsValidModbusSlaveId(Fp0hConfig.SlaveId))
                errors.Add($"PLC Modbus从站ID {Fp0hConfig.SlaveId} 超出范围（应为 {Common.Validators.InputValidationHelper.ModbusSlaveIdMin}~{Common.Validators.InputValidationHelper.ModbusSlaveIdMax}）");

            if (!Common.Validators.InputValidationHelper.IsValidTimeoutMs(Fp0hConfig.ReceiveTimeoutMs))
                errors.Add($"PLC 接收超时 {Fp0hConfig.ReceiveTimeoutMs}ms 超出范围（应为 {Common.Validators.InputValidationHelper.TimeoutMsMinValue}~{Common.Validators.InputValidationHelper.TimeoutMsMaxValue}）");

            if (!Common.Validators.InputValidationHelper.IsValidTimeoutMs(Fp0hConfig.SendTimeoutMs))
                errors.Add($"PLC 发送超时 {Fp0hConfig.SendTimeoutMs}ms 超出范围（应为 {Common.Validators.InputValidationHelper.TimeoutMsMinValue}~{Common.Validators.InputValidationHelper.TimeoutMsMaxValue}）");

            if (!Common.Validators.InputValidationHelper.IsValidReconnectDelayMs(Fp0hConfig.ReconnectDelayMs))
                errors.Add($"PLC 重连延迟 {Fp0hConfig.ReconnectDelayMs}ms 超出范围（应为 {Common.Validators.InputValidationHelper.ReconnectDelayMsMin}~{Common.Validators.InputValidationHelper.ReconnectDelayMsMax}）");

            if (!Common.Validators.InputValidationHelper.IsValidReconnectAttempts(Fp0hConfig.MaxReconnectAttempts))
                errors.Add($"PLC 最大重连次数 {Fp0hConfig.MaxReconnectAttempts} 超出范围（应为 {Common.Validators.InputValidationHelper.ReconnectAttemptsMin}~{Common.Validators.InputValidationHelper.ReconnectAttemptsMax}）");

            if (!Common.Validators.InputValidationHelper.IsValidHealthCheckInterval(Fp0hConfig.HealthCheckIntervalSeconds))
                errors.Add($"PLC 心跳间隔 {Fp0hConfig.HealthCheckIntervalSeconds}秒 超出范围（应为 {Common.Validators.InputValidationHelper.HealthCheckIntervalMinSec}~{Common.Validators.InputValidationHelper.HealthCheckIntervalMaxSec}）");

            // ─── 扫描枪 H1900 校验 ───
            if (!Common.Validators.InputValidationHelper.IsValidComPort(ScannerConfig.SerialNumber))
                errors.Add($"扫描枪串口号 \"{ScannerConfig.SerialNumber}\" 格式不正确（应为 COM1~COM256）");

            if (!Common.Validators.InputValidationHelper.IsValidHealthCheckInterval(ScannerConfig.HealthCheckIntervalSeconds))
                errors.Add($"扫描枪心跳间隔 {ScannerConfig.HealthCheckIntervalSeconds}秒 超出范围（应为 {Common.Validators.InputValidationHelper.HealthCheckIntervalMinSec}~{Common.Validators.InputValidationHelper.HealthCheckIntervalMaxSec}）");

            if (!Common.Validators.InputValidationHelper.IsValidDataTimeout(ScannerConfig.LastDataTimeoutSeconds))
                errors.Add($"扫描枪数据超时 {ScannerConfig.LastDataTimeoutSeconds}秒 超出范围（应为 {Common.Validators.InputValidationHelper.DataTimeoutMinSec}~{Common.Validators.InputValidationHelper.DataTimeoutMaxSec}）");

            // ─── GDM-9060 万用表校验 ───
            if (!Common.Validators.InputValidationHelper.IsValidIpAddress(Gdm9060Config.IpAddress))
                errors.Add($"万用表 IP地址 \"{Gdm9060Config.IpAddress}\" 格式不正确（应为 xxx.xxx.xxx.xxx）");

            if (!Common.Validators.InputValidationHelper.IsValidPort(Gdm9060Config.Port))
                errors.Add($"万用表 端口号 {Gdm9060Config.Port} 超出范围（应为 {Common.Validators.InputValidationHelper.PortMinValue}~{Common.Validators.InputValidationHelper.PortMaxValue}）");

            if (!Common.Validators.InputValidationHelper.IsValidTimeoutMs(Gdm9060Config.ReceiveTimeoutMs))
                errors.Add($"万用表 接收超时 {Gdm9060Config.ReceiveTimeoutMs}ms 超出范围（应为 {Common.Validators.InputValidationHelper.TimeoutMsMinValue}~{Common.Validators.InputValidationHelper.TimeoutMsMaxValue}）");

            if (!Common.Validators.InputValidationHelper.IsValidTimeoutMs(Gdm9060Config.SendTimeoutMs))
                errors.Add($"万用表 发送超时 {Gdm9060Config.SendTimeoutMs}ms 超出范围（应为 {Common.Validators.InputValidationHelper.TimeoutMsMinValue}~{Common.Validators.InputValidationHelper.TimeoutMsMaxValue}）");

            if (!Common.Validators.InputValidationHelper.IsValidHealthCheckInterval(Gdm9060Config.HealthCheckIntervalSeconds))
                errors.Add($"万用表心跳间隔 {Gdm9060Config.HealthCheckIntervalSeconds}秒 超出范围（应为 {Common.Validators.InputValidationHelper.HealthCheckIntervalMinSec}~{Common.Validators.InputValidationHelper.HealthCheckIntervalMaxSec}）");

            if (!Common.Validators.InputValidationHelper.IsValidDataTimeout(Gdm9060Config.LastDataTimeoutSeconds))
                errors.Add($"万用表数据超时 {Gdm9060Config.LastDataTimeoutSeconds}秒 超出范围（应为 {Common.Validators.InputValidationHelper.DataTimeoutMinSec}~{Common.Validators.InputValidationHelper.DataTimeoutMaxSec}）");

            return errors;
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

        /// <summary>
        /// 浏览选择存储文件夹
        /// 使用 Microsoft.Win32.OpenFolderDialog（.NET 5+ 原生支持）
        /// </summary>
        [RelayCommand]
        private void BrowseStoragePath()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "请选择 CSV 日志文件的存储根目录",
                    Multiselect = false
                };

                // 如果有已输入路径且存在，设为初始目录
                if (!string.IsNullOrWhiteSpace(CustomStoragePath) && Directory.Exists(CustomStoragePath))
                {
                    dialog.FolderName = CustomStoragePath;
                }
                else
                {
                    dialog.FolderName = AppDomain.CurrentDomain.BaseDirectory;
                }

                // ShowDialog 返回 bool?，true 表示用户点击了"确定"
                bool? result = dialog.ShowDialog();
                if (result == true && !string.IsNullOrEmpty(dialog.FolderName))
                {
                    CustomStoragePath = dialog.FolderName;
                    UpdateTestLogPreview();
                    _logger.Information("用户选择存储路径: {Path}", CustomStoragePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "浏览文件夹失败");
                _ = _notificationService.ShowErrorAsync($"浏览文件夹失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 打开当前 TestLog 文件夹
        /// </summary>
        [RelayCommand]
        private void OpenTestLogFolder()
        {
            try
            {
                var testLogPath = _csvPathManager.GetTestLogRootPath();
                if (Directory.Exists(testLogPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", testLogPath);
                }
                else
                {
                    _notificationService.ShowWarningAsync("TestLog 文件夹尚不存在，保存第一条记录后会自动创建。", "提示");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "打开 TestLog 文件夹失败");
                _notificationService.ShowErrorAsync($"打开文件夹失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 更新 TestLog 路径预览
        /// </summary>
        private void UpdateTestLogPreview()
        {
            if (UseDefaultStoragePath || string.IsNullOrWhiteSpace(CustomStoragePath))
            {
                CurrentTestLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "数据", "TestLog");
            }
            else
            {
                CurrentTestLogPath = Path.Combine(CustomStoragePath, "数据", "TestLog");
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

                // 加载 CSV 存储路径
                LoadCsvStoragePath();

                _logger.Debug("设备配置加载完成");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "加载已有配置失败，使用默认值");
            }
        }

        /// <summary>
        /// 加载 CSV 存储路径配置
        /// </summary>
        private void LoadCsvStoragePath()
        {
            try
            {
                var rootPath = _csvStorageSettings.RootPath;
                if (string.IsNullOrEmpty(rootPath) || rootPath.Equals("Default", StringComparison.OrdinalIgnoreCase))
                {
                    UseDefaultStoragePath = true;
                    CustomStoragePath = string.Empty;
                }
                else
                {
                    UseDefaultStoragePath = false;
                    CustomStoragePath = rootPath;
                }

                CsvStorageRootPath = rootPath;
                UpdateTestLogPreview();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "加载CSV存储路径失败，使用默认值");
                UseDefaultStoragePath = true;
                CustomStoragePath = string.Empty;
                UpdateTestLogPreview();
            }
        }

        /// <summary>
        /// 保存 CSV 存储路径到 appsettings.json（立即生效，无需重启）
        /// </summary>
        private async Task SaveCsvStoragePathAsync()
        {
            try
            {
                var configManager = new ConfigManagerService(_configuration);

                var newPath = UseDefaultStoragePath ? "Default" : CustomStoragePath;
                var updates = new Dictionary<string, object>
                {
                    ["CsvStorage:RootPath"] = newPath
                };

                await configManager.SaveConfigurationAsync(updates);
                _logger.Information("CSV存储路径已保存并立即生效: {Path}", newPath);

                // 刷新路径预览
                UpdateTestLogPreview();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "保存CSV存储路径失败");
                await _notificationService.ShowErrorAsync($"保存存储路径失败：{ex.Message}");
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