// ============================================================
// 文件: Services/DeviceConnectionService.cs
// 描述: 全局设备连接管理器 —— 单服务，合并原 DeviceConnectionManager
//       及 4 个子组件（DeviceConfigurationApplier / DeviceConnectionExecutor /
//       DeviceConnectionStateStore / DeviceConnectionMonitor）的全部逻辑。
// 重构: 2026-06-30，设备数量固定为 3 台，不再拆分多个子组件。
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 全局设备连接管理器。
    /// 单例服务，随应用程序启动初始化，统一管理所有硬件设备。
    ///
    /// 职责：
    /// - 启动时从 IDeviceSettingsService 加载配置并注入各硬件驱动
    /// - 并行连接 PLC、万用表、扫描枪（带指数退避重试）
    /// - 对外统一发布设备连接状态（通过 IDeviceConnectionManager 接口）
    /// - 后台监控设备断线并自动重连
    /// - 扫描枪重连成功后自动初始化 ScannerBarcodeService
    ///
    /// 三台设备有独立互斥锁，避免一个设备重连卡住另外两台。
    /// </summary>
    public sealed class DeviceConnectionService : IDeviceConnectionManager, IDisposable
    {
        #region 字段

        private readonly ILogger<DeviceConnectionService> _logger;
        private readonly IPlcDevice _plcDevice;
        private readonly IMultimeterDevice _dmmDevice;
        private readonly IScannerDevice _scannerDevice;
        private readonly IScannerBarcodeService _scannerBarcodeService;
        private readonly IDeviceSettingsService _settingsService;

        // 三台设备独立互斥锁，允许并行连接
        private readonly SemaphoreSlim _plcLock = new(1, 1);
        private readonly SemaphoreSlim _dmmLock = new(1, 1);
        private readonly SemaphoreSlim _scannerLock = new(1, 1);

        // 独立无状态临时测试器
        private readonly IPlcConnectionTester _plcConnectionTester;
        private readonly IDmmConnectionTester _dmmConnectionTester;

        // 线程安全状态存储
        private readonly object _stateSyncRoot = new();
        private readonly Dictionary<string, DeviceState> _states = new()
        {
            [DeviceTypeNames.Plc] = new DeviceState(),
            [DeviceTypeNames.Dmm] = new DeviceState(),
            [DeviceTypeNames.Scanner] = new DeviceState()
        };
        private volatile bool _areAllDevicesReady;

        // 后台监控
        private CancellationTokenSource? _monitorCts;
        private Task? _monitorTask;

        // 连接流程
        private CancellationTokenSource? _connectionCts;

        private bool _isDisposed;

        #endregion

        #region 重试参数（原 DeviceConnectionRetryOptions，内联为常量）

        private const int ReconnectBaseDelayMs = 2000;
        private const int ReconnectMaxDelayMs = 8000;
        private const int MaxReconnectAttempts = 5;
        private const int MonitorIntervalMs = 2000;
        private const int CleanDisconnectDelayMs = 100;
        private const int HealthFailureThreshold = 2;

        // 分段重连退避：前 5 次 2s，6~12 次 5s，之后 10s
        private const int ReconnectBackoffFastMs = 2000;
        private const int ReconnectBackoffSlowMs = 5000;
        private const int ReconnectBackoffMaxMs = 10000;
        private const int ReconnectBackoffFastThreshold = 5;

        #endregion

        #region 属性（委托给状态存储）

        public bool IsPlcConnected => IsConnected(DeviceTypeNames.Plc);
        public bool IsDmmConnected => IsConnected(DeviceTypeNames.Dmm);
        public bool IsScannerConnected => IsConnected(DeviceTypeNames.Scanner);
        public bool AreAllDevicesReady => _areAllDevicesReady;

        public string PlcStatusText => GetStatusText(DeviceTypeNames.Plc);
        public string DmmStatusText => GetStatusText(DeviceTypeNames.Dmm);
        public string ScannerStatusText => GetStatusText(DeviceTypeNames.Scanner);
        public DeviceConnectionStatus PlcStatus => GetStatus(DeviceTypeNames.Plc);
        public DeviceConnectionStatus DmmStatus => GetStatus(DeviceTypeNames.Dmm);
        public DeviceConnectionStatus ScannerStatus => GetStatus(DeviceTypeNames.Scanner);

        #endregion

        #region 事件

        public event EventHandler<DeviceConnectionStateChangedEventArgs>? PlcConnectionStateChanged;
        public event EventHandler<DeviceConnectionStateChangedEventArgs>? DmmConnectionStateChanged;
        public event EventHandler<DeviceConnectionStateChangedEventArgs>? ScannerConnectionStateChanged;
        public event EventHandler<BarcodeParsedEventArgs>? BarcodeScanned;
        public event EventHandler<bool>? AllDevicesReadyChanged;

        #endregion

        #region 构造函数

        public DeviceConnectionService(
            ILogger<DeviceConnectionService> logger,
            IPlcDevice plcDevice,
            IMultimeterDevice dmmDevice,
            IScannerDevice scannerDevice,
            IScannerBarcodeService scannerBarcodeService,
            IDeviceSettingsService settingsService,
            IPlcConnectionTester plcConnectionTester,
            IDmmConnectionTester dmmConnectionTester)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
            _dmmDevice = dmmDevice ?? throw new ArgumentNullException(nameof(dmmDevice));
            _scannerDevice = scannerDevice ?? throw new ArgumentNullException(nameof(scannerDevice));
            _scannerBarcodeService = scannerBarcodeService ?? throw new ArgumentNullException(nameof(scannerBarcodeService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _plcConnectionTester = plcConnectionTester ?? throw new ArgumentNullException(nameof(plcConnectionTester));
            _dmmConnectionTester = dmmConnectionTester ?? throw new ArgumentNullException(nameof(dmmConnectionTester));

            SubscribeToHardwareEvents();

            _logger.LogInformation("[设备连接] DeviceConnectionService 初始化完成（硬件尚未连接，等待 StartAllAsync）");
        }

        #endregion

        #region 启动与停止

        public async Task StartAllAsync()
        {
            _logger.LogInformation("[设备连接] 开始自动连接所有设备");
            _connectionCts = new CancellationTokenSource();

            // 第一步：加载并注入设备配置
            var settings = _settingsService.LoadSettings();
            ApplyDeviceSettings(settings);

            // 第二步：并行连接三台设备
            StartMonitor();

            var ct = _connectionCts.Token;
            _ = Task.Run(() => RunInitialConnectionsAsync(ct), ct);

            await Task.CompletedTask.ConfigureAwait(false);

            _logger.LogInformation("[设备连接] 设备连接初始化完成 - PLC:{Plc}, DMM:{Dmm}, Scanner:{Scanner}",
                IsPlcConnected, IsDmmConnected, IsScannerConnected);

            // 第三步：扫描枪硬件就绪后初始化 ScannerBarcodeService
            // 首次扫描枪初始化由 RunInitialConnectionsAsync 或后台重连成功后处理。

            // 第四步：启动后台监控
            // 后台监控已在首次连接前启动。

            _logger.LogInformation("[设备连接] DeviceConnectionService 启动完成");
        }

        private async Task RunInitialConnectionsAsync(CancellationToken ct)
        {
            try
            {
                var plcTask = ConnectAndPublishAsync(_plcDevice, DeviceTypeNames.Plc, ct, maxAttempts: 1);
                var dmmTask = ConnectAndPublishAsync(_dmmDevice, DeviceTypeNames.Dmm, ct, maxAttempts: 1);
                var scannerTask = ConnectAndPublishAsync(_scannerDevice, DeviceTypeNames.Scanner, ct, maxAttempts: 1);

                await Task.WhenAll(plcTask, dmmTask, scannerTask).ConfigureAwait(false);

                _logger.LogInformation("[设备连接] 首次设备连接尝试完成 - PLC:{Plc}, DMM:{Dmm}, Scanner:{Scanner}",
                    IsPlcConnected, IsDmmConnected, IsScannerConnected);

                if (IsScannerConnected)
                {
                    await InitializeScannerBarcodeServiceAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("[设备连接] 首次设备连接尝试已取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[设备连接] 首次设备连接尝试异常");
            }
        }

        public async Task StopAllAsync()
        {
            _logger.LogInformation("[设备连接] 正在停止所有设备连接");

            StopMonitor();
            _connectionCts?.Cancel();

            await Task.WhenAll(
                DisconnectDeviceInternalAsync(_plcDevice, DeviceTypeNames.Plc),
                DisconnectDeviceInternalAsync(_dmmDevice, DeviceTypeNames.Dmm),
                DisconnectDeviceInternalAsync(_scannerDevice, DeviceTypeNames.Scanner)
            ).ConfigureAwait(false);

            await PublishStateChangeAsync(DeviceTypeNames.Plc, false).ConfigureAwait(false);
            await PublishStateChangeAsync(DeviceTypeNames.Dmm, false).ConfigureAwait(false);
            await PublishStateChangeAsync(DeviceTypeNames.Scanner, false).ConfigureAwait(false);

            _connectionCts?.Dispose();
            _connectionCts = null;
            _logger.LogInformation("[设备连接] 所有设备连接已停止");
        }

        public async Task ReconnectDeviceAsync(string deviceType)
        {
            if (!DeviceTypeNames.IsKnown(deviceType))
            {
                _logger.LogWarning("[设备连接] 未知设备类型: {DeviceType}", deviceType);
                return;
            }

            _logger.LogWarning("[用户操作][设备连接][{DeviceType}] 用户手动重连设备", deviceType);

            var device = GetDevice(deviceType);
            var settings = _settingsService.LoadSettings();
            ApplyDeviceSettings(settings);
            await ExecuteReconnectAsync(
                    deviceType,
                    device,
                    "手动重连",
                    _connectionCts?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }

        public async Task ConnectDeviceAsync(string deviceType)
        {
            if (!DeviceTypeNames.IsKnown(deviceType))
            {
                _logger.LogWarning("[设备连接] 未知设备类型: {DeviceType}", deviceType);
                return;
            }

            if (IsConnected(deviceType))
            {
                _logger.LogInformation("[设备连接][{DeviceType}] 已连接，跳过", deviceType);
                return;
            }

            _logger.LogInformation("[设备连接][{DeviceType}] 连接设备", deviceType);

            var settings = _settingsService.LoadSettings();
            ApplyDeviceSettings(settings);

            PublishConnecting(deviceType);
            var device = GetDevice(deviceType);
            await ConnectAndPublishAsync(device, deviceType, _connectionCts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            if (deviceType == DeviceTypeNames.Scanner && IsScannerConnected)
            {
                await InitializeScannerBarcodeServiceAsync().ConfigureAwait(false);
            }
        }

        public async Task DisconnectDeviceAsync(string deviceType)
        {
            if (!DeviceTypeNames.IsKnown(deviceType))
            {
                _logger.LogWarning("[设备连接] 未知设备类型: {DeviceType}", deviceType);
                return;
            }

            _logger.LogInformation("[设备连接][{DeviceType}] 断开设备", deviceType);
            var device = GetDevice(deviceType);
            await DisconnectDeviceInternalAsync(device, deviceType).ConfigureAwait(false);
            await PublishStateChangeAsync(deviceType, false).ConfigureAwait(false);
        }

        public async Task<DeviceReconnectSummary> ApplySettingsAndReconnectAsync(bool reconnectPlc, bool reconnectDmm, bool reconnectScanner)
        {
            var settings = _settingsService.LoadSettings();

            if (!reconnectPlc && !reconnectDmm && !reconnectScanner)
            {
                return DeviceReconnectSummary.NoChanges(
                    IsPlcConnected,
                    IsDmmConnected,
                    IsScannerConnected,
                    PlcStatusText,
                    DmmStatusText,
                    ScannerStatusText);
            }

            _logger.LogWarning("[设备连接][配置变更] 正在应用新配置并重连：PLC={Plc}, DMM={Dmm}, Scanner={Scanner}",
                reconnectPlc, reconnectDmm, reconnectScanner);

            var plcTask = reconnectPlc
                ? ApplyConfigAndReconnectDeviceAsync(DeviceTypeNames.Plc, settings)
                : Task.FromResult(DeviceReconnectResult.NotRequested(DeviceTypeNames.Plc, IsPlcConnected, PlcStatusText));
            var dmmTask = reconnectDmm
                ? ApplyConfigAndReconnectDeviceAsync(DeviceTypeNames.Dmm, settings)
                : Task.FromResult(DeviceReconnectResult.NotRequested(DeviceTypeNames.Dmm, IsDmmConnected, DmmStatusText));
            var scannerTask = reconnectScanner
                ? ApplyConfigAndReconnectDeviceAsync(DeviceTypeNames.Scanner, settings)
                : Task.FromResult(DeviceReconnectResult.NotRequested(DeviceTypeNames.Scanner, IsScannerConnected, ScannerStatusText));

            await Task.WhenAll(plcTask, dmmTask, scannerTask).ConfigureAwait(false);
            return new DeviceReconnectSummary(
                await plcTask.ConfigureAwait(false),
                await dmmTask.ConfigureAwait(false),
                await scannerTask.ConfigureAwait(false));
        }

        #endregion

        #region 设备配置注入（原 DeviceConfigurationApplier 逻辑）

        private void ApplyDeviceSettings(DeviceSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ApplyPlcConfig(settings);
            ApplyDmmConfig(settings);
            ApplyScannerConfig(settings);
        }

        private void ApplyPlcConfig(DeviceSettings settings)
        {
            if (settings.FP0HCommunication is null)
            {
                _logger.LogWarning("[设备连接][PLC] 配置注入跳过: 设置中无 FP0H 配置");
                return;
            }

            _plcDevice.ApplyConfig(settings.FP0HCommunication);
            _logger.LogDebug("[设备连接][PLC] 已注入配置: {Host}:{Port}",
                settings.FP0HCommunication.IpAddress, settings.FP0HCommunication.Port);
        }

        private void ApplyDmmConfig(DeviceSettings settings)
        {
            if (settings.GDM9060Communication is null || _dmmDevice is not GwInstekGDM9060Driver dmmDriver)
            {
                _logger.LogWarning("[设备连接][DMM] 万用表配置注入失败，Settings={HasSettings}, Device={DeviceType}",
                    settings.GDM9060Communication is not null, _dmmDevice.GetType().Name);
                return;
            }

            dmmDriver.Host = settings.GDM9060Communication.IpAddress;
            dmmDriver.Port = settings.GDM9060Communication.Port;
            dmmDriver.TimeoutMs = settings.GDM9060Communication.ReceiveTimeoutMs;
            _logger.LogDebug("[设备连接][DMM] 已注入配置: {Host}:{Port}", dmmDriver.Host, dmmDriver.Port);
        }

        private void ApplyScannerConfig(DeviceSettings settings)
        {
            if (settings.ScannerSerialCommunication is null || _scannerDevice is not HoneywellH1900Scanner scanner)
            {
                _logger.LogWarning("[设备连接][扫描枪] 配置注入失败，Settings={HasSettings}, Device={DeviceType}",
                    settings.ScannerSerialCommunication is not null, _scannerDevice.GetType().Name);
                return;
            }

            scanner.PortName = settings.ScannerSerialCommunication.SerialNumber;
            scanner.BaudRate = settings.ScannerSerialCommunication.BaudRate;
            scanner.Parity = settings.ScannerSerialCommunication.Parity;
            scanner.DataBits = settings.ScannerSerialCommunication.DataBits;
            scanner.StopBits = settings.ScannerSerialCommunication.StopBits;
            scanner.FlowControl = settings.ScannerSerialCommunication.FlowControl;
            _logger.LogInformation(
                "[设备连接][扫描枪] 已注入配置: Port={Port}, BaudRate={BaudRate}, Parity={Parity}, DataBits={DataBits}, StopBits={StopBits}, FlowControl={FlowControl}",
                scanner.PortName, scanner.BaudRate, scanner.Parity, scanner.DataBits, scanner.StopBits, scanner.FlowControl);
        }

        private async Task<DeviceReconnectResult> ApplyConfigAndReconnectDeviceAsync(string deviceType, DeviceSettings settings)
        {
            switch (deviceType)
            {
                case DeviceTypeNames.Plc:
                    ApplyPlcConfig(settings);
                    break;
                case DeviceTypeNames.Dmm:
                    ApplyDmmConfig(settings);
                    break;
                case DeviceTypeNames.Scanner:
                    ApplyScannerConfig(settings);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, "未知设备类型");
            }

            var device = GetDevice(deviceType);
            var connected = await ExecuteReconnectAsync(
                    deviceType,
                    device,
                    "配置重连",
                    _connectionCts?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);

            return new DeviceReconnectResult(
                deviceType,
                Requested: true,
                IsConnected: connected,
                StatusText: GetStatusText(deviceType));
        }

        #endregion

        #region 连接执行（原 DeviceConnectionExecutor 逻辑）

        private async Task<bool> ExecuteReconnectAsync(
            string deviceType,
            ICommunicationDevice device,
            string reason,
            CancellationToken ct)
        {
            SetReconnectInProgress(deviceType, true);
            try
            {
                PublishConnecting(deviceType);
                _logger.LogWarning("[{Reason}][{DeviceType}] 开始", reason, deviceType);

                await DisconnectDeviceInternalAsync(device, deviceType).ConfigureAwait(false);
                await ConnectAndPublishAsync(device, deviceType, ct, maxAttempts: 1).ConfigureAwait(false);

                if (deviceType == DeviceTypeNames.Scanner && IsScannerConnected)
                    await InitializeScannerBarcodeServiceAsync().ConfigureAwait(false);

                var connected = IsConnected(deviceType);
                _logger.LogInformation("[{Reason}][{DeviceType}] 结果: {Result}",
                    reason, deviceType, connected ? "成功" : "失败");
                return connected;
            }
            finally
            {
                SetReconnectInProgress(deviceType, false);
            }
        }

        /// <summary>
        /// 带指数退避重试连接指定设备。
        /// </summary>
        private async Task<bool> ConnectWithRetryAsync(
            ICommunicationDevice device,
            string deviceType,
            CancellationToken ct,
            int? maxAttempts = null)
        {
            var deviceLock = GetLock(deviceType);
            _logger.LogInformation("[设备连接][{DeviceType}] 开始连接流程", deviceType);

            await deviceLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var attemptsLimit = maxAttempts ?? MaxReconnectAttempts;

                if (device.IsConnected)
                {
                    _logger.LogInformation("[设备连接][{DeviceType}] 硬件报告已连接", deviceType);
                    return true;
                }

                for (var attempt = 1; attempt <= attemptsLimit && !ct.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        LogAttempt(device, deviceType, attempt, attemptsLimit);
                        await device.DisconnectAsync().ConfigureAwait(false);
                        await Task.Delay(CleanDisconnectDelayMs, ct).ConfigureAwait(false);

                        var connected = await device.ConnectAsync(ct).ConfigureAwait(false);
                        if (connected)
                        {
                            _logger.LogInformation("[设备连接][{DeviceType}] 连接成功", deviceType);
                            return true;
                        }

                        _logger.LogWarning("[设备连接][{DeviceType}] ConnectAsync 返回 false，尝试 {Attempt}/{MaxAttempts}",
                            deviceType, attempt, attemptsLimit);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("[设备连接][{DeviceType}] 连接被取消", deviceType);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[设备连接][{DeviceType}] 连接异常，尝试 {Attempt}/{MaxAttempts}",
                            deviceType, attempt, attemptsLimit);
                    }

                    if (attempt < attemptsLimit)
                    {
                        var delay = Math.Min(
                            ReconnectBaseDelayMs * (int)Math.Pow(2, attempt - 1),
                            ReconnectMaxDelayMs);
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                }

                _logger.LogWarning("[设备连接][{DeviceType}] 达到最大重试次数，连接失败", deviceType);
                return false;
            }
            finally
            {
                deviceLock.Release();
                _logger.LogInformation("[设备连接][{DeviceType}] 连接流程结束", deviceType);
            }
        }

        /// <summary>
        /// 安全断开指定设备，异常被捕获记录不向上抛出。
        /// </summary>
        private async Task DisconnectDeviceInternalAsync(ICommunicationDevice device, string deviceType)
        {
            var deviceLock = GetLock(deviceType);
            await deviceLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await device.DisconnectAsync().ConfigureAwait(false);
                _logger.LogInformation("[设备连接][{DeviceType}] 已断开连接", deviceType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[设备连接][{DeviceType}] 断开连接失败", deviceType);
            }
            finally
            {
                deviceLock.Release();
            }
        }

        private SemaphoreSlim GetLock(string deviceType) => deviceType switch
        {
            DeviceTypeNames.Plc => _plcLock,
            DeviceTypeNames.Dmm => _dmmLock,
            DeviceTypeNames.Scanner => _scannerLock,
            _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, "未知设备类型")
        };

        private void LogAttempt(ICommunicationDevice device, string deviceType, int attempt, int maxAttempts)
        {
            _logger.LogInformation("[设备连接][{DeviceType}] 连接尝试 {Attempt}/{MaxAttempts}",
                deviceType, attempt, maxAttempts);

            if (deviceType == DeviceTypeNames.Scanner && device is HoneywellH1900Scanner scanner)
            {
                _logger.LogInformation("[设备连接][扫描枪] 当前配置 - 端口:{Port}, 波特率:{BaudRate}",
                    scanner.PortName, scanner.BaudRate);
                try
                {
                    var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
                    _logger.LogInformation("[设备连接][扫描枪] 系统可用串口: {Ports}", string.Join(", ", availablePorts));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[设备连接][扫描枪] 无法枚举系统串口");
                }
            }
        }

        #endregion

        #region 状态存储（原 DeviceConnectionStateStore 逻辑）

        private bool IsConnected(string deviceType)
        {
            lock (_stateSyncRoot)
            {
                return _states[deviceType].IsConnected;
            }
        }

        private string GetStatusText(string deviceType)
        {
            lock (_stateSyncRoot)
            {
                return _states[deviceType].StatusText;
            }
        }

        private DeviceConnectionStatus GetStatus(string deviceType)
        {
            lock (_stateSyncRoot)
                return _states[deviceType].Status;
        }

        private bool IsReconnectInProgress(string deviceType)
        {
            lock (_stateSyncRoot)
                return _states[deviceType].IsReconnectInProgress;
        }

        private void SetReconnectInProgress(string deviceType, bool value)
        {
            lock (_stateSyncRoot)
                _states[deviceType].IsReconnectInProgress = value;
        }

        private DeviceConnectionStateChangedEventArgs? SetConnecting(string deviceType)
        {
            lock (_stateSyncRoot)
            {
                var state = _states[deviceType];
                if (state.Status == DeviceConnectionStatus.Connecting)
                    return null;
                state.IsConnected = false;
                state.Status = DeviceConnectionStatus.Connecting;
                state.StatusText = "连接中...";
                _areAllDevicesReady = _states[DeviceTypeNames.Plc].IsConnected
                    && _states[DeviceTypeNames.Dmm].IsConnected
                    && _states[DeviceTypeNames.Scanner].IsConnected;
                return new DeviceConnectionStateChangedEventArgs(deviceType, false, state.StatusText, state.Status);
            }
        }

        private void PublishConnecting(string deviceType)
        {
            var args = SetConnecting(deviceType);
            if (args is not null)
                PublishOnUiThread(deviceType, args);
            CheckAllDevicesReady();
        }

        /// <summary>
        /// 更新设备连接状态，原子计算全部设备就绪缓存。
        /// </summary>
        private DeviceConnectionStateChangedEventArgs? UpdateConnectionState(string deviceType, bool isConnected)
        {
            lock (_stateSyncRoot)
            {
                var state = _states[deviceType];
                var newStatus = isConnected ? DeviceConnectionStatus.Connected : DeviceConnectionStatus.Disconnected;
                if (state.Status == newStatus && state.IsConnected == isConnected)
                    return null;
                state.IsConnected = isConnected;
                state.Status = newStatus;
                state.StatusText = isConnected ? "已连接" : "未连接";

                if (isConnected)
                {
                    // 设备重新连接成功后，健康检查和自动重连历史都不再影响下一轮策略。
                    state.ConsecutiveHealthFailures = 0;
                    state.ConsecutiveReconnectFailures = 0;
                }

                // 同一锁内原子计算全部设备就绪状态，避免快照不一致
                _areAllDevicesReady = _states[DeviceTypeNames.Plc].IsConnected
                    && _states[DeviceTypeNames.Dmm].IsConnected
                    && _states[DeviceTypeNames.Scanner].IsConnected;

                return new DeviceConnectionStateChangedEventArgs(deviceType, isConnected, state.StatusText, state.Status);
            }
        }

        /// <summary>
        /// 检查设备重连冷却时间，使用分段退避：
        /// 前 5 次失败每 2s 允许一次，之后每 5s 允许一次，最大 10s。
        /// </summary>
        private bool ShouldAttemptReconnect(string deviceType, DateTime now)
        {
            lock (_stateSyncRoot)
            {
                var state = _states[deviceType];
                var cooldownMs = state.ConsecutiveReconnectFailures <= ReconnectBackoffFastThreshold
                    ? ReconnectBackoffFastMs
                    : Math.Min(
                        ReconnectBackoffSlowMs + (state.ConsecutiveReconnectFailures - ReconnectBackoffFastThreshold) * 1000,
                        ReconnectBackoffMaxMs);

                if ((now - state.LastReconnectAttempt).TotalMilliseconds < cooldownMs)
                    return false;

                state.LastReconnectAttempt = now;
                return true;
            }
        }

        private sealed class DeviceState
        {
            public bool IsConnected { get; set; }
            public DeviceConnectionStatus Status { get; set; } = DeviceConnectionStatus.Disconnected;
            public string StatusText { get; set; } = "未连接";
            public int ConsecutiveHealthFailures { get; set; }
            public int ConsecutiveReconnectFailures { get; set; }
            public DateTime LastReconnectAttempt { get; set; } = DateTime.MinValue;
            public bool IsReconnectInProgress { get; set; }
        }

        #endregion

        #region 后台监控（原 DeviceConnectionMonitor 逻辑）

        private void StartMonitor()
        {
            if (_monitorCts is not null)
            {
                _logger.LogDebug("[设备连接] 后台监控已在运行，忽略重复启动");
                return;
            }

            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));
            _logger.LogInformation("[设备连接] 后台设备监控已启动，检查间隔 {Interval}ms", MonitorIntervalMs);
        }

        private void StopMonitor()
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;

            if (_monitorTask is not null)
            {
                try
                {
                    _monitorTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[设备连接] 等待监控线程结束时出现异常");
                }
                _monitorTask = null;
            }

            _logger.LogInformation("[设备连接] 后台设备监控已停止");
        }

        private async Task MonitorLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(MonitorIntervalMs, ct).ConfigureAwait(false);

                    await Task.WhenAll(
                        MonitorDeviceSafelyAsync(DeviceTypeNames.Plc, _plcDevice, ct),
                        MonitorDeviceSafelyAsync(DeviceTypeNames.Dmm, _dmmDevice, ct),
                        MonitorDeviceSafelyAsync(DeviceTypeNames.Scanner, _scannerDevice, ct)
                    ).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _logger.LogDebug("[设备连接] 后台监控被取消");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[设备连接] 单轮监控异常，下一轮继续");
                }
            }
        }

        private async Task MonitorDeviceSafelyAsync(string deviceType, ICommunicationDevice device, CancellationToken ct)
        {
            try
            {
                if (IsReconnectInProgress(deviceType) || GetStatus(deviceType) == DeviceConnectionStatus.Connecting)
                    return;

                if (GetStatus(deviceType) == DeviceConnectionStatus.Connected)
                {
                    var health = await device.CheckHealthAsync(ct).ConfigureAwait(false);
                    if (health.Status == GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceHealthCheckStatus.Healthy)
                    {
                        ResetHealthFailures(deviceType);
                        return;
                    }

                    if (health.Status is GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceHealthCheckStatus.SkippedBusy
                        or GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceHealthCheckStatus.Disabled)
                    {
                        // SkippedBusy/Disabled 不计入失败次数
                        if (health.Status == GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceHealthCheckStatus.SkippedBusy)
                            _logger.LogDebug("[健康检查][{DeviceType}] 跳过（业务繁忙）", deviceType);
                        return;
                    }

                    var failures = IncrementHealthFailures(deviceType);
                    if (failures == 1)
                        _logger.LogWarning("[健康检查][{DeviceType}] 首次失败: {Message}", deviceType, health.Message);
                    else
                        _logger.LogDebug("[健康检查][{DeviceType}] 第 {Failures} 次失败/{Threshold}: {Message}",
                            deviceType, failures, HealthFailureThreshold, health.Message);
                    if (failures < HealthFailureThreshold)
                        return;

                    try
                    {
                        if (device.IsConnected)
                            await device.DisconnectAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[健康检查][{DeviceType}] 清理失效连接时异常", deviceType);
                    }

                    await PublishStateChangeAsync(deviceType, false).ConfigureAwait(false);
                    _logger.LogWarning("[健康检查][{DeviceType}] 已确认设备断线", deviceType);
                    return;
                }

                if (!ShouldAttemptReconnect(deviceType, DateTime.Now))
                    return;

                PublishConnecting(deviceType);
                _logger.LogInformation("[自动重连][{DeviceType}] 开始（重连失败次数={Failures}）", deviceType, GetReconnectFailureCount(deviceType));

                var connected = await ConnectWithRetryAsync(device, deviceType, ct, maxAttempts: 1).ConfigureAwait(false);
                await PublishStateChangeAsync(deviceType, connected).ConfigureAwait(false);

                if (connected)
                {
                    _logger.LogInformation("[自动重连][{DeviceType}] 成功", deviceType);
                    if (deviceType == DeviceTypeNames.Scanner)
                        await InitializeScannerBarcodeServiceAsync().ConfigureAwait(false);
                }
                else
                {
                    var failures = IncrementReconnectFailures(deviceType);
                    _logger.LogDebug("[自动重连][{DeviceType}] 失败，重连失败次数={Failures}，等待下一轮",
                        deviceType,
                        failures);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[设备连接][{DeviceType}] 本轮监控异常，已隔离", deviceType);
            }
        }

        private void ResetHealthFailures(string deviceType)
        {
            lock (_stateSyncRoot)
                _states[deviceType].ConsecutiveHealthFailures = 0;
        }

        private int IncrementHealthFailures(string deviceType)
        {
            lock (_stateSyncRoot)
                return ++_states[deviceType].ConsecutiveHealthFailures;
        }

        private int IncrementReconnectFailures(string deviceType)
        {
            lock (_stateSyncRoot)
                return ++_states[deviceType].ConsecutiveReconnectFailures;
        }

        private int GetReconnectFailureCount(string deviceType)
        {
            lock (_stateSyncRoot)
                return _states[deviceType].ConsecutiveReconnectFailures;
        }

        private async Task CheckAndReconnectAsync(string deviceType, ICommunicationDevice device, CancellationToken ct)
        {
            // 保留私有兼容入口，实际逻辑统一由单设备安全监控执行。
            await MonitorDeviceSafelyAsync(deviceType, device, ct).ConfigureAwait(false);
        }

        #endregion

        #region 内部辅助方法

        private async Task ConnectAndPublishAsync(
            ICommunicationDevice device,
            string deviceType,
            CancellationToken ct,
            int? maxAttempts = null)
        {
            var connected = await ConnectWithRetryAsync(device, deviceType, ct, maxAttempts).ConfigureAwait(false);
            await PublishStateChangeAsync(deviceType, connected).ConfigureAwait(false);
        }

        private Task PublishStateChangeAsync(string deviceType, bool connected)
        {
            var args = UpdateConnectionState(deviceType, connected);

            if (args is null)
            {
                _logger.LogDebug(
                    "[设备连接][{DeviceType}] 状态未变化，跳过重复状态事件",
                    deviceType);
                return Task.CompletedTask;
            }

            PublishOnUiThread(deviceType, args);
            CheckAllDevicesReady();
            return Task.CompletedTask;
        }

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

        private ICommunicationDevice GetDevice(string deviceType) => deviceType switch
        {
            DeviceTypeNames.Plc => _plcDevice,
            DeviceTypeNames.Dmm => _dmmDevice,
            DeviceTypeNames.Scanner => _scannerDevice,
            _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, "未知设备类型")
        };

        private void PublishOnUiThread(
            string deviceType,
            DeviceConnectionStateChangedEventArgs? args)
        {
            if (args is null)
            {
                _logger.LogError(
                    "[设备连接][{DeviceType}][防御] 阻止发布空连接状态事件参数",
                    deviceType);
                return;
            }

            void Publish()
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
                    default:
                        _logger.LogWarning(
                            "[设备连接] 未知设备类型，连接状态事件未发布: {DeviceType}",
                            deviceType);
                        break;
                }
            }

            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Publish();
                return;
            }

            dispatcher.BeginInvoke((Action)Publish);
        }

        private void CheckAllDevicesReady()
        {
            var allReady = _areAllDevicesReady;
            AllDevicesReadyChanged?.Invoke(this, allReady);
            _logger.LogDebug("设备就绪状态变更: AllReady={AllReady} (PLC:{Plc}, DMM:{Dmm}, Scanner:{Scanner})",
                allReady, IsPlcConnected, IsDmmConnected, IsScannerConnected);
        }

        #endregion

        #region 硬件事件订阅与转发

        private void HandleHardwareConnectionStateChanged(
            string deviceType,
            bool connected)
        {
            if (!connected
                && (IsReconnectInProgress(deviceType)
                    || GetStatus(deviceType) == DeviceConnectionStatus.Connecting))
            {
                _logger.LogDebug(
                    "[设备连接][{DeviceType}] 重连中收到驱动 false 事件，已忽略",
                    deviceType);
                return;
            }

            var args = UpdateConnectionState(deviceType, connected);

            if (args is null)
            {
                _logger.LogDebug(
                    "[设备连接][{DeviceType}] 驱动重复报告状态 Connected={Connected}，跳过事件发布",
                    deviceType,
                    connected);
                return;
            }

            PublishOnUiThread(deviceType, args);
            CheckAllDevicesReady();
        }

        private void SubscribeToHardwareEvents()
        {
            _plcDevice.ConnectionStateChanged += (_, connected) =>
                HandleHardwareConnectionStateChanged(
                    DeviceTypeNames.Plc,
                    connected);

            _dmmDevice.ConnectionStateChanged += (_, connected) =>
                HandleHardwareConnectionStateChanged(
                    DeviceTypeNames.Dmm,
                    connected);

            _scannerDevice.ConnectionStateChanged += (_, connected) =>
                HandleHardwareConnectionStateChanged(
                    DeviceTypeNames.Scanner,
                    connected);

            _scannerBarcodeService.BarcodeParsed += (sender, e) =>
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    BarcodeScanned?.Invoke(this, e);
                });
            };
        }

        #endregion

        #region 临时测试（复用现有设备锁）

        /// <summary>
        /// 测试输入 PLC 配置是否可达。
        /// 等待当前 PLC 生产连接/重连结束后，获取 _plcLock 执行独立临时测试。
        /// 锁内严禁调用 ConnectWithRetryAsync / DisconnectDeviceInternalAsync /
        /// ExecuteReconnectAsync / ApplyConfigAndReconnectDeviceAsync，否则造成死锁。
        /// 测试结束后释放锁，后台下一轮自动重连继续。
        /// </summary>
        public async Task<bool> TestPlcConfigurationAsync(
            FP0HCommunicationConfig config,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(config);

            await _plcLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _logger.LogInformation(
                    "[临时测试][PLC] 获得设备互斥锁，开始测试 Host={Host} Port={Port} UnitId={UnitId}",
                    config.IpAddress,
                    config.Port,
                    config.SlaveId);

                return await _plcConnectionTester.TestAsync(
                        config.IpAddress,
                        config.Port,
                        checked((byte)config.SlaveId),
                        8000,
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _plcLock.Release();
            }
        }

        /// <summary>
        /// 测试输入 DMM 配置是否可达。
        /// 等待当前 DMM 生产连接/重连结束后，获取 _dmmLock 执行独立临时测试。
        /// 锁内严禁调用 ConnectWithRetryAsync / DisconnectDeviceInternalAsync /
        /// ExecuteReconnectAsync / ApplyConfigAndReconnectDeviceAsync，否则造成死锁。
        /// 测试结束后释放锁，后台下一轮自动重连继续。
        /// </summary>
        public async Task<string?> TestDmmConfigurationAsync(
            GDM9060CommunicationConfig config,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(config);

            await _dmmLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _logger.LogInformation(
                    "[临时测试][DMM] 获得设备互斥锁，开始测试 Host={Host} Port={Port}",
                    config.IpAddress,
                    config.Port);

                return await _dmmConnectionTester.TestAsync(
                        config.IpAddress,
                        config.Port,
                        8000,
                        ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _dmmLock.Release();
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            StopMonitor();
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();

            _plcLock.Dispose();
            _dmmLock.Dispose();
            _scannerLock.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
