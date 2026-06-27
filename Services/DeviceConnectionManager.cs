// ============================================================
// 文件: Services/DeviceConnectionManager.cs
// 描述: 全局设备连接管理器 —— 门面，对外保持 IDeviceConnectionManager 契约
// 重构: 内部委托给 DeviceConfigurationApplier / DeviceConnectionExecutor /
//       DeviceConnectionStateStore / DeviceConnectionMonitor 四个小组件
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 全局设备连接管理器（门面）
    /// 单例服务，随应用程序启动初始化，统一管理所有硬件设备。
    ///
    /// 架构说明：
    /// - 本服务是硬件连接的唯一对外入口，所有 ViewModel 通过 IDeviceConnectionManager 与本服务交互
    /// - 内部委托给 DeviceConfigurationApplier（配置注入）、DeviceConnectionExecutor（连接执行）、
    ///   DeviceConnectionStateStore（状态存储）、DeviceConnectionMonitor（后台监控）
    /// - ScannerBarcodeService 仅负责条码解析，不参与连接管理
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

        // 重构拆分出的内部组件
        private readonly DeviceConfigurationApplier _configurationApplier;
        private readonly DeviceConnectionExecutor _connectionExecutor;
        private readonly DeviceConnectionStateStore _stateStore;
        private readonly DeviceConnectionMonitor _connectionMonitor;

        private CancellationTokenSource? _connectionCts;
        private bool _isDisposed;

        #endregion

        #region 属性（委托给 DeviceConnectionStateStore）

        public bool IsPlcConnected => _stateStore.IsPlcConnected;
        public bool IsDmmConnected => _stateStore.IsDmmConnected;
        public bool IsScannerConnected => _stateStore.IsScannerConnected;
        public bool AreAllDevicesReady => _stateStore.AreAllDevicesReady;
        public string PlcStatusText => _stateStore.PlcStatusText;
        public string DmmStatusText => _stateStore.DmmStatusText;
        public string ScannerStatusText => _stateStore.ScannerStatusText;

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
        /// 初始化设备连接管理器。
        /// 构造函数中只注入依赖并订阅硬件事件，不发起任何连接。
        /// 连接在 StartAllAsync() 中统一启动。
        /// </summary>
        public DeviceConnectionManager(
            ILogger<DeviceConnectionManager> logger,
            IPlcDevice plcDevice,
            IMultimeterDevice dmmDevice,
            IScannerDevice scannerDevice,
            IScannerBarcodeService scannerBarcodeService,
            IDeviceSettingsService settingsService,
            DeviceConfigurationApplier configurationApplier,
            DeviceConnectionExecutor connectionExecutor,
            DeviceConnectionStateStore stateStore,
            DeviceConnectionMonitor connectionMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
            _dmmDevice = dmmDevice ?? throw new ArgumentNullException(nameof(dmmDevice));
            _scannerDevice = scannerDevice ?? throw new ArgumentNullException(nameof(scannerDevice));
            _scannerBarcodeService = scannerBarcodeService ?? throw new ArgumentNullException(nameof(scannerBarcodeService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _configurationApplier = configurationApplier ?? throw new ArgumentNullException(nameof(configurationApplier));
            _connectionExecutor = connectionExecutor ?? throw new ArgumentNullException(nameof(connectionExecutor));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _connectionMonitor = connectionMonitor ?? throw new ArgumentNullException(nameof(connectionMonitor));

            // 订阅底层硬件事件，统一管理状态变更
            SubscribeToHardwareEvents();

            _logger.LogInformation("[设备连接] DeviceConnectionManager 初始化完成（硬件尚未连接，等待 StartAllAsync）");
        }

        #endregion

        #region 启动与停止

        /// <summary>
        /// 启动设备连接管理器，自动连接所有设备。
        ///
        /// 启动流程：
        /// 1. 从配置文件加载设备参数 → 注入到各硬件驱动
        /// 2. 并行连接 PLC、万用表、扫描枪
        /// 3. 扫描枪连接成功后初始化 ScannerBarcodeService
        /// 4. 启动后台监控
        /// </summary>
        public async Task StartAllAsync()
        {
            _logger.LogInformation("[设备连接] 开始自动连接所有设备");
            _connectionCts = new CancellationTokenSource();

            // 第一步：加载并注入设备配置
            var settings = _settingsService.LoadSettings();
            _configurationApplier.Apply(settings, _plcDevice, _dmmDevice, _scannerDevice);

            // 第二步：并行连接三台设备
            var ct = _connectionCts.Token;
            var plcTask = ConnectAndPublishAsync(_plcDevice, DeviceTypeNames.Plc, ct);
            var dmmTask = ConnectAndPublishAsync(_dmmDevice, DeviceTypeNames.Dmm, ct);
            var scannerTask = ConnectAndPublishAsync(_scannerDevice, DeviceTypeNames.Scanner, ct);

            await Task.WhenAll(plcTask, dmmTask, scannerTask).ConfigureAwait(false);

            _logger.LogInformation("[设备连接] 设备连接初始化完成 - PLC:{Plc}, DMM:{Dmm}, Scanner:{Scanner}",
                _stateStore.IsPlcConnected, _stateStore.IsDmmConnected, _stateStore.IsScannerConnected);

            // 第三步：扫描枪硬件就绪后初始化 ScannerBarcodeService
            if (_stateStore.IsScannerConnected)
            {
                await InitializeScannerBarcodeServiceAsync().ConfigureAwait(false);
            }

            // 第四步：启动后台监控
            _connectionMonitor.Start(CreateDeviceMap(), PublishStateChangeAsync, InitializeScannerBarcodeServiceAsync);

            _logger.LogInformation("[设备连接] 设备连接管理器启动完成");
        }

        /// <summary>
        /// 停止所有设备连接。
        /// </summary>
        public async Task StopAllAsync()
        {
            _logger.LogInformation("[设备连接] 正在停止所有设备连接");

            _connectionMonitor.Stop();
            _connectionCts?.Cancel();

            await Task.WhenAll(
                _connectionExecutor.DisconnectAsync(_plcDevice, DeviceTypeNames.Plc),
                _connectionExecutor.DisconnectAsync(_dmmDevice, DeviceTypeNames.Dmm),
                _connectionExecutor.DisconnectAsync(_scannerDevice, DeviceTypeNames.Scanner)
            ).ConfigureAwait(false);

            await PublishStateChangeAsync(DeviceTypeNames.Plc, false).ConfigureAwait(false);
            await PublishStateChangeAsync(DeviceTypeNames.Dmm, false).ConfigureAwait(false);
            await PublishStateChangeAsync(DeviceTypeNames.Scanner, false).ConfigureAwait(false);

            _connectionCts?.Dispose();
            _connectionCts = null;
            _logger.LogInformation("[设备连接] 所有设备连接已停止");
        }

        /// <summary>
        /// 手动重连指定设备。
        /// 重新加载配置 → 重新注入 → 断开 → 连接 → 发布状态。
        /// 扫描枪重连成功后重新初始化 ScannerBarcodeService。
        /// </summary>
        public async Task ReconnectDeviceAsync(string deviceType)
        {
            if (!DeviceTypeNames.IsKnown(deviceType))
            {
                _logger.LogWarning("[设备连接] 未知设备类型: {DeviceType}", deviceType);
                return;
            }

            _logger.LogWarning("[用户操作][设备连接][{DeviceType}] 用户手动重连设备", deviceType);

            // 重新加载并注入最新配置
            var settings = _settingsService.LoadSettings();
            _configurationApplier.Apply(settings, _plcDevice, _dmmDevice, _scannerDevice);

            _stateStore.SetConnecting(deviceType);
            var device = GetDevice(deviceType);
            await _connectionExecutor.DisconnectAsync(device, deviceType).ConfigureAwait(false);
            await ConnectAndPublishAsync(device, deviceType, _connectionCts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            // 扫描枪重连成功后重新初始化条码服务
            if (deviceType == DeviceTypeNames.Scanner && _stateStore.IsScannerConnected)
            {
                await InitializeScannerBarcodeServiceAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("[设备连接][{DeviceType}] 手动重连完成，结果: {Result}",
                deviceType, _stateStore.IsConnected(deviceType) ? "成功" : "失败");
        }

        /// <summary>
        /// 连接指定设备（已连接则跳过）。
        /// </summary>
        public async Task ConnectDeviceAsync(string deviceType)
        {
            if (!DeviceTypeNames.IsKnown(deviceType))
            {
                _logger.LogWarning("[设备连接] 未知设备类型: {DeviceType}", deviceType);
                return;
            }

            if (_stateStore.IsConnected(deviceType))
            {
                _logger.LogInformation("[设备连接][{DeviceType}] 已连接，跳过", deviceType);
                return;
            }

            _logger.LogInformation("[设备连接][{DeviceType}] 连接设备", deviceType);

            // 加载并注入最新配置
            var settings = _settingsService.LoadSettings();
            _configurationApplier.Apply(settings, _plcDevice, _dmmDevice, _scannerDevice);

            _stateStore.SetConnecting(deviceType);
            var device = GetDevice(deviceType);
            await ConnectAndPublishAsync(device, deviceType, _connectionCts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            // 扫描枪连接成功后初始化条码服务
            if (deviceType == DeviceTypeNames.Scanner && _stateStore.IsScannerConnected)
            {
                await InitializeScannerBarcodeServiceAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 断开指定设备。
        /// </summary>
        public async Task DisconnectDeviceAsync(string deviceType)
        {
            if (!DeviceTypeNames.IsKnown(deviceType))
            {
                _logger.LogWarning("[设备连接] 未知设备类型: {DeviceType}", deviceType);
                return;
            }

            _logger.LogInformation("[设备连接][{DeviceType}] 断开设备", deviceType);
            var device = GetDevice(deviceType);
            await _connectionExecutor.DisconnectAsync(device, deviceType).ConfigureAwait(false);
            await PublishStateChangeAsync(deviceType, false).ConfigureAwait(false);
        }

        #endregion

        #region 内部辅助方法

        /// <summary>
        /// 通过执行器连接设备，然后发布状态变更。
        /// </summary>
        private async Task ConnectAndPublishAsync(ICommunicationDevice device, string deviceType, CancellationToken ct)
        {
            var connected = await _connectionExecutor.ConnectWithRetryAsync(device, deviceType, ct).ConfigureAwait(false);
            await PublishStateChangeAsync(deviceType, connected).ConfigureAwait(false);
        }

        /// <summary>
        /// 更新状态仓库并发布到 UI 线程。
        /// </summary>
        private Task PublishStateChangeAsync(string deviceType, bool connected)
        {
            var args = _stateStore.UpdateConnectionState(deviceType, connected);
            PublishOnUiThread(deviceType, args);
            CheckAllDevicesReady();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 初始化扫描枪条码服务。
        /// </summary>
        private async Task InitializeScannerBarcodeServiceAsync()
        {
            try
            {
                await _scannerBarcodeService.InitializeAsync().ConfigureAwait(false);
                _logger.LogInformation("[设备连接][扫描枪] ScannerBarcodeService 初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[设备连接][扫描枪] ScannerBarcodeService 初始化失败");
            }
        }

        /// <summary>
        /// 根据设备类型字符串返回对应接口。
        /// </summary>
        private ICommunicationDevice GetDevice(string deviceType) => deviceType switch
        {
            DeviceTypeNames.Plc => _plcDevice,
            DeviceTypeNames.Dmm => _dmmDevice,
            DeviceTypeNames.Scanner => _scannerDevice,
            _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, "未知设备类型")
        };

        /// <summary>
        /// 构建设备类型到设备实例的字典，供监控器使用。
        /// </summary>
        private IReadOnlyDictionary<string, ICommunicationDevice> CreateDeviceMap() => new Dictionary<string, ICommunicationDevice>
        {
            [DeviceTypeNames.Plc] = _plcDevice,
            [DeviceTypeNames.Dmm] = _dmmDevice,
            [DeviceTypeNames.Scanner] = _scannerDevice
        };

        /// <summary>
        /// 在 UI 线程上触发对应设备的连接状态变更事件。
        /// </summary>
        private void PublishOnUiThread(string deviceType, DeviceConnectionStateChangedEventArgs args)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                switch (deviceType)
                {
                    case DeviceTypeNames.Plc:
                        PlcConnectionStateChanged?.Invoke(this, args);
                        break;
                    case DeviceTypeNames.Dmm:
                        DmmConnectionStateChanged?.Invoke(this, args);
                        break;
                    case DeviceTypeNames.Scanner:
                        ScannerConnectionStateChanged?.Invoke(this, args);
                        break;
                }
            });
        }

        /// <summary>
        /// 检查并发布全部设备就绪状态变更。
        /// </summary>
        private void CheckAllDevicesReady()
        {
            var allReady = _stateStore.AreAllDevicesReady;
            AllDevicesReadyChanged?.Invoke(this, allReady);
            _logger.LogDebug("设备就绪状态变更: AllReady={AllReady} (PLC:{Plc}, DMM:{Dmm}, Scanner:{Scanner})",
                allReady,
                _stateStore.IsPlcConnected,
                _stateStore.IsDmmConnected,
                _stateStore.IsScannerConnected);
        }

        #endregion

        #region 硬件事件订阅与转发

        /// <summary>
        /// 订阅底层硬件事件，统一管理状态变更和事件转发。
        /// 硬件自行触发的状态变化通过 PublishStateChangeAsync 统一处理。
        /// </summary>
        private void SubscribeToHardwareEvents()
        {
            // ⭐ PLC 连接状态变更 —— 通过 Dispatcher.BeginInvoke 排队到 UI 线程，
            // 避免驱动层在 ConnectAsync 内部过早触发 ConnectionStateChanged 同步污染 StateStore。
            _plcDevice.ConnectionStateChanged += (sender, connected) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    var args = _stateStore.UpdateConnectionState(DeviceTypeNames.Plc, connected);
                    PlcConnectionStateChanged?.Invoke(this, args);
                    CheckAllDevicesReady();
                });
            };

            // 万用表连接状态变更（同上 Dispatcher 包裹）
            _dmmDevice.ConnectionStateChanged += (sender, connected) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    var args = _stateStore.UpdateConnectionState(DeviceTypeNames.Dmm, connected);
                    DmmConnectionStateChanged?.Invoke(this, args);
                    CheckAllDevicesReady();
                });
            };

            // 扫描枪连接状态变更（同上 Dispatcher 包裹）
            _scannerDevice.ConnectionStateChanged += (sender, connected) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    var args = _stateStore.UpdateConnectionState(DeviceTypeNames.Scanner, connected);
                    ScannerConnectionStateChanged?.Invoke(this, args);
                    CheckAllDevicesReady();
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

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _connectionMonitor.Stop();
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();
            // 注意：以下组件由 DI 容器管理生命周期，不在此处 Dispose
            // DeviceConnectionExecutor（含内部 SemaphoreSlim）由容器在应用退出时清理
            // DeviceConnectionMonitor 同理由容器管理
            // DeviceConnectionStateStore / DeviceConfigurationApplier / DeviceConnectionRetryOptions 无托管资源

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
