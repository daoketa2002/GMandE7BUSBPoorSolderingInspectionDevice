// ============================================================
// 文件: Services/DeviceConnectionManager.cs
// 描述: 全局设备连接管理器实现（重构版）
// 修改:
//   - ⭐ 修复扫描仪连接时序：先连接硬件 → 再通知 ScannerBarcodeService
//   - ⭐ 统一扫描仪连接入口，避免与 ScannerBarcodeService 竞态
//   - ⭐ 增加详细的连接状态日志，便于排查问题
//   - ⭐ 手动重连时重新注入配置，确保与 DeviceSettings.json 同步
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 全局设备连接管理器
    /// 单例服务，随应用程序启动初始化，统一管理所有硬件设备
    /// 
    /// 架构说明：
    /// - 本服务是硬件连接的唯一入口
    /// - ScannerBarcodeService 仅负责条码解析，不参与连接管理
    /// - 所有 ViewModel 通过订阅本服务的事件获取设备状态
    /// </summary>
    public class DeviceConnectionManager : IDeviceConnectionManager, IDisposable
    {
        #region 字段

        private readonly ILogger<DeviceConnectionManager> _logger;
        private readonly IPlcDevice _plcDevice;
        private readonly IMultimeterDevice _dmmDevice;
        private readonly IScannerDevice _scannerDevice;
        private readonly IScannerBarcodeService _scannerBarcodeService;
        private readonly IDeviceSettingsService _settingsService;

        // 设备连接状态（线程安全）
        private volatile bool _isPlcConnected;
        private volatile bool _isDmmConnected;
        private volatile bool _isScannerConnected;

        // 重连控制
        private CancellationTokenSource? _reconnectCts;

        // ⭐ 改为每个设备独立锁，允许PLC/DMM/Scanner并行连接
        private readonly SemaphoreSlim _plcLock = new(1, 1);
        private readonly SemaphoreSlim _dmmLock = new(1, 1);
        private readonly SemaphoreSlim _scannerLock = new(1, 1);

        // ⭐ 全局重连互斥锁（防止同一设备同时被手动重连和后台监控重连）
        private readonly SemaphoreSlim _globalReconnectLock = new(1, 1);

        private bool _isDisposed;

        // 后台监控
        private CancellationTokenSource? _monitorCts;
        private Task? _monitorTask;
        private readonly Dictionary<string, DateTime> _lastReconnectAttempt = new()
        {
            ["PLC"] = DateTime.MinValue,
            ["DMM"] = DateTime.MinValue,
            ["Scanner"] = DateTime.MinValue
        };

        // 状态文本缓存
        private string _plcStatusText = "未连接";
        private string _dmmStatusText = "未连接";
        private string _scannerStatusText = "未连接";

        // 重连配置
        private const int RECONNECT_BASE_DELAY_MS = 2000;
        private const int RECONNECT_MAX_DELAY_MS = 30000;
        private const int MAX_RECONNECT_ATTEMPTS = 12;

        // 后台监控配置
        private const int MONITOR_INTERVAL_MS = 5000;
        private const int INITIAL_RECONNECT_INTERVAL_MS = 3000;
        private const int BACKOFF_RECONNECT_INTERVAL_MS = 5000;
        private const int MAX_RECONNECT_INTERVAL_MS = 30000;

        // 每个设备的连续失败次数（用于指数退避）
        private readonly Dictionary<string, int> _consecutiveFailures = new()
        {
            ["PLC"] = 0,
            ["DMM"] = 0,
            ["Scanner"] = 0
        };

        #endregion

        #region 属性

        public bool IsPlcConnected => _isPlcConnected;
        public bool IsDmmConnected => _isDmmConnected;
        public bool IsScannerConnected => _isScannerConnected;

        public bool AreAllDevicesReady =>
            _isPlcConnected && _isDmmConnected && _isScannerConnected;

        public string PlcStatusText => _plcStatusText;
        public string DmmStatusText => _dmmStatusText;
        public string ScannerStatusText => _scannerStatusText;

        #endregion

        #region 事件

        public event EventHandler<DeviceConnectionStateChangedEventArgs>? PlcConnectionStateChanged;
        public event EventHandler<DeviceConnectionStateChangedEventArgs>? DmmConnectionStateChanged;
        public event EventHandler<DeviceConnectionStateChangedEventArgs>? ScannerConnectionStateChanged;
        public event EventHandler<BarcodeParsedEventArgs>? BarcodeScanned;
        public event EventHandler<bool>? AllDevicesReadyChanged;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化设备连接管理器
        /// ⭐ 构造函数中只注入依赖，不发起任何连接
        /// 连接在 StartAllAsync() 中统一启动
        /// </summary>
        public DeviceConnectionManager(
            ILogger<DeviceConnectionManager> logger,
            IPlcDevice plcDevice,
            IMultimeterDevice dmmDevice,
            IScannerDevice scannerDevice,
            IScannerBarcodeService scannerBarcodeService,
            IDeviceSettingsService settingsService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
            _dmmDevice = dmmDevice ?? throw new ArgumentNullException(nameof(dmmDevice));
            _scannerDevice = scannerDevice ?? throw new ArgumentNullException(nameof(scannerDevice));
            _scannerBarcodeService = scannerBarcodeService ?? throw new ArgumentNullException(nameof(scannerBarcodeService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            // 订阅底层硬件事件，统一管理状态变更
            SubscribeToHardwareEvents();

            _logger.LogInformation("DeviceConnectionManager 初始化完成（硬件尚未连接，等待 StartAllAsync）");
        }

        #endregion

        #region 启动与停止

        /// <summary>
        /// 启动设备连接管理器，自动连接所有设备
        /// 
        /// ⭐ 修改后的启动流程：
        /// 1. 从配置文件加载设备参数
        /// 2. 注入配置到各硬件驱动
        /// 3. 并行连接 PLC、万用表、扫描枪
        /// 4. 连接完成后通知 ScannerBarcodeService 初始化
        /// 5. 启动后台监控
        /// </summary>
        public async Task StartAllAsync()
        {
            _logger.LogInformation("========== 开始自动连接所有设备 ==========");

            _reconnectCts = new CancellationTokenSource();

            // 第一步：从配置文件加载设备参数
            var settings = _settingsService.LoadSettings();
            ApplyConfigurationToDevices(settings);

            // 第二步：并行连接所有设备
            var plcTask = ConnectDeviceWithRetryAsync(
                _plcDevice, "PLC", _reconnectCts.Token);
            var dmmTask = ConnectDeviceWithRetryAsync(
                _dmmDevice, "DMM", _reconnectCts.Token);
            var scannerTask = ConnectDeviceWithRetryAsync(
                _scannerDevice, "Scanner", _reconnectCts.Token);

            await Task.WhenAll(plcTask, dmmTask, scannerTask);

            _logger.LogInformation("设备连接初始化完成 - PLC:{Plc}, DMM:{Dmm}, Scanner:{Scanner}",
                _isPlcConnected, _isDmmConnected, _isScannerConnected);

            // ⭐ 第三步：通知 ScannerBarcodeService 初始化（硬件已就绪）
            try
            {
                await _scannerBarcodeService.InitializeAsync();
                _logger.LogInformation("ScannerBarcodeService 初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ScannerBarcodeService 初始化失败");
            }

            // 第四步：启动后台监控（热插拔检测 + 断线自动重连）
            StartBackgroundMonitor();

            _logger.LogInformation("========== 设备连接管理器启动完成 ==========");
        }

        /// <summary>
        /// 停止所有设备连接
        /// </summary>
        public async Task StopAllAsync()
        {
            _logger.LogInformation("正在停止所有设备连接...");

            StopBackgroundMonitor();

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = null;

            try
            {
                await Task.WhenAll(
                    _plcDevice.DisconnectAsync(),
                    _dmmDevice.DisconnectAsync(),
                    _scannerDevice.DisconnectAsync()
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止设备连接时出现异常（可忽略）");
            }

            _isPlcConnected = false;
            _isDmmConnected = false;
            _isScannerConnected = false;

            _logger.LogInformation("所有设备连接已停止");
        }

        /// <summary>
        /// 手动重连指定设备
        /// ⭐ 使用全局锁防止同一设备被并发重连
        /// </summary>
        public async Task ReconnectDeviceAsync(string deviceType)
        {
            _logger.LogInformation("========== 手动重连设备: {DeviceType} ==========", deviceType);

            // ⭐ 重新加载配置
            var settings = _settingsService.LoadSettings();
            ApplyConfigurationToDevices(settings);

            // ⭐ 使用全局锁防止并发重连同一设备
            await _globalReconnectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var ct = _reconnectCts?.Token ?? CancellationToken.None;

                switch (deviceType)
                {
                    case "PLC":
                        SetStatusText("PLC", "连接中...");
                        RaisePlcStateChanged(false);
                        await ConnectDeviceWithRetryAsync(_plcDevice, "PLC", ct);
                        break;
                    case "DMM":
                        SetStatusText("DMM", "连接中...");
                        RaiseDmmStateChanged(false);
                        await ConnectDeviceWithRetryAsync(_dmmDevice, "DMM", ct);
                        break;
                    case "Scanner":
                        SetStatusText("Scanner", "连接中...");
                        RaiseScannerStateChanged(false);
                        _logger.LogInformation("手动重连扫描枪，当前配置端口: {Port}",
                            (_scannerDevice as Devices.Scanner.HoneywellH1900Scanner)?.PortName ?? "未知");
                        await ConnectDeviceWithRetryAsync(_scannerDevice, "Scanner", ct);
                        if (_isScannerConnected)
                        {
                            await _scannerBarcodeService.InitializeAsync();
                        }
                        break;
                    default:
                        _logger.LogWarning("未知设备类型: {DeviceType}", deviceType);
                        break;
                }
            }
            finally
            {
                _globalReconnectLock.Release();
            }

            _logger.LogInformation("========== 手动重连 {DeviceType} 完成，结果: {Result} ==========",
                deviceType, deviceType switch
                {
                    "PLC" => _isPlcConnected ? "成功" : "失败",
                    "DMM" => _isDmmConnected ? "成功" : "失败",
                    "Scanner" => _isScannerConnected ? "成功" : "失败",
                    _ => "未知"
                });
        }

        /// <summary>
        /// 连接指定设备（不先断开，如果已连接则直接返回）
        /// </summary>
        public async Task ConnectDeviceAsync(string deviceType)
        {
            _logger.LogInformation("连接设备: {DeviceType}", deviceType);

            var settings = _settingsService.LoadSettings();
            ApplyConfigurationToDevices(settings);

            var ct = _reconnectCts?.Token ?? CancellationToken.None;

            switch (deviceType)
            {
                case "PLC":
                    if (_isPlcConnected) { _logger.LogInformation("PLC已连接，跳过"); return; }
                    SetStatusText("PLC", "连接中...");
                    await ConnectDeviceWithRetryAsync(_plcDevice, "PLC", ct);
                    break;
                case "DMM":
                    if (_isDmmConnected) { _logger.LogInformation("万用表已连接，跳过"); return; }
                    SetStatusText("DMM", "连接中...");
                    await ConnectDeviceWithRetryAsync(_dmmDevice, "DMM", ct);
                    break;
                case "Scanner":
                    if (_isScannerConnected) { _logger.LogInformation("扫描枪已连接，跳过"); return; }
                    SetStatusText("Scanner", "连接中...");
                    await ConnectDeviceWithRetryAsync(_scannerDevice, "Scanner", ct);
                    if (_isScannerConnected)
                    {
                        await _scannerBarcodeService.InitializeAsync();
                    }
                    break;
                default:
                    _logger.LogWarning("未知设备类型: {DeviceType}", deviceType);
                    break;
            }
        }

        /// <summary>
        /// 断开指定设备
        /// </summary>
        public async Task DisconnectDeviceAsync(string deviceType)
        {
            _logger.LogInformation("断开设备: {DeviceType}", deviceType);

            switch (deviceType)
            {
                case "PLC":
                    await _plcDevice.DisconnectAsync();
                    UpdateConnectionState("PLC", false);
                    break;
                case "DMM":
                    await _dmmDevice.DisconnectAsync();
                    UpdateConnectionState("DMM", false);
                    break;
                case "Scanner":
                    await _scannerDevice.DisconnectAsync();
                    UpdateConnectionState("Scanner", false);
                    break;
                default:
                    _logger.LogWarning("未知设备类型: {DeviceType}", deviceType);
                    break;
            }
        }

        #endregion

        #region 配置注入

        /// <summary>
        /// 从 DeviceSettings 读取配置并注入到各设备驱动属性中
        /// 实现配置驱动连接，与系统设定页联动
        /// </summary>
        private void ApplyConfigurationToDevices(DeviceSettings settings)
        {
            // ========== 注入万用表配置 ==========
            if (settings.GDM9060Communication != null &&
                _dmmDevice is Devices.Multimeter.GwInstekGDM9060Driver dmmDriver)
            {
                dmmDriver.Host = settings.GDM9060Communication.IpAddress;
                dmmDriver.Port = settings.GDM9060Communication.Port;
                dmmDriver.TimeoutMs = settings.GDM9060Communication.ReceiveTimeoutMs;
                _logger.LogDebug("已注入万用表配置: {Host}:{Port}", dmmDriver.Host, dmmDriver.Port);
            }

            // ========== 注入扫描枪配置 ⭐ 增强日志 ==========
            if (settings.ScannerSerialCommunication != null &&
                _scannerDevice is Devices.Scanner.HoneywellH1900Scanner scanner)
            {
                scanner.PortName = settings.ScannerSerialCommunication.SerialNumber;
                scanner.BaudRate = settings.ScannerSerialCommunication.BaudRate;
                _logger.LogInformation("⭐ 已注入扫描枪配置: Port={Port}, BaudRate={BaudRate}, Parity={Parity}",
                    scanner.PortName, scanner.BaudRate, settings.ScannerSerialCommunication.Parity);
            }
            else
            {
                _logger.LogWarning("⚠️ 扫描枪配置注入失败！Settings={Settings}, Device={Device}",
                    settings.ScannerSerialCommunication != null,
                    _scannerDevice?.GetType().Name ?? "null");
            }

            // ========== 注入PLC配置 ==========
            if (settings.FP0HCommunication != null &&
                _plcDevice is Services.TcpModbus.PlcCommunicationAdapter plcAdapter)
            {
                plcAdapter.ApplyConfig(settings.FP0HCommunication);
                _logger.LogDebug("已注入PLC配置: {Host}:{Port}",
                    settings.FP0HCommunication.IpAddress,
                    settings.FP0HCommunication.Port);
            }
        }

        #endregion

        #region 统一设备连接（带重试）

        /// <summary>
        /// 统一设备连接方法（带指数退避重试）
        /// ⭐ 修复：使用独立锁，允许 PLC/DMM/Scanner 并行连接
        /// </summary>
        private async Task ConnectDeviceWithRetryAsync(
            ICommunicationDevice device, string deviceType, CancellationToken ct)
        {
            // ⭐ 根据设备类型选择对应的独立锁
            SemaphoreSlim deviceLock = deviceType switch
            {
                "PLC" => _plcLock,
                "DMM" => _dmmLock,
                "Scanner" => _scannerLock,
                _ => _plcLock
            };

            _logger.LogInformation("🔌 [{DeviceType}] 开始连接流程...", deviceType);

            await deviceLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 检查实际硬件连接状态
                if (device.IsConnected)
                {
                    _logger.LogInformation("[{DeviceType}] 硬件报告已连接，更新状态", deviceType);
                    UpdateConnectionState(deviceType, true);
                    _consecutiveFailures[deviceType] = 0;
                    return;
                }

                _logger.LogInformation("[{DeviceType}] 当前未连接，开始重试连接（最多{Max}次）", deviceType, MAX_RECONNECT_ATTEMPTS);

                int attempt = 0;
                while (!ct.IsCancellationRequested && attempt < MAX_RECONNECT_ATTEMPTS)
                {
                    attempt++;
                    try
                    {
                        _logger.LogInformation("[{DeviceType}] 连接尝试 {Attempt}/{Max}...",
                            deviceType, attempt, MAX_RECONNECT_ATTEMPTS);

                        // ⭐ 扫描枪额外日志：输出当前配置和系统可用串口
                        if (deviceType == "Scanner" && device is Devices.Scanner.HoneywellH1900Scanner scanner)
                        {
                            _logger.LogInformation("[扫描枪] 当前配置 - 端口:{Port}, 波特率:{BaudRate}",
                                scanner.PortName, scanner.BaudRate);

                            try
                            {
                                var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
                                _logger.LogInformation("[扫描枪] 系统可用串口: {Ports}",
                                    string.Join(", ", availablePorts));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[扫描枪] 无法枚举系统串口");
                            }
                        }

                        // 先断开（确保干净状态）
                        _logger.LogDebug("[{DeviceType}] 先断开旧连接...", deviceType);
                        await device.DisconnectAsync();
                        await Task.Delay(300, ct);

                        // 统一调用接口的 ConnectAsync
                        _logger.LogDebug("[{DeviceType}] 调用 ConnectAsync...", deviceType);
                        var result = await device.ConnectAsync(ct);

                        if (result)
                        {
                            UpdateConnectionState(deviceType, true);
                            _consecutiveFailures[deviceType] = 0;
                            _logger.LogInformation("✅ [{DeviceType}] 连接成功！", deviceType);
                            return;
                        }
                        else
                        {
                            _logger.LogWarning("[{DeviceType}] ConnectAsync 返回 false（尝试 {Attempt}/{Max}）",
                                deviceType, attempt, MAX_RECONNECT_ATTEMPTS);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("[{DeviceType}] 连接被取消", deviceType);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{DeviceType}] 连接异常 (尝试 {Attempt}/{Max}): {ErrorType} - {ErrorMessage}",
                            deviceType, attempt, MAX_RECONNECT_ATTEMPTS, ex.GetType().Name, ex.Message);
                    }

                    if (attempt < MAX_RECONNECT_ATTEMPTS && !ct.IsCancellationRequested)
                    {
                        int delay = Math.Min(
                            RECONNECT_BASE_DELAY_MS * (int)Math.Pow(2, attempt - 1),
                            RECONNECT_MAX_DELAY_MS);
                        _logger.LogDebug("[{DeviceType}] 等待 {Delay}ms 后重试...", deviceType, delay);
                        await Task.Delay(delay, ct);
                    }
                }

                UpdateConnectionState(deviceType, false);
                _consecutiveFailures[deviceType]++;
                _logger.LogError("❌ [{DeviceType}] 连接失败！已达最大重试次数 ({Max})", deviceType, MAX_RECONNECT_ATTEMPTS);
            }
            finally
            {
                deviceLock.Release();
                _logger.LogInformation("🔓 [{DeviceType}] 连接流程结束（释放锁）", deviceType);
            }
        }

        /// <summary>
        /// 更新指定设备的连接状态并触发事件
        /// </summary>
        private void UpdateConnectionState(string deviceType, bool connected)
        {
            switch (deviceType)
            {
                case "PLC":
                    _isPlcConnected = connected;
                    SetStatusText("PLC", connected ? "已连接" : "未连接");
                    RaisePlcStateChanged(connected);
                    break;
                case "DMM":
                    _isDmmConnected = connected;
                    SetStatusText("DMM", connected ? "已连接" : "未连接");
                    RaiseDmmStateChanged(connected);
                    break;
                case "Scanner":
                    _isScannerConnected = connected;
                    SetStatusText("Scanner", connected ? "已连接" : "未连接");
                    RaiseScannerStateChanged(connected);
                    _logger.LogInformation("📡 扫描枪状态更新: {Status}", connected ? "已连接 ✅" : "未连接 ❌");
                    break;
            }
            CheckAllDevicesReady();
        }

        #endregion

        #region 硬件事件订阅与转发

        /// <summary>
        /// 订阅底层硬件事件，统一管理状态变更和事件转发
        /// </summary>
        private void SubscribeToHardwareEvents()
        {
            // PLC连接状态变更（接口事件）
            _plcDevice.ConnectionStateChanged += (sender, connected) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _isPlcConnected = connected;
                    SetStatusText("PLC", connected ? "已连接" : "未连接");
                    RaisePlcStateChanged(connected);
                    CheckAllDevicesReady();
                });
            };

            // 万用表连接状态变更（接口事件）
            _dmmDevice.ConnectionStateChanged += (sender, connected) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _isDmmConnected = connected;
                    SetStatusText("DMM", connected ? "已连接" : "未连接");
                    RaiseDmmStateChanged(connected);
                    CheckAllDevicesReady();
                });
            };

            // ⭐ 扫描枪连接状态变更（接口事件）- 增强日志
            _scannerDevice.ConnectionStateChanged += (sender, connected) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _isScannerConnected = connected;
                    SetStatusText("Scanner", connected ? "已连接" : "未连接");
                    RaiseScannerStateChanged(connected);
                    CheckAllDevicesReady();

                    if (connected)
                    {
                        _consecutiveFailures["Scanner"] = 0;
                        _logger.LogInformation("✅ 扫描枪连接状态事件: 已连接");
                    }
                    else
                    {
                        _logger.LogWarning("❌ 扫描枪连接状态事件: 已断开");
                    }
                });
            };

            // 扫描枪条码转发（统一入口）
            _scannerBarcodeService.BarcodeParsed += (sender, e) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    BarcodeScanned?.Invoke(this, e);
                });
            };
        }

        #endregion

        #region 事件触发辅助方法

        private void RaisePlcStateChanged(bool connected)
        {
            PlcConnectionStateChanged?.Invoke(this,
                new DeviceConnectionStateChangedEventArgs("PLC", connected, _plcStatusText));
        }

        private void RaiseDmmStateChanged(bool connected)
        {
            DmmConnectionStateChanged?.Invoke(this,
                new DeviceConnectionStateChangedEventArgs("DMM", connected, _dmmStatusText));
        }

        private void RaiseScannerStateChanged(bool connected)
        {
            ScannerConnectionStateChanged?.Invoke(this,
                new DeviceConnectionStateChangedEventArgs("Scanner", connected, _scannerStatusText));
        }

        /// <summary>
        /// 设置设备状态文本（线程安全）
        /// </summary>
        private void SetStatusText(string deviceType, string text)
        {
            switch (deviceType)
            {
                case "PLC":
                    _plcStatusText = text;
                    break;
                case "DMM":
                    _dmmStatusText = text;
                    break;
                case "Scanner":
                    _scannerStatusText = text;
                    break;
            }
        }

        /// <summary>
        /// 检查所有设备是否全部就绪，状态变化时触发事件
        /// </summary>
        private void CheckAllDevicesReady()
        {
            bool allReady = AreAllDevicesReady;
            AllDevicesReadyChanged?.Invoke(this, allReady);
            _logger.LogDebug("设备就绪状态变更: AllReady={AllReady} (PLC:{Plc}, DMM:{Dmm}, Scanner:{Scanner})",
                allReady, _isPlcConnected, _isDmmConnected, _isScannerConnected);
        }

        #endregion

        #region 后台监控（热插拔检测 + 断线自动重连）

        /// <summary>
        /// 启动后台监控线程
        /// </summary>
        private void StartBackgroundMonitor()
        {
            if (_monitorCts != null) return;

            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorDevicesLoopAsync(_monitorCts.Token));
            _logger.LogInformation("后台设备监控已启动，检查间隔 {Interval}ms", MONITOR_INTERVAL_MS);
        }

        /// <summary>
        /// 停止后台监控线程
        /// </summary>
        private void StopBackgroundMonitor()
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;

            if (_monitorTask != null)
            {
                try { _monitorTask.Wait(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { _logger.LogDebug(ex, "等待监控线程结束时出现异常（可忽略）"); }
                _monitorTask = null;
            }

            _logger.LogInformation("后台设备监控已停止");
        }

        /// <summary>
        /// 后台监控主循环
        /// </summary>
        private async Task MonitorDevicesLoopAsync(CancellationToken ct)
        {
            _logger.LogInformation("后台监控线程已启动");

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(MONITOR_INTERVAL_MS, ct).ConfigureAwait(false);

                    if (ct.IsCancellationRequested) break;

                    await CheckAndReconnectDeviceAsync(_plcDevice, "PLC", ct).ConfigureAwait(false);
                    await CheckAndReconnectDeviceAsync(_dmmDevice, "DMM", ct).ConfigureAwait(false);
                    await CheckAndReconnectDeviceAsync(_scannerDevice, "Scanner", ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("后台监控被取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台监控异常");
            }

            _logger.LogInformation("后台监控线程已退出");
        }

        /// <summary>
        /// 检查单个设备，如果断开则尝试重连
        /// </summary>
        private async Task CheckAndReconnectDeviceAsync(
            ICommunicationDevice device, string deviceType, CancellationToken ct)
        {
            if (device.IsConnected)
            {
                if (_consecutiveFailures.GetValueOrDefault(deviceType, 0) > 0)
                {
                    _logger.LogDebug("后台监控: {DeviceType} 已恢复连接，重置失败计数", deviceType);
                    _consecutiveFailures[deviceType] = 0;
                }
                return;
            }

            int failureCount = _consecutiveFailures.GetValueOrDefault(deviceType, 0);
            int cooldownMs = Math.Min(
                INITIAL_RECONNECT_INTERVAL_MS + failureCount * BACKOFF_RECONNECT_INTERVAL_MS,
                MAX_RECONNECT_INTERVAL_MS);

            var now = DateTime.Now;
            var lastAttempt = _lastReconnectAttempt.GetValueOrDefault(deviceType, DateTime.MinValue);
            if ((now - lastAttempt).TotalMilliseconds < cooldownMs)
                return;

            _lastReconnectAttempt[deviceType] = now;

            try
            {
                _logger.LogDebug("后台监控: 尝试连接 {DeviceType} (失败次数:{Failures}, 冷却:{Cooldown}ms)...",
                    deviceType, failureCount, cooldownMs);
                SetStatusText(deviceType, "连接中...");

                var result = await device.ConnectAsync(ct).ConfigureAwait(false);

                if (result)
                {
                    UpdateConnectionState(deviceType, true);
                    _consecutiveFailures[deviceType] = 0;
                    _logger.LogInformation("后台监控: {DeviceType} 连接成功！", deviceType);

                    // ⭐ 扫描枪重连成功后通知 ScannerBarcodeService
                    if (deviceType == "Scanner")
                    {
                        await _scannerBarcodeService.InitializeAsync();
                    }
                }
                else
                {
                    UpdateConnectionState(deviceType, false);
                    _consecutiveFailures[deviceType] = failureCount + 1;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                UpdateConnectionState(deviceType, false);
                _consecutiveFailures[deviceType] = failureCount + 1;
                _logger.LogDebug(ex, "后台监控: {DeviceType} 连接异常", deviceType);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            StopBackgroundMonitor();
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _plcLock?.Dispose();
            _dmmLock?.Dispose();
            _scannerLock?.Dispose();
            _globalReconnectLock?.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}