// ============================================================
// 文件: Services/DeviceConnectionManager.cs
// 描述: 全局设备连接管理器实现（重构版）
// 职责:
//   1. 应用启动时从配置文件读取参数，自动连接 PLC、万用表、扫描枪
//   2. 断线后自动重连（可配置重连策略）
//   3. 统一暴露设备连接状态，各 ViewModel 通过事件订阅获取
//   4. 统一转发扫描枪条码事件到当前活跃页面
// 改动:
//   - 依赖接口 ICommunicationDevice / IScannerDevice，与具体硬件解耦
//   - 连接前从 IDeviceSettingsService 读取配置并注入设备属性
//   - 统一使用 ConnectAsync / DisconnectAsync 生命周期
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 全局设备连接管理器
    /// 单例服务，随应用程序启动初始化，统一管理所有硬件设备
    /// 依赖抽象接口，与具体硬件型号完全解耦
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
        private readonly SemaphoreSlim _reconnectLock = new(1, 1);
        private bool _isDisposed;

        // 重连配置
        private const int RECONNECT_BASE_DELAY_MS = 2000;
        private const int RECONNECT_MAX_DELAY_MS = 30000;
        private const int MAX_RECONNECT_ATTEMPTS = 12;

        #endregion

        #region 属性

        public bool IsPlcConnected => _isPlcConnected;
        public bool IsDmmConnected => _isDmmConnected;
        public bool IsScannerConnected => _isScannerConnected;

        public bool AreAllDevicesReady =>
            _isPlcConnected && _isDmmConnected && _isScannerConnected;

        public string PlcStatusText => GetStatusText(_isPlcConnected);
        public string DmmStatusText => GetStatusText(_isDmmConnected);
        public string ScannerStatusText => GetStatusText(_isScannerConnected);

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
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="plcDevice">PLC设备</param>
        /// <param name="dmmDevice">万用表设备</param>
        /// <param name="scannerDevice">扫描枪设备（实现 IScannerDevice）</param>
        /// <param name="scannerBarcodeService">条码解析服务</param>
        /// <param name="settingsService">设备配置服务</param>
        public DeviceConnectionManager(
            ILogger<DeviceConnectionManager> logger,
            IPlcDevice plcDevice,           // ⭐ 唯一匹配 PlcCommunicationAdapter
            IMultimeterDevice dmmDevice,    // ⭐ 唯一匹配 GwInstekGDM9060Driver
            IScannerDevice scannerDevice,   // 唯一匹配 HoneywellH1900Scanner
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

            _logger.LogInformation("DeviceConnectionManager 初始化完成");
        }

        #endregion

        #region 启动与停止

        /// <summary>
        /// 启动设备连接管理器，自动连接所有设备
        /// 从配置文件读取设备参数，注入到设备驱动后进行连接
        /// </summary>
        public async Task StartAllAsync()
        {
            _logger.LogInformation("开始自动连接所有设备...");

            _reconnectCts = new CancellationTokenSource();

            // ⭐ 第一步：从配置文件加载设备参数
            var settings = _settingsService.LoadSettings();
            ApplyConfigurationToDevices(settings);

            // ⭐ 第二步：并行连接所有设备
            var plcTask = ConnectDeviceWithRetryAsync(
                _plcDevice, "PLC", _reconnectCts.Token);
            var dmmTask = ConnectDeviceWithRetryAsync(
                _dmmDevice, "DMM", _reconnectCts.Token);
            var scannerTask = ConnectDeviceWithRetryAsync(
                _scannerDevice, "Scanner", _reconnectCts.Token);

            await Task.WhenAll(plcTask, dmmTask, scannerTask);

            _logger.LogInformation("设备连接初始化完成 - PLC:{Plc}, DMM:{Dmm}, Scanner:{Scanner}",
                _isPlcConnected, _isDmmConnected, _isScannerConnected);
        }

        /// <summary>
        /// 停止所有设备连接
        /// </summary>
        public async Task StopAllAsync()
        {
            _logger.LogInformation("正在停止所有设备连接...");

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
        /// </summary>
        /// <param name="deviceType">设备类型："PLC" / "DMM" / "Scanner"</param>
        public async Task ReconnectDeviceAsync(string deviceType)
        {
            _logger.LogInformation("手动重连设备: {DeviceType}", deviceType);

            // 重新加载配置（可能用户在系统设定页修改了参数）
            var settings = _settingsService.LoadSettings();
            ApplyConfigurationToDevices(settings);

            var ct = _reconnectCts?.Token ?? CancellationToken.None;

            switch (deviceType)
            {
                case "PLC":
                    await ConnectDeviceWithRetryAsync(_plcDevice, "PLC", ct);
                    break;
                case "DMM":
                    await ConnectDeviceWithRetryAsync(_dmmDevice, "DMM", ct);
                    break;
                case "Scanner":
                    await ConnectDeviceWithRetryAsync(_scannerDevice, "Scanner", ct);
                    break;
                default:
                    _logger.LogWarning("未知设备类型: {DeviceType}", deviceType);
                    break;
            }
        }

        #endregion

        #region 配置注入（⭐核心新增方法）

        /// <summary>
        /// 从 DeviceSettings 读取配置并注入到各设备驱动属性中
        /// 实现配置驱动连接，与系统设定页联动
        /// </summary>
        /// <param name="settings">从 DeviceSettings.json 加载的设备配置</param>
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

            // ========== 注入扫描枪配置 ==========
            if (settings.ScannerSerialCommunication != null &&
                _scannerDevice is Devices.Scanner.HoneywellH1900Scanner scanner)
            {
                scanner.PortName = settings.ScannerSerialCommunication.SerialNumber;
                scanner.BaudRate = settings.ScannerSerialCommunication.BaudRate;
                _logger.LogDebug("已注入扫描枪配置: {Port}@{BaudRate}",
                    scanner.PortName, scanner.BaudRate);
            }

            // ========== 注入PLC配置 ==========
            // PLC 的配置注入方式取决于 TcpClientPLCMotionService 的具体实现
            // 如果有类似 Host/Port 属性，在此处注入
            if (settings.FP0HCommunication != null)
            {
                // 由于缺少 PLC 驱动源码，此处保留扩展点
                // 如果 TcpClientPLCMotionService 有 Host/Port 属性，在此注入
                _logger.LogDebug("已加载PLC配置: {Ip}:{Port}",
                    settings.FP0HCommunication.IpAddress,
                    settings.FP0HCommunication.Port);
            }
        }

        #endregion

        #region 统一设备连接（带重试）

        /// <summary>
        /// 统一设备连接方法（带指数退避重试）
        /// 替代原先三个独立的 ConnectXxxWithRetryAsync 方法
        /// </summary>
        /// <param name="device">目标设备（实现 ICommunicationDevice 接口）</param>
        /// <param name="deviceType">设备类型名称（用于日志）</param>
        /// <param name="ct">取消令牌</param>
        private async Task ConnectDeviceWithRetryAsync(
            ICommunicationDevice device, string deviceType, CancellationToken ct)
        {
            await _reconnectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (device.IsConnected)
                {
                    UpdateConnectionState(deviceType, true);
                    return;
                }

                int attempt = 0;
                while (!ct.IsCancellationRequested && attempt < MAX_RECONNECT_ATTEMPTS)
                {
                    attempt++;
                    try
                    {
                        _logger.LogInformation("{DeviceType}连接尝试 {Attempt}/{Max}",
                            deviceType, attempt, MAX_RECONNECT_ATTEMPTS);

                        // 先断开（确保干净状态）
                        await device.DisconnectAsync();

                        // 统一调用接口的 ConnectAsync
                        var result = await device.ConnectAsync(ct);

                        if (result)
                        {
                            UpdateConnectionState(deviceType, true);
                            _logger.LogInformation("{DeviceType}连接成功", deviceType);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "{DeviceType}连接失败 (尝试 {Attempt}/{Max})",
                            deviceType, attempt, MAX_RECONNECT_ATTEMPTS);
                    }

                    if (attempt < MAX_RECONNECT_ATTEMPTS && !ct.IsCancellationRequested)
                    {
                        int delay = Math.Min(
                            RECONNECT_BASE_DELAY_MS * (int)Math.Pow(2, attempt - 1),
                            RECONNECT_MAX_DELAY_MS);
                        _logger.LogDebug("等待 {Delay}ms 后重试{DeviceType}连接", delay, deviceType);
                        await Task.Delay(delay, ct);
                    }
                }

                UpdateConnectionState(deviceType, false);
                _logger.LogWarning("{DeviceType}连接失败，已达最大重试次数", deviceType);
            }
            finally
            {
                _reconnectLock.Release();
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
                    RaisePlcStateChanged(connected);
                    break;
                case "DMM":
                    _isDmmConnected = connected;
                    RaiseDmmStateChanged(connected);
                    break;
                case "Scanner":
                    _isScannerConnected = connected;
                    RaiseScannerStateChanged(connected);
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
            // ⭐ PLC连接状态变更（接口事件）
            _plcDevice.ConnectionStateChanged += (sender, connected) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    _isPlcConnected = connected;
                    RaisePlcStateChanged(connected);
                    CheckAllDevicesReady();
                });
            };

            // ⭐ 万用表连接状态变更（接口事件）
            _dmmDevice.ConnectionStateChanged += (sender, connected) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    _isDmmConnected = connected;
                    RaiseDmmStateChanged(connected);
                    CheckAllDevicesReady();
                });
            };

            // ⭐ 扫描枪连接状态变更（接口事件）
            _scannerDevice.ConnectionStateChanged += (sender, connected) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    _isScannerConnected = connected;
                    RaiseScannerStateChanged(connected);
                    CheckAllDevicesReady();
                });
            };

            // 扫描枪条码转发（统一入口）
            _scannerBarcodeService.BarcodeParsed += (sender, e) =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
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
                new DeviceConnectionStateChangedEventArgs("PLC", connected, GetStatusText(connected)));
        }

        private void RaiseDmmStateChanged(bool connected)
        {
            DmmConnectionStateChanged?.Invoke(this,
                new DeviceConnectionStateChangedEventArgs("DMM", connected, GetStatusText(connected)));
        }

        private void RaiseScannerStateChanged(bool connected)
        {
            ScannerConnectionStateChanged?.Invoke(this,
                new DeviceConnectionStateChangedEventArgs("Scanner", connected, GetStatusText(connected)));
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

        private static string GetStatusText(bool connected) => connected ? "已连接" : "断开";

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectLock?.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}