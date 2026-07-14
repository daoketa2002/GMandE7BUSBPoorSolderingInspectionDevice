using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 系统设定页面的ViewModel
    /// 负责三个设备的参数配置展示、保存以及设备测试连接功能
    /// </summary>
    public partial class SystemSettingsViewModel : ObservableObject, INavigationAware
    {
        #region 依赖注入字段

        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IDeviceSettingsService _settingsService;
        private readonly IConfiguration _configuration;
        private readonly Serilog.ILogger _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IDeviceConnectionManager _deviceManager;

        private readonly CsvStorageSettings _csvStorageSettings;
        private readonly CsvStoragePathManager _csvPathManager;

        #endregion

        #region 设备配置属性

        /// <summary>FP0H PLC配置</summary>
        [ObservableProperty]
        private FP0HCommunicationConfig _fp0hConfig = new FP0HCommunicationConfig();

        /// <summary>扫描仪H1900配置</summary>
        [ObservableProperty]
        private ScannerSerialCommunicationConfig _scannerConfig = new ScannerSerialCommunicationConfig();

        /// <summary>GDM-9060万用表配置</summary>
        [ObservableProperty]
        private GDM9060CommunicationConfig _gdm9060Config = new GDM9060CommunicationConfig();

        /// <summary>
        /// 单项 NG 后是否继续测试后续项目。
        /// true：记录该项 NG，继续测完整个方案；
        /// false：首个 NG 后停止本轮，等待操作员复位或终了。
        /// </summary>
        [ObservableProperty]
        private bool _continueTestingAfterNg = true;

        /// <summary>
        /// 单项 NG 后继续测试时，最终 NG 记录是否继续保存。
        /// 仅在 ContinueTestingAfterNg 开启时生效。
        /// </summary>
        [ObservableProperty]
        private bool _saveNgInspectionResult = true;

        /// <summary>当前选中的Tab页索引（0=常规设置, 1=PLC, 2=扫描仪, 3=万用表）</summary>
        [ObservableProperty]
        private int _selectedTabIndex = 0;

        /// <summary>保存配置期间显示非模态等待遮罩并阻止重复操作。</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveAllConfigCommand))]
        [NotifyCanExecuteChangedFor(nameof(NavigateBackToMainMenuCommand))]
        [NotifyCanExecuteChangedFor(nameof(BrowseStoragePathCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenTestLogFolderCommand))]
        [NotifyCanExecuteChangedFor(nameof(TestPlcConnectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(TestScannerConnectionCommand))]
        [NotifyCanExecuteChangedFor(nameof(TestDmmConnectionCommand))]
        private bool _isSavingConfig;

        [ObservableProperty]
        private string _saveStatusText = string.Empty;

        #endregion

        #region CSV存储路径属性

        [ObservableProperty]
        private string _csvStorageRootPath = string.Empty;

        [ObservableProperty]
        private string _customStoragePath = string.Empty;

        [ObservableProperty]
        private bool _useDefaultStoragePath = true;

        [ObservableProperty]
        private string _currentTestLogPath = string.Empty;

        public bool CanCustomizePath => !UseDefaultStoragePath;

        partial void OnUseDefaultStoragePathChanged(bool value)
        {
            if (value)
            {
                CustomStoragePath = string.Empty;
            }
            OnPropertyChanged(nameof(CanCustomizePath));
        }

        #endregion

        #region PLC 测试连接状态

        /// <summary>PLC测试是否正在进行中</summary>
        [ObservableProperty]
        private bool _isTestingPlc;

        /// <summary>PLC测试结果显示文本</summary>
        [ObservableProperty]
        private string _plcTestMessage = string.Empty;

        /// <summary>PLC测试结果状态指示（"Green"/"Red"/"Gray"）</summary>
        [ObservableProperty]
        private string _plcTestStatus = "Gray";

        /// <summary>PLC测试按钮显示文本</summary>
        [ObservableProperty]
        private string _plcTestButtonText = "🔍 测试连接";

        /// <summary>PLC测试结果自动清除的CTS</summary>
        private CancellationTokenSource? _plcTestClearCts;

        #endregion

        #region 扫描枪 测试连接状态

        [ObservableProperty]
        private bool _isTestingScanner;

        [ObservableProperty]
        private string _scannerTestMessage = string.Empty;

        [ObservableProperty]
        private string _scannerTestStatus = "Gray";

        [ObservableProperty]
        private string _scannerTestButtonText = "🔍 测试连接";

        private CancellationTokenSource? _scannerTestClearCts;

        #endregion

        #region 万用表 测试连接状态

        [ObservableProperty]
        private bool _isTestingDmm;

        [ObservableProperty]
        private string _dmmTestMessage = string.Empty;

        [ObservableProperty]
        private string _dmmTestStatus = "Gray";

        [ObservableProperty]
        private string _dmmTestButtonText = "🔍 测试连接";

        private CancellationTokenSource? _dmmTestClearCts;

        #endregion

        #region 测试连接超时常量

        /// <summary>测试连接总超时（毫秒）</summary>
        private const int TEST_CONNECTION_TIMEOUT_MS = 8000;

        /// <summary>测试结果自动清除延迟（毫秒）</summary>
        private const int TEST_RESULT_CLEAR_DELAY_MS = 5000;

        #endregion

        #region 构造函数

        public SystemSettingsViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IDeviceSettingsService settingsService,
            CsvStorageSettings csvStorageSettings,
            CsvStoragePathManager csvPathManager,
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory,
            IDeviceConnectionManager deviceManager)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _csvStorageSettings = csvStorageSettings ?? throw new ArgumentNullException(nameof(csvStorageSettings));
            _csvPathManager = csvPathManager ?? throw new ArgumentNullException(nameof(csvPathManager));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));

            _logger = Log.ForContext<SystemSettingsViewModel>();

            LoadExistingSettings();
        }

        #endregion

        #region 保存/返回命令

        /// <summary>保存所有设备配置到文件</summary>
        [RelayCommand(CanExecute = nameof(CanEditSettings))]
        private async Task SaveAllConfigAsync()
        {
            if (IsSavingConfig)
                return;

            var errors = ValidateAllConfigs();
            if (errors.Count > 0)
            {
                string errorMessage = "以下参数设置有误，请修正后再保存：\n\n" + string.Join("\n", errors);
                await _notificationService.ShowWarningAsync(errorMessage, "参数校验失败");
                _logger.Warning("系统设置保存被拒绝，校验错误 {Count} 项", errors.Count);
                return;
            }

            string? resultMessage = null;
            bool showAsWarning = false;
            bool showAsError = false;
            string previousCsvRootPath = _csvStorageSettings.RootPath;
            string previousTestLogPath = CurrentTestLogPath;
            bool csvPathApplied = false;

            IsSavingConfig = true;
            SaveStatusText = "正在保存并应用配置，请稍候……";
            _logger.Information("[系统设置][保存] 开始");
            try
            {
                // 保存前从磁盘读取快照，避免 UI 双向绑定已原地修改对象而丢失旧值。
                var previousSettings = _settingsService.LoadSettings();

                var deviceSettings = new DeviceSettings
                {
                    FP0HCommunication = ClonePlcConfig(Fp0hConfig),
                    ScannerSerialCommunication = CloneScannerConfig(ScannerConfig),
                    GDM9060Communication = CloneDmmConfig(Gdm9060Config),
                    ContinueTestingAfterNg = ContinueTestingAfterNg,
                    SaveNgInspectionResult = ContinueTestingAfterNg && SaveNgInspectionResult
                };

                _settingsService.SaveSettings(deviceSettings);
                await SaveCsvStoragePathAsync();
                csvPathApplied = true;

                bool plcChanged = HasPlcConnectionChanged(previousSettings.FP0HCommunication, Fp0hConfig);
                bool dmmChanged = HasDmmConnectionChanged(previousSettings.GDM9060Communication, Gdm9060Config);
                bool scannerChanged = HasScannerConnectionChanged(previousSettings.ScannerSerialCommunication, ScannerConfig);

                DeviceReconnectSummary? reconnectSummary = null;
                try
                {
                    reconnectSummary = await _deviceManager
                        .ApplySettingsAndReconnectAsync(plcChanged, dmmChanged, scannerChanged)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[系统设置][保存] 设备连接应用失败");
                    resultMessage = "配置已保存，但设备连接失败，请检查设备。";
                    showAsWarning = true;
                }

                _logger.Warning(
                    "[系统设置][审计] 单项 NG 后继续测试={ContinueTestingAfterNg}, 单项 NG 后继续保存={SaveNgInspectionResult}",
                    ContinueTestingAfterNg, SaveNgInspectionResult);

                if (reconnectSummary is not null)
                {
                    foreach (var result in reconnectSummary.Results.Where(item => item.Requested))
                    {
                        _logger.Information(
                            "[系统设置][保存] 设备重连结果 Device={DeviceType}, Connected={Connected}, Status={StatusText}",
                            result.DeviceType, result.IsConnected, result.StatusText);
                    }

                    bool hasConnectionFailure = reconnectSummary.Results
                        .Any(item => item.Requested && !item.IsConnected);
                    resultMessage = hasConnectionFailure
                        ? "配置已保存，但设备连接失败，请检查设备。"
                        : "配置已保存成功。";
                    showAsWarning = hasConnectionFailure;
                }

                _logger.Information("[系统设置][保存] 配置处理完成");
            }
            catch (Exception ex)
            {
                if (!csvPathApplied)
                    RestoreCsvPathPreview(previousCsvRootPath, previousTestLogPath);

                _logger.Error(ex, "保存配置失败");
                resultMessage = $"配置保存失败：{ex.Message}";
                showAsError = true;
            }
            finally
            {
                SaveStatusText = string.Empty;
                IsSavingConfig = false;
                _logger.Information("[系统设置][保存] Loading 已关闭");
            }

            await WaitForSaveOverlayToCloseAsync();

            if (string.IsNullOrWhiteSpace(resultMessage))
                return;

            _logger.Information("[系统设置][保存] 显示结果提示");
            if (showAsError)
                await _notificationService.ShowErrorAsync(resultMessage, "保存失败");
            else if (showAsWarning)
                await _notificationService.ShowWarningAsync(resultMessage, "保存完成");
            else
                await _notificationService.ShowInfoAsync(resultMessage, "保存成功");
        }

        private bool CanEditSettings() => !IsSavingConfig;

        private static bool HasPlcConnectionChanged(FP0HCommunicationConfig? before, FP0HCommunicationConfig after)
            => before is null || before.IpAddress != after.IpAddress || before.Port != after.Port
                || before.SlaveId != after.SlaveId;

        private static bool HasDmmConnectionChanged(GDM9060CommunicationConfig? before, GDM9060CommunicationConfig after)
            => before is null || before.IpAddress != after.IpAddress || before.Port != after.Port
                || before.ReceiveTimeoutMs != after.ReceiveTimeoutMs;

        private static bool HasScannerConnectionChanged(ScannerSerialCommunicationConfig? before, ScannerSerialCommunicationConfig after)
            => before is null
                || !string.Equals(before.SerialNumber, after.SerialNumber, StringComparison.OrdinalIgnoreCase)
                || before.BaudRate != after.BaudRate
                || !string.Equals(before.Parity, after.Parity, StringComparison.OrdinalIgnoreCase)
                || before.DataBits != after.DataBits
                || !string.Equals(before.StopBits, after.StopBits, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(before.FlowControl, after.FlowControl, StringComparison.OrdinalIgnoreCase);

        private static bool IsSameScannerConnection(ScannerSerialCommunicationConfig? saved, ScannerSerialCommunicationConfig input)
            => saved is not null
                && string.Equals(saved.SerialNumber, input.SerialNumber, StringComparison.OrdinalIgnoreCase)
                && saved.BaudRate == input.BaudRate
                && string.Equals(saved.Parity, input.Parity, StringComparison.OrdinalIgnoreCase)
                && saved.DataBits == input.DataBits
                && string.Equals(saved.StopBits, input.StopBits, StringComparison.OrdinalIgnoreCase)
                && string.Equals(saved.FlowControl, input.FlowControl, StringComparison.OrdinalIgnoreCase);

        private static bool IsSamePlcConnectionTarget(FP0HCommunicationConfig? saved, FP0HCommunicationConfig input)
            => saved is not null
                && string.Equals(saved.IpAddress, input.IpAddress, StringComparison.OrdinalIgnoreCase)
                && saved.Port == input.Port
                && saved.SlaveId == input.SlaveId;

        private static bool IsSameDmmConnectionTarget(GDM9060CommunicationConfig? saved, GDM9060CommunicationConfig input)
            => saved is not null
                && string.Equals(saved.IpAddress, input.IpAddress, StringComparison.OrdinalIgnoreCase)
                && saved.Port == input.Port;

        private static FP0HCommunicationConfig ClonePlcConfig(FP0HCommunicationConfig source)
            => new()
            {
                IpAddress = source.IpAddress,
                Port = source.Port,
                SlaveId = source.SlaveId
            };

        private static GDM9060CommunicationConfig CloneDmmConfig(GDM9060CommunicationConfig source)
            => new()
            {
                IpAddress = source.IpAddress,
                Port = source.Port,
                ReceiveTimeoutMs = source.ReceiveTimeoutMs,
                ContinuityThresholdOhm = source.ContinuityThresholdOhm
            };

        private static ScannerSerialCommunicationConfig CloneScannerConfig(ScannerSerialCommunicationConfig source)
            => new()
            {
                SerialNumber = source.SerialNumber,
                BaudRate = source.BaudRate,
                Parity = source.Parity,
                DataBits = source.DataBits,
                StopBits = source.StopBits,
                FlowControl = source.FlowControl
            };

        private void RestoreCsvPathPreview(string rootPath, string previousTestLogPath)
        {
            bool useDefaultPath = string.IsNullOrWhiteSpace(rootPath)
                || rootPath.Equals("Default", StringComparison.OrdinalIgnoreCase);

            UseDefaultStoragePath = useDefaultPath;
            CustomStoragePath = useDefaultPath ? string.Empty : rootPath;
            CsvStorageRootPath = rootPath;
            CurrentTestLogPath = previousTestLogPath;
        }

        private static async Task WaitForSaveOverlayToCloseAsync()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                await Task.Yield();
                return;
            }

            await dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Render);
        }

        /// <summary>返回主菜单</summary>
        [RelayCommand(CanExecute = nameof(CanEditSettings))]
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

        /// <summary>浏览选择存储文件夹</summary>
        [RelayCommand(CanExecute = nameof(CanEditSettings))]
        private void BrowseStoragePath()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "请选择 CSV 日志文件的存储根目录",
                    Multiselect = false
                };

                if (!string.IsNullOrWhiteSpace(CustomStoragePath) && Directory.Exists(CustomStoragePath))
                {
                    dialog.FolderName = CustomStoragePath;
                }
                else
                {
                    dialog.FolderName = AppDomain.CurrentDomain.BaseDirectory;
                }

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

        /// <summary>打开当前 TestLog 文件夹</summary>
        [RelayCommand(CanExecute = nameof(CanEditSettings))]
        private void OpenTestLogFolder()
        {
            try
            {
                var testLogPath = _csvPathManager.GetTestLogRootPath();
                Directory.CreateDirectory(testLogPath);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = testLogPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "打开 TestLog 文件夹失败");
                _notificationService.ShowErrorAsync($"打开文件夹失败：{ex.Message}");
            }
        }

        #endregion

        #region PLC 测试连接命令

        /// <summary>
        /// 测试FP0H PLC连接。
        /// 输入目标与已保存生产目标相同且生产已连接时，不创建第二条 Modbus 连接。
        /// 其余情况通过 IDeviceConnectionManager.TestPlcConfigurationAsync 使用独立临时测试器，
        /// 复用 _plcLock 与生产重连互斥。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEditSettings))]
        private async Task TestPlcConnectionAsync()
        {
            // 防止重复点击
            if (IsTestingPlc) return;

            // 取消之前的清除任务
            CancelTestClear(ref _plcTestClearCts);

            IsTestingPlc = true;
            PlcTestButtonText = "⏳ 测试中...";
            PlcTestStatus = "Gray";
            PlcTestMessage = $"正在连接 {Fp0hConfig.IpAddress}:{Fp0hConfig.Port} ...";
            _logger.Information("PLC测试连接开始: {Host}:{Port}", Fp0hConfig.IpAddress, Fp0hConfig.Port);

            try
            {
                // 参数基础校验
                if (!Common.Validators.InputValidationHelper.IsValidIpAddress(Fp0hConfig.IpAddress))
                {
                    SetPlcTestResult(false, $"IP地址格式不正确: \"{Fp0hConfig.IpAddress}\"");
                    return;
                }
                if (!Common.Validators.InputValidationHelper.IsValidPort(Fp0hConfig.Port))
                {
                    SetPlcTestResult(false, $"端口号超出范围: {Fp0hConfig.Port}（应为1~65535）");
                    return;
                }
                if (!Common.Validators.InputValidationHelper.IsValidModbusSlaveId(Fp0hConfig.SlaveId))
                {
                    SetPlcTestResult(false,
                        $"PLC Modbus从站ID {Fp0hConfig.SlaveId} 超出范围（应为 {Common.Validators.InputValidationHelper.ModbusSlaveIdMin}~{Common.Validators.InputValidationHelper.ModbusSlaveIdMax}）");
                    return;
                }

                var savedSettings = _settingsService.LoadSettings();
                if (IsSamePlcConnectionTarget(savedSettings.FP0HCommunication, Fp0hConfig)
                    && _deviceManager.IsPlcConnected)
                {
                    SetPlcTestResult(true,
                        $"当前生产连接正常 — FP0H @ {Fp0hConfig.IpAddress}:{Fp0hConfig.Port}, UnitId={Fp0hConfig.SlaveId}（当前配置已生效）");

                    _logger.Warning(
                        "[临时测试][PLC] 跳过第二连接，当前输入目标与生产配置相同且生产连接正常 Host={Host} Port={Port} UnitId={UnitId}",
                        Fp0hConfig.IpAddress,
                        Fp0hConfig.Port,
                        Fp0hConfig.SlaveId);

                    return;
                }

                // 通过 DeviceConnectionManager 使用独立临时测试器，复用 _plcLock
                using var cts = new CancellationTokenSource(TEST_CONNECTION_TIMEOUT_MS);

                bool success = await _deviceManager
                    .TestPlcConfigurationAsync(
                        ClonePlcConfig(Fp0hConfig),
                        cts.Token)
                    .ConfigureAwait(true);

                if (success)
                {
                    SetPlcTestResult(true,
                        $"输入配置测试成功 — FP0H @ {Fp0hConfig.IpAddress}:{Fp0hConfig.Port}, UnitId={Fp0hConfig.SlaveId}\n当前生产设备仍按已保存配置运行；保存后才会应用此配置。");
                    _logger.Warning("[临时测试][PLC] 成功 Host={Host} Port={Port} UnitId={UnitId}",
                        Fp0hConfig.IpAddress, Fp0hConfig.Port, Fp0hConfig.SlaveId);
                }
                else
                {
                    SetPlcTestResult(false, $"连接失败: {Fp0hConfig.IpAddress}:{Fp0hConfig.Port}, UnitId={Fp0hConfig.SlaveId} 不可达或未响应Modbus");
                    _logger.Warning("[临时测试][PLC] 失败 Host={Host} Port={Port} UnitId={UnitId}",
                        Fp0hConfig.IpAddress, Fp0hConfig.Port, Fp0hConfig.SlaveId);
                }
            }
            catch (OperationCanceledException)
            {
                SetPlcTestResult(false, "连接超时：设备无响应");
                _logger.Warning("PLC测试连接超时");
            }
            catch (Exception ex)
            {
                SetPlcTestResult(false, $"连接失败: {ex.Message}");
                _logger.Error(ex, "PLC测试连接异常");
            }
            finally
            {
                IsTestingPlc = false;
                PlcTestButtonText = "🔍 测试连接";
                ScheduleTestResultClear(ref _plcTestClearCts, ClearPlcTestResult);
            }
        }

        /// <summary>设置PLC测试结果并更新UI状态</summary>
        private void SetPlcTestResult(bool success, string message)
        {
            PlcTestStatus = success ? "Green" : "Red";
            PlcTestMessage = (success ? "✅ " : "❌ ") + message;
        }

        /// <summary>清除PLC测试结果显示</summary>
        private void ClearPlcTestResult()
        {
            PlcTestStatus = "Gray";
            PlcTestMessage = string.Empty;
        }

        #endregion

        #region 扫描枪 测试连接命令

        /// <summary>
        /// 测试扫描枪连接。
        /// 输入配置与已保存生产配置相同且生产连接正常时，仅说明端口被生产实例占用。
        /// 其他场景创建临时 HoneywellH1900Scanner 实例测试当前输入串口，用完即释放。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEditSettings))]
        private async Task TestScannerConnectionAsync()
        {
            if (IsTestingScanner) return;

            CancelTestClear(ref _scannerTestClearCts);

            IsTestingScanner = true;
            ScannerTestButtonText = "⏳ 测试中...";
            ScannerTestStatus = "Gray";
            ScannerTestMessage = $"正在打开串口 {ScannerConfig.SerialNumber} ...";
            _logger.Information("扫描枪测试连接开始: Port={Port}, BaudRate={BaudRate}",
                ScannerConfig.SerialNumber, ScannerConfig.BaudRate);

            HoneywellH1900Scanner? tempScanner = null;
            try
            {
                // 参数校验
                if (!Common.Validators.InputValidationHelper.IsValidComPort(ScannerConfig.SerialNumber))
                {
                    SetScannerTestResult(false, $"串口号格式不正确: \"{ScannerConfig.SerialNumber}\"（应为 COM1~COM256）");
                    return;
                }
                if (!Common.Validators.InputValidationHelper.IsValidBaudRate(ScannerConfig.BaudRate)
                    || !Common.Validators.InputValidationHelper.IsValidParity(ScannerConfig.Parity)
                    || !Common.Validators.InputValidationHelper.IsValidDataBits(ScannerConfig.DataBits)
                    || !Common.Validators.InputValidationHelper.IsValidStopBits(ScannerConfig.StopBits)
                    || !Common.Validators.InputValidationHelper.IsValidFlowControl(ScannerConfig.FlowControl))
                {
                    SetScannerTestResult(false, "扫描枪串口参数无效，请检查波特率、校验位、数据位、停止位和流控制");
                    return;
                }

                var savedSettings = _settingsService.LoadSettings();
                if (IsSameScannerConnection(savedSettings.ScannerSerialCommunication, ScannerConfig)
                    && _deviceManager.IsScannerConnected)
                {
                    SetScannerTestResult(true,
                        $"当前生产连接正常 — {ScannerConfig.SerialNumber} @ {ScannerConfig.BaudRate}bps（该端口正由程序使用）");
                    _logger.Warning("[临时测试][Scanner] 跳过临时打开，生产端口占用 Port={Port} BaudRate={BaudRate}",
                        ScannerConfig.SerialNumber, ScannerConfig.BaudRate);
                    return;
                }

                // 创建临时扫描枪驱动实例
                var scannerLogger = _loggerFactory.CreateLogger<HoneywellH1900Scanner>();
                tempScanner = new HoneywellH1900Scanner(scannerLogger);
                tempScanner.PortName = ScannerConfig.SerialNumber;
                tempScanner.BaudRate = ScannerConfig.BaudRate;
                tempScanner.Parity = ScannerConfig.Parity;
                tempScanner.DataBits = ScannerConfig.DataBits;
                tempScanner.StopBits = ScannerConfig.StopBits;
                tempScanner.FlowControl = ScannerConfig.FlowControl;

                using var cts = new CancellationTokenSource(TEST_CONNECTION_TIMEOUT_MS);
                bool success = await tempScanner.ConnectAsync(cts.Token).ConfigureAwait(true);

                if (success)
                {
                    SetScannerTestResult(true,
                        $"串口打开成功 — {ScannerConfig.SerialNumber} @ {ScannerConfig.BaudRate}bps");
                    _logger.Warning("[临时测试][Scanner] 成功 Port={Port} BaudRate={BaudRate}",
                        ScannerConfig.SerialNumber, ScannerConfig.BaudRate);
                }
                else
                {
                    SetScannerTestResult(false,
                        $"连接失败: 无法打开 {ScannerConfig.SerialNumber}，请检查端口是否存在或被占用");
                    _logger.Warning("[临时测试][Scanner] 失败 Port={Port} BaudRate={BaudRate}",
                        ScannerConfig.SerialNumber, ScannerConfig.BaudRate);
                }
            }
            catch (UnauthorizedAccessException)
            {
                SetScannerTestResult(false, $"连接失败: {ScannerConfig.SerialNumber} 被其他程序占用");
                _logger.Warning("扫描枪测试连接失败: {Port} 端口被占用", ScannerConfig.SerialNumber);
            }
            catch (Exception ex)
            {
                SetScannerTestResult(false, $"连接失败: {ex.Message}");
                _logger.Error(ex, "扫描枪测试连接异常");
            }
            finally
            {
                // 确保测试连接断开并释放资源
                if (tempScanner != null)
                {
                    try
                    {
                        await tempScanner.DisconnectAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "断开测试扫描枪连接时异常（可忽略）");
                    }
                    tempScanner.Dispose();
                }

                IsTestingScanner = false;
                ScannerTestButtonText = "🔍 测试连接";
                ScheduleTestResultClear(ref _scannerTestClearCts, ClearScannerTestResult);
            }
        }

        /// <summary>设置扫描枪测试结果</summary>
        private void SetScannerTestResult(bool success, string message)
        {
            ScannerTestStatus = success ? "Green" : "Red";
            ScannerTestMessage = (success ? "✅ " : "❌ ") + message;
        }

        /// <summary>清除扫描枪测试结果</summary>
        private void ClearScannerTestResult()
        {
            ScannerTestStatus = "Gray";
            ScannerTestMessage = string.Empty;
        }

        #endregion

        #region 万用表 测试连接命令

        /// <summary>
        /// 测试GDM-9060万用表连接。
        /// 输入目标与已保存生产目标相同且生产已连接时，不创建第二条 TCP/SCPI 会话。
        /// 其余情况通过 IDeviceConnectionManager.TestDmmConfigurationAsync 使用独立临时测试器，
        /// 不复用生产 GwInstekGDM9060Driver 实例。
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEditSettings))]
        private async Task TestDmmConnectionAsync()
        {
            if (IsTestingDmm) return;

            CancelTestClear(ref _dmmTestClearCts);

            IsTestingDmm = true;
            DmmTestButtonText = "⏳ 测试中...";
            DmmTestStatus = "Gray";
            DmmTestMessage = $"正在连接 {Gdm9060Config.IpAddress}:{Gdm9060Config.Port} ...";
            _logger.Information("万用表测试连接开始: {Host}:{Port}", Gdm9060Config.IpAddress, Gdm9060Config.Port);

            try
            {
                // 参数校验
                if (!Common.Validators.InputValidationHelper.IsValidIpAddress(Gdm9060Config.IpAddress))
                {
                    SetDmmTestResult(false, $"IP地址格式不正确: \"{Gdm9060Config.IpAddress}\"");
                    return;
                }
                if (!Common.Validators.InputValidationHelper.IsValidPort(Gdm9060Config.Port))
                {
                    SetDmmTestResult(false, $"端口号超出范围: {Gdm9060Config.Port}（应为1~65535）");
                    return;
                }

                var savedSettings = _settingsService.LoadSettings();
                if (IsSameDmmConnectionTarget(savedSettings.GDM9060Communication, Gdm9060Config)
                    && _deviceManager.IsDmmConnected)
                {
                    SetDmmTestResult(true,
                        $"当前生产连接正常 — GDM-9060 @ {Gdm9060Config.IpAddress}:{Gdm9060Config.Port}（当前配置已生效）");

                    _logger.Warning(
                        "[临时测试][DMM] 跳过第二连接，当前输入目标与生产配置相同且生产连接正常 Host={Host} Port={Port}",
                        Gdm9060Config.IpAddress,
                        Gdm9060Config.Port);

                    return;
                }

                // 通过 DeviceConnectionManager 使用独立临时测试器，复用 _dmmLock
                using var cts = new CancellationTokenSource(TEST_CONNECTION_TIMEOUT_MS);

                var idn = await _deviceManager
                    .TestDmmConfigurationAsync(
                        CloneDmmConfig(Gdm9060Config),
                        cts.Token)
                    .ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(idn))
                {
                    SetDmmTestResult(true,
                        $"输入配置测试成功 — {idn}\n当前生产设备仍按已保存配置运行；保存后才会应用此配置。");
                    _logger.Warning("[临时测试][DMM] 成功 Host={Host} Port={Port} IDN={Idn}",
                        Gdm9060Config.IpAddress, Gdm9060Config.Port, idn);
                }
                else
                {
                    SetDmmTestResult(false,
                        $"连接失败: {Gdm9060Config.IpAddress}:{Gdm9060Config.Port} 不可达或未响应SCPI");
                    _logger.Warning("[临时测试][DMM] 失败 Host={Host} Port={Port}",
                        Gdm9060Config.IpAddress, Gdm9060Config.Port);
                }
            }
            catch (OperationCanceledException)
            {
                SetDmmTestResult(false, "连接超时：设备无响应");
                _logger.Warning("DMM测试连接超时");
            }
            catch (Exception ex)
            {
                SetDmmTestResult(false, $"连接失败: {ex.Message}");
                _logger.Error(ex, "DMM测试连接异常");
            }
            finally
            {
                IsTestingDmm = false;
                DmmTestButtonText = "🔍 测试连接";
                ScheduleTestResultClear(ref _dmmTestClearCts, ClearDmmTestResult);
            }
        }

        /// <summary>设置万用表测试结果</summary>
        private void SetDmmTestResult(bool success, string message)
        {
            DmmTestStatus = success ? "Green" : "Red";
            DmmTestMessage = (success ? "✅ " : "❌ ") + message;
        }

        /// <summary>清除万用表测试结果</summary>
        private void ClearDmmTestResult()
        {
            DmmTestStatus = "Gray";
            DmmTestMessage = string.Empty;
        }

        #endregion

        #region 测试结果自动清除辅助

        /// <summary>
        /// 取消之前的测试结果清除任务
        /// </summary>
        private static void CancelTestClear(ref CancellationTokenSource? cts)
        {
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
                cts = null;
            }
        }

        /// <summary>
        /// 延迟指定时间后执行清除操作
        /// </summary>
        private void ScheduleTestResultClear(ref CancellationTokenSource? cts, Action clearAction)
        {
            CancelTestClear(ref cts);
            cts = new CancellationTokenSource();
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TEST_RESULT_CLEAR_DELAY_MS, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        // 回到UI线程执行清除
                        System.Windows.Application.Current?.Dispatcher.Invoke(clearAction);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常的取消操作
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "自动清除测试结果时异常（可忽略）");
                }
            }, token);
        }

        #endregion

        #region 校验方法

        /// <summary>全面校验三个设备的所有配置参数</summary>
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
            // ─── 扫描枪 H1900 校验 ───
            if (!Common.Validators.InputValidationHelper.IsValidComPort(ScannerConfig.SerialNumber))
                errors.Add($"扫描枪串口号 \"{ScannerConfig.SerialNumber}\" 格式不正确（应为 COM1~COM256）");
            if (!Common.Validators.InputValidationHelper.IsValidBaudRate(ScannerConfig.BaudRate))
                errors.Add($"扫描枪波特率 {ScannerConfig.BaudRate} 不在允许列表内");
            if (!Common.Validators.InputValidationHelper.IsValidParity(ScannerConfig.Parity))
                errors.Add($"扫描枪校验位 {ScannerConfig.Parity} 无效");
            if (!Common.Validators.InputValidationHelper.IsValidDataBits(ScannerConfig.DataBits))
                errors.Add($"扫描枪数据位 {ScannerConfig.DataBits} 无效");
            if (!Common.Validators.InputValidationHelper.IsValidStopBits(ScannerConfig.StopBits))
                errors.Add($"扫描枪停止位 {ScannerConfig.StopBits} 无效");
            if (!Common.Validators.InputValidationHelper.IsValidFlowControl(ScannerConfig.FlowControl))
                errors.Add($"扫描枪流控制 {ScannerConfig.FlowControl} 无效");

            // ─── GDM-9060 万用表校验 ───
            if (!Common.Validators.InputValidationHelper.IsValidIpAddress(Gdm9060Config.IpAddress))
                errors.Add($"万用表 IP地址 \"{Gdm9060Config.IpAddress}\" 格式不正确");
            if (!Common.Validators.InputValidationHelper.IsValidPort(Gdm9060Config.Port))
                errors.Add($"万用表 端口号 {Gdm9060Config.Port} 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidTimeoutMs(Gdm9060Config.ReceiveTimeoutMs))
                errors.Add($"万用表通信超时 {Gdm9060Config.ReceiveTimeoutMs}ms 超出范围");
            if (Gdm9060Config.ContinuityThresholdOhm < 1.0 || Gdm9060Config.ContinuityThresholdOhm > 1000.0)
                errors.Add($"万用表导通阈值 {Gdm9060Config.ContinuityThresholdOhm}Ω 超出范围（应为 1~1000Ω）");

            return errors;
        }

        #endregion

        #region 配置加载与保存

        /// <summary>从文件加载已保存的设备配置</summary>
        private void LoadExistingSettings()
        {
            try
            {
                var deviceSettings = _settingsService.LoadSettings();

                if (deviceSettings.FP0HCommunication != null)
                    Fp0hConfig = ClonePlcConfig(deviceSettings.FP0HCommunication);
                if (deviceSettings.ScannerSerialCommunication != null)
                    ScannerConfig = CloneScannerConfig(deviceSettings.ScannerSerialCommunication);
                if (deviceSettings.GDM9060Communication != null)
                    Gdm9060Config = CloneDmmConfig(deviceSettings.GDM9060Communication);

                ContinueTestingAfterNg = deviceSettings.ContinueTestingAfterNg;
                SaveNgInspectionResult = deviceSettings.SaveNgInspectionResult;
                LoadCsvStoragePath();

                _logger.Debug("设备配置加载完成");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "加载已有配置失败，使用默认值");
            }
        }

        /// <summary>加载CSV存储路径配置</summary>
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

        /// <summary>保存CSV存储路径到appsettings.json</summary>
        private async Task SaveCsvStoragePathAsync()
        {
            string newPath = UseDefaultStoragePath ? "Default" : ValidateAndNormalizeCustomStoragePath(CustomStoragePath);
            using var configManager = new ConfigManagerService(_configuration);
            var updates = new Dictionary<string, object>
            {
                ["CsvStorage:RootPath"] = newPath
            };

            bool saved = await configManager.SaveConfigurationAsync(updates);
            if (!saved)
                throw new IOException("CSV 存储路径配置写入失败");

            _csvStorageSettings.ApplyRuntimeRootPath(newPath);
            _logger.Information("CSV存储路径已保存: {Path}", newPath);
            UpdateTestLogPreview();
        }

        /// <summary>
        /// 保存自定义路径前只做一次目录创建和临时写入测试，避免配置保存成功但运行时无法写 CSV。
        /// </summary>
        private static string ValidateAndNormalizeCustomStoragePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("自定义 CSV 存储路径不能为空");

            string normalizedPath = Path.GetFullPath(path.Trim());
            Directory.CreateDirectory(normalizedPath);

            string probePath = Path.Combine(normalizedPath, $".csv-write-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probePath, string.Empty);
            }
            finally
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }

            return normalizedPath;
        }

        /// <summary>
        /// 刷新系统设置界面的 TestLog 路径预览。
        /// 路径规则必须与实际 CSV 保存规则保持一致。
        /// </summary>
        private void UpdateTestLogPreview()
        {
            string rootPath = UseDefaultStoragePath || string.IsNullOrWhiteSpace(CustomStoragePath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : CustomStoragePath;

            CurrentTestLogPath = CsvStoragePathManager.BuildTestLogRootPath(rootPath);
        }

        #endregion

        #region 导航生命周期

        public Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.Debug("进入系统设定页面");
            LoadExistingSettings();
            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            // 离开页面时取消所有自动清除任务
            CancelTestClear(ref _plcTestClearCts);
            CancelTestClear(ref _scannerTestClearCts);
            CancelTestClear(ref _dmmTestClearCts);

            // 重置所有测试状态
            ClearPlcTestResult();
            ClearScannerTestResult();
            ClearDmmTestResult();

            _logger.Debug("离开系统设定页面");
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion
    }
}
