using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 系统设定页面的ViewModel
    /// 负责三个设备的参数配置展示、保存/恢复默认、PLC通信测试开关
    /// 以及设备测试连接功能
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
        private readonly IModbusTcpClient _modbusClient;

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

        /// <summary>是否开启PLC通信测试功能（勾选后主菜单显示对应按钮）</summary>
        [ObservableProperty]
        private bool _isPlcCommunicationTestEnabled = false;

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
            IDeviceConnectionManager deviceManager,
            IModbusTcpClient modbusClient)
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
            _modbusClient = modbusClient ?? throw new ArgumentNullException(nameof(modbusClient));
            _logger = Log.ForContext<SystemSettingsViewModel>();

            LoadExistingSettings();
        }

        #endregion

        #region 保存/恢复/返回命令

        /// <summary>恢复所有设备配置为默认值</summary>
        [RelayCommand]
        private async Task ResetToDefaultAsync()
        {
            try
            {
                bool confirmed = await _notificationService.ConfirmAsync(
                    "确定要恢复所有设备参数为默认配置吗？\n当前修改将丢失。",
                    "恢复默认配置");

                if (!confirmed) return;

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

        /// <summary>保存所有设备配置到文件</summary>
        [RelayCommand]
        private async Task SaveAllConfigAsync()
        {
            try
            {
                var errors = ValidateAllConfigs();
                if (errors.Count > 0)
                {
                    string errorMessage = "以下参数设置有误，请修正后再保存：\n\n" + string.Join("\n", errors);
                    await _notificationService.ShowWarningAsync(errorMessage, "参数校验失败");
                    _logger.Warning("系统设置保存被拒绝，校验错误 {Count} 项", errors.Count);
                    return;
                }

                var deviceSettings = new DeviceSettings
                {
                    FP0HCommunication = Fp0hConfig,
                    ScannerSerialCommunication = ScannerConfig,
                    GDM9060Communication = Gdm9060Config,
                    IsPlcCommunicationTestEnabled = IsPlcCommunicationTestEnabled,
                    ContinueTestingAfterNg = ContinueTestingAfterNg,
                    SaveNgInspectionResult = ContinueTestingAfterNg && SaveNgInspectionResult
                };

                _settingsService.SaveSettings(deviceSettings);
                await SaveCsvStoragePathAsync();

                _logger.Warning(
                    "[系统设置][审计] 单项 NG 后继续测试={ContinueTestingAfterNg}, 单项 NG 后继续保存={SaveNgInspectionResult}",
                    ContinueTestingAfterNg, SaveNgInspectionResult);
                await _notificationService.ShowInfoAsync("所有配置已保存成功！存储路径修改立即生效，无需重启。");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "保存配置失败");
                await _notificationService.ShowErrorAsync($"保存配置失败：{ex.Message}");
            }
        }

        /// <summary>返回主菜单</summary>
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

        /// <summary>浏览选择存储文件夹</summary>
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

        #endregion

        #region PLC 测试连接命令

        /// <summary>
        /// 测试FP0H PLC连接。
        /// 生产已连接时直接返回成功，不干扰持久连接。
        /// 未连接时委托 IModbusTcpClient.TestConnectionAsync 创建独立连接测试，不经过生产单例。
        /// </summary>
        [RelayCommand]
        private async Task TestPlcConnectionAsync()
        {
            // 防止重复点击
            if (IsTestingPlc) return;

            // 取消之前的清除任务
            CancelTestClear(ref _plcTestClearCts);

            // ⭐ 生产已连接则直接返回成功
            if (_deviceManager.IsPlcConnected)
            {
                SetPlcTestResult(true, "PLC已连接（生产连接正常）");
                ScheduleTestResultClear(ref _plcTestClearCts, ClearPlcTestResult);
                return;
            }

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

                // 委托给 IModbusTcpClient.TestConnectionAsync，复用已验证的 Modbus TCP 握手逻辑
                bool success = await _modbusClient.TestConnectionAsync(
                    Fp0hConfig.IpAddress, Fp0hConfig.Port,
                    TEST_CONNECTION_TIMEOUT_MS).ConfigureAwait(true);

                if (success)
                {
                    SetPlcTestResult(true, $"连接成功 — FP0H @ {Fp0hConfig.IpAddress}:{Fp0hConfig.Port}");
                    _logger.Information("PLC测试连接成功: {Host}:{Port}", Fp0hConfig.IpAddress, Fp0hConfig.Port);
                }
                else
                {
                    SetPlcTestResult(false, $"连接失败: {Fp0hConfig.IpAddress}:{Fp0hConfig.Port} 不可达或未响应Modbus");
                    _logger.Warning("PLC测试连接失败: {Host}:{Port}", Fp0hConfig.IpAddress, Fp0hConfig.Port);
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
        /// 生产已连时直接返回成功（避免Windows COM口独占导致的假阴性）。
        /// 未连接时创建临时HoneywellH1900Scanner实例测试串口通信，用完即释放。
        /// </summary>
        [RelayCommand]
        private async Task TestScannerConnectionAsync()
        {
            if (IsTestingScanner) return;

            CancelTestClear(ref _scannerTestClearCts);

            // ⭐ 生产已连接则直接返回成功
            if (_deviceManager.IsScannerConnected)
            {
                SetScannerTestResult(true, "扫描枪已连接（生产连接正常）");
                ScheduleTestResultClear(ref _scannerTestClearCts, ClearScannerTestResult);
                return;
            }

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

                // 创建临时扫描枪驱动实例
                var scannerLogger = _loggerFactory.CreateLogger<HoneywellH1900Scanner>();
                tempScanner = new HoneywellH1900Scanner(scannerLogger);
                tempScanner.PortName = ScannerConfig.SerialNumber;
                tempScanner.BaudRate = ScannerConfig.BaudRate;

                using var cts = new CancellationTokenSource(TEST_CONNECTION_TIMEOUT_MS);
                bool success = await tempScanner.ConnectAsync(cts.Token).ConfigureAwait(true);

                if (success)
                {
                    SetScannerTestResult(true,
                        $"连接成功 — {ScannerConfig.SerialNumber} 已打开 @ {ScannerConfig.BaudRate}bps");
                    _logger.Information("扫描枪测试连接成功: {Port}", ScannerConfig.SerialNumber);
                }
                else
                {
                    SetScannerTestResult(false,
                        $"连接失败: 无法打开 {ScannerConfig.SerialNumber}，请检查端口是否存在或被占用");
                    _logger.Warning("扫描枪测试连接失败: {Port}", ScannerConfig.SerialNumber);
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
        /// 生产已连时直接返回成功。未连接时创建临时GwInstekGDM9060Driver实例测试TCP+SCPI通信。
        /// </summary>
        [RelayCommand]
        private async Task TestDmmConnectionAsync()
        {
            if (IsTestingDmm) return;

            CancelTestClear(ref _dmmTestClearCts);

            // ⭐ 生产已连接则直接返回成功
            if (_deviceManager.IsDmmConnected)
            {
                SetDmmTestResult(true, "万用表已连接（生产连接正常）");
                ScheduleTestResultClear(ref _dmmTestClearCts, ClearDmmTestResult);
                return;
            }

            IsTestingDmm = true;
            DmmTestButtonText = "⏳ 测试中...";
            DmmTestStatus = "Gray";
            DmmTestMessage = $"正在连接 {Gdm9060Config.IpAddress}:{Gdm9060Config.Port} ...";
            _logger.Information("万用表测试连接开始: {Host}:{Port}", Gdm9060Config.IpAddress, Gdm9060Config.Port);

            GwInstekGDM9060Driver? tempDmm = null;
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

                // 创建临时万用表驱动实例
                var dmmLogger = _loggerFactory.CreateLogger<GwInstekGDM9060Driver>();
                tempDmm = new GwInstekGDM9060Driver(dmmLogger);
                tempDmm.Host = Gdm9060Config.IpAddress;
                tempDmm.Port = Gdm9060Config.Port;
                tempDmm.TimeoutMs = Gdm9060Config.ReceiveTimeoutMs;

                using var cts = new CancellationTokenSource(TEST_CONNECTION_TIMEOUT_MS);
                bool success = await tempDmm.ConnectAsync(cts.Token).ConfigureAwait(true);

                if (success)
                {
                    // 尝试获取设备标识信息
                    string deviceInfo = "GDM-9060";
                    try
                    {
                        var idn = await tempDmm.GetDeviceIdentifierAsync(CancellationToken.None).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(idn))
                        {
                            deviceInfo = idn;
                        }
                    }
                    catch
                    {
                        // 获取标识失败不影响测试结果
                    }

                    SetDmmTestResult(true, $"连接成功 — {deviceInfo}");
                    _logger.Information("万用表测试连接成功: {Host}:{Port}, IDN={Idn}",
                        Gdm9060Config.IpAddress, Gdm9060Config.Port, deviceInfo);
                }
                else
                {
                    SetDmmTestResult(false,
                        $"连接失败: {Gdm9060Config.IpAddress}:{Gdm9060Config.Port} 不可达或未响应SCPI");
                    _logger.Warning("万用表测试连接失败: {Host}:{Port}",
                        Gdm9060Config.IpAddress, Gdm9060Config.Port);
                }
            }
            catch (Exception ex)
            {
                SetDmmTestResult(false, $"连接失败: {ex.Message}");
                _logger.Error(ex, "万用表测试连接异常");
            }
            finally
            {
                // 确保断开并释放
                if (tempDmm != null)
                {
                    try
                    {
                        await tempDmm.DisconnectAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "断开测试万用表连接时异常（可忽略）");
                    }
                    tempDmm.Dispose();
                }

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
            if (!Common.Validators.InputValidationHelper.IsValidTimeoutMs(Fp0hConfig.ReceiveTimeoutMs))
                errors.Add($"PLC 接收超时 {Fp0hConfig.ReceiveTimeoutMs}ms 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidTimeoutMs(Fp0hConfig.SendTimeoutMs))
                errors.Add($"PLC 发送超时 {Fp0hConfig.SendTimeoutMs}ms 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidReconnectDelayMs(Fp0hConfig.ReconnectDelayMs))
                errors.Add($"PLC 重连延迟 {Fp0hConfig.ReconnectDelayMs}ms 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidReconnectAttempts(Fp0hConfig.MaxReconnectAttempts))
                errors.Add($"PLC 最大重连次数 {Fp0hConfig.MaxReconnectAttempts} 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidHealthCheckInterval(Fp0hConfig.HealthCheckIntervalSeconds))
                errors.Add($"PLC 心跳间隔 {Fp0hConfig.HealthCheckIntervalSeconds}秒 超出范围");

            // ─── 扫描枪 H1900 校验 ───
            if (!Common.Validators.InputValidationHelper.IsValidComPort(ScannerConfig.SerialNumber))
                errors.Add($"扫描枪串口号 \"{ScannerConfig.SerialNumber}\" 格式不正确（应为 COM1~COM256）");
            if (!Common.Validators.InputValidationHelper.IsValidHealthCheckInterval(ScannerConfig.HealthCheckIntervalSeconds))
                errors.Add($"扫描枪心跳间隔 {ScannerConfig.HealthCheckIntervalSeconds}秒 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidDataTimeout(ScannerConfig.LastDataTimeoutSeconds))
                errors.Add($"扫描枪数据超时 {ScannerConfig.LastDataTimeoutSeconds}秒 超出范围");

            // ─── GDM-9060 万用表校验 ───
            if (!Common.Validators.InputValidationHelper.IsValidIpAddress(Gdm9060Config.IpAddress))
                errors.Add($"万用表 IP地址 \"{Gdm9060Config.IpAddress}\" 格式不正确");
            if (!Common.Validators.InputValidationHelper.IsValidPort(Gdm9060Config.Port))
                errors.Add($"万用表 端口号 {Gdm9060Config.Port} 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidTimeoutMs(Gdm9060Config.ReceiveTimeoutMs))
                errors.Add($"万用表 接收超时 {Gdm9060Config.ReceiveTimeoutMs}ms 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidTimeoutMs(Gdm9060Config.SendTimeoutMs))
                errors.Add($"万用表 发送超时 {Gdm9060Config.SendTimeoutMs}ms 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidHealthCheckInterval(Gdm9060Config.HealthCheckIntervalSeconds))
                errors.Add($"万用表心跳间隔 {Gdm9060Config.HealthCheckIntervalSeconds}秒 超出范围");
            if (!Common.Validators.InputValidationHelper.IsValidDataTimeout(Gdm9060Config.LastDataTimeoutSeconds))
                errors.Add($"万用表数据超时 {Gdm9060Config.LastDataTimeoutSeconds}秒 超出范围");
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
                    Fp0hConfig = deviceSettings.FP0HCommunication;
                if (deviceSettings.ScannerSerialCommunication != null)
                    ScannerConfig = deviceSettings.ScannerSerialCommunication;
                if (deviceSettings.GDM9060Communication != null)
                    Gdm9060Config = deviceSettings.GDM9060Communication;

                IsPlcCommunicationTestEnabled = deviceSettings.IsPlcCommunicationTestEnabled;
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
            try
            {
                var configManager = new ConfigManagerService(_configuration);
                var newPath = UseDefaultStoragePath ? "Default" : CustomStoragePath;
                var updates = new Dictionary<string, object>
                {
                    ["CsvStorage:RootPath"] = newPath
                };
                await configManager.SaveConfigurationAsync(updates);
                _logger.Information("CSV存储路径已保存: {Path}", newPath);
                UpdateTestLogPreview();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "保存CSV存储路径失败");
                await _notificationService.ShowErrorAsync($"保存存储路径失败：{ex.Message}");
            }
        }

        /// <summary>更新TestLog路径预览</summary>
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
