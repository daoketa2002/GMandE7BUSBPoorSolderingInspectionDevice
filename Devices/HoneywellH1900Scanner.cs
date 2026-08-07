using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner
{
    /// <summary>
    /// 霍尼韦尔 H1900 条码扫描枪驱动
    /// 通讯方式：USB虚拟串口 (CDC类)
    /// 工作原理：监听串口事件，通过独立静默计时器判定完整条码
    /// 
    /// ⚠️ 使用前请用Honeywell配置条码将扫描枪切换为 USB Serial 模式
    /// 
    /// 兼容性说明：
    /// - 不依赖条码结束符（\r\n），兼容扫描枪有/无结束符两种模式
    /// - 采用100ms数据停顿超时判定条码完整，响应速度无感知
    /// 
    /// 热插拔说明（⭐2026-06重构）：
    /// - 连接后5秒宽限期内不检测，避免端口稳定前的瞬态异常导致误判
    /// - 连续2次检测到断开才触发断开，避免单次异常误判
    /// - 检测到端口重新出现后自动重连，无需手动操作
    /// </summary>
    public class HoneywellH1900Scanner : IScannerDevice, IDisposable
    {
        #region 常量

        private const int DEFAULT_BAUD_RATE = 115200;
        private const int WRITE_TIMEOUT_MS = 100;

        /// <summary>
        /// 条码完整判定超时（毫秒）
        /// 串口收到数据后，若此时间内无新数据到达，则认为条码已接收完整
        /// H1900扫描枪连续发送字符间隔通常 < 10ms，100ms足够覆盖且无感知延迟
        /// </summary>
        private const int BARCODE_COMPLETE_TIMEOUT_MS = 100;

        /// <summary>单帧最大允许字节数，覆盖现有机种/序列号上限并保留日期段余量。</summary>
        public const int MaxBarcodeFrameBytes = 128;

        private const int RECOVERY_DRAIN_MIN_MS = 500;
        private const int RECOVERY_DRAIN_QUIET_MS = 500;
        private const int RECOVERY_DRAIN_MAX_MS = 2000;
        private const int REJECTED_FRAME_HEX_PREVIEW_BYTES = 32;

        #endregion

        #region 热插拔检测常量（⭐新增）

        /// <summary>
        /// 热插拔检测间隔（毫秒）
        /// PortWatchdog 每3秒检查一次COM口状态
        /// </summary>
        private const int PORT_WATCHDOG_INTERVAL_MS = 3000;

        /// <summary>
        /// 宽限期（毫秒）
        /// 连接成功后此时间内跳过 PortWatchdog 检测
        /// 防止端口刚打开时 Windows 串口枚举缓存延迟导致误判断开
        /// </summary>
        private const int GRACE_PERIOD_MS = 5000;

        /// <summary>
        /// 防抖确认次数
        /// 连续检测到断开达到此次数才真正触发断开逻辑
        /// 单次异常（如瞬时 IO 抖动）不会误触发
        /// </summary>
        private const int DISCONNECT_CONFIRM_COUNT = 2;

        #endregion

        #region 字段

        private readonly ILogger<HoneywellH1900Scanner> _logger;
        private SerialPort? _serialPort;
        private readonly SemaphoreSlim _connectionLifecycleLock = new(1, 1);
        private int _connectionVersion;

        // DataReceived 事件只负责把当前连接的数据读入缓存；所有组包状态统一受此锁保护。
        private readonly object _serialReceiveSync = new();
        private readonly object _frameSync = new();
        private SerialDataReceivedEventHandler? _serialDataReceivedHandler;
        private readonly System.Threading.Timer _frameEndTimer;
        private readonly System.Threading.Timer _recoveryDrainTimer;
        private readonly StringBuilder _frameBuffer = new();
        private readonly List<byte> _framePreviewBytes = new(REJECTED_FRAME_HEX_PREVIEW_BYTES);
        private int _frameByteCount;
        private bool _frameRejected;
        private int _frameConnectionVersion = -1;
        private long _lastFrameDataTimestamp;

        private string _portName = "COM9";
        private int _baudRate = DEFAULT_BAUD_RATE;
        private string _parity = "None";
        private int _dataBits = 8;
        private string _stopBits = "1";
        private string _flowControl = "None";

        private volatile bool _isConnected;
        private volatile bool _isDisposed;
        private volatile bool _isMonitoring;

        // ⭐ 热插拔检测：定时检查COM口是否存在
        private System.Timers.Timer? _portWatchdogTimer;

        // ⭐ 连接成功的时间戳，用于宽限期判断
        private DateTime _connectionStableTime = DateTime.MinValue;

        // ⭐ 防抖计数器：记录连续检测到断开的次数
        private int _disconnectDetectedCount = 0;

        private readonly object _recoveryDrainSync = new();
        private bool _recoveryDrainActive;
        private long _recoveryDrainStartedTimestamp;
        private long _recoveryDrainLastDataTimestamp;
        private int _recoveryDrainBytes;
        private int _recoveryDrainConnectionVersion = -1;
        private TaskCompletionSource<ScannerRecoveryDrainResult>? _recoveryDrainCompletion;

        #endregion

        #region 事件

        /// <summary>
        /// 条码扫描成功事件（IScannerDevice 接口实现）
        /// </summary>
        public event EventHandler<BarcodeReceivedEventArgs>? BarcodeReceived;

        /// <summary>异常超长扫码帧被完整丢弃时触发。</summary>
        public event EventHandler<ScannerFrameRejectedEventArgs>? BarcodeFrameRejected;

        /// <summary>
        /// 连接状态变更事件（ICommunicationDevice 接口实现）
        /// </summary>
        public event EventHandler<bool>? ConnectionStateChanged;

        /// <summary>
        /// 通信通知事件
        /// </summary>
        public event EventHandler<CommunicationNotification>? OnNotification;

        /// <summary>
        /// 原始数据接收事件（调试用）
        /// </summary>
        public event EventHandler<string>? RawDataReceived;

        #endregion

        #region 属性

        /// <summary>
        /// 串口号（如 COM9）
        /// 由 DeviceConnectionManager 从 DeviceSettings.json 读取并设置
        /// </summary>
        public string PortName
        {
            get => _portName;
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                    _portName = value;
            }
        }

        /// <summary>
        /// 波特率（默认115200）
        /// 由 DeviceConnectionManager 从 DeviceSettings.json 读取并设置
        /// </summary>
        public int BaudRate
        {
            get => _baudRate;
            set
            {
                if (value > 0)
                    _baudRate = value;
            }
        }

        /// <summary>
        /// 设备是否已连接（ICommunicationDevice 接口实现）
        /// </summary>
        public bool IsConnected
        {
            get
            {
                var serialPort = Volatile.Read(ref _serialPort);
                return _isConnected
                    && serialPort?.IsOpen == true;
            }
        }

        /// <summary>串口设备不发送未知心跳，仅检查当前串口是否仍可用。</summary>
        public Task<DeviceHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            if (!IsConnected)
                return Task.FromResult(DeviceHealthCheckResult.Unhealthy("扫描枪串口未打开"));

            try
            {
                _ = _serialPort!.BytesToRead;
                return Task.FromResult(DeviceHealthCheckResult.Healthy("扫描枪串口已打开"));
            }
            catch (IOException ex)
            {
                return Task.FromResult(DeviceHealthCheckResult.Unhealthy("扫描枪串口读取失败", ex));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(DeviceHealthCheckResult.Unhealthy("扫描枪串口已失效", ex));
            }
        }

        /// <summary>串口校验位，配置值会在打开串口时转换为 Parity 枚举。</summary>
        public string Parity
        {
            get => _parity;
            set => _parity = string.IsNullOrWhiteSpace(value) ? "None" : value.Trim();
        }

        /// <summary>串口数据位。</summary>
        public int DataBits
        {
            get => _dataBits;
            set => _dataBits = value;
        }

        /// <summary>串口停止位，配置值会在打开串口时转换为 StopBits 枚举。</summary>
        public string StopBits
        {
            get => _stopBits;
            set => _stopBits = string.IsNullOrWhiteSpace(value) ? "1" : value.Trim();
        }

        /// <summary>串口流控制，配置值会在打开串口时转换为 Handshake 枚举。</summary>
        public string FlowControl
        {
            get => _flowControl;
            set => _flowControl = string.IsNullOrWhiteSpace(value) ? "None" : value.Trim();
        }

        #endregion

        #region 构造函数

        public HoneywellH1900Scanner(ILogger<HoneywellH1900Scanner> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _frameEndTimer = new System.Threading.Timer(
                OnFrameEndTimerElapsed,
                null,
                Timeout.Infinite,
                Timeout.Infinite);
            _recoveryDrainTimer = new System.Threading.Timer(
                OnRecoveryDrainTimerElapsed,
                null,
                Timeout.Infinite,
                Timeout.Infinite);
        }

        /// <summary>
        /// 在深度恢复关闭串口前设置排空门禁，确保重新打开瞬间的历史数据不会进入业务。
        /// </summary>
        public void BeginRecoveryDrain()
        {
            lock (_recoveryDrainSync)
            {
                _recoveryDrainActive = true;
                _recoveryDrainStartedTimestamp = Stopwatch.GetTimestamp();
                _recoveryDrainLastDataTimestamp = _recoveryDrainStartedTimestamp;
                _recoveryDrainBytes = 0;
                _recoveryDrainConnectionVersion = -1;
                _recoveryDrainCompletion = new TaskCompletionSource<ScannerRecoveryDrainResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            StopRecoveryDrainTimer();
            _logger.LogWarning("[扫码恢复][排空] 已进入排空状态，后续串口字节只统计不发布");
        }

        /// <summary>等待独立计时器完成 500ms 静默排空，最长不超过 2 秒。</summary>
        public async Task<ScannerRecoveryDrainResult> WaitForRecoveryDrainAsync(
            CancellationToken cancellationToken = default)
        {
            Task<ScannerRecoveryDrainResult>? completion;
            lock (_recoveryDrainSync)
            {
                completion = _recoveryDrainCompletion?.Task;
            }

            if (completion is null)
                return new ScannerRecoveryDrainResult();

            return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>取消排空状态并释放排空等待者，供恢复失败和页面离开路径调用。</summary>
        public void CancelRecoveryDrain()
        {
            TaskCompletionSource<ScannerRecoveryDrainResult>? completion;
            ScannerRecoveryDrainResult result;
            lock (_recoveryDrainSync)
            {
                if (!_recoveryDrainActive && _recoveryDrainCompletion is null)
                    return;

                completion = _recoveryDrainCompletion;
                result = CreateRecoveryDrainResult(timedOut: true);
                _recoveryDrainActive = false;
                _recoveryDrainCompletion = null;
            }

            StopRecoveryDrainTimer();
            completion?.TrySetResult(result);
            _logger.LogWarning("[扫码恢复][排空] 已取消，累计字节={Bytes}", result.DrainedBytes);
        }

        #endregion

        #region 连接管理

        /// <summary>
        /// 异步连接设备（IScannerDevice 接口实现）
        /// 使用已注入的 PortName/BaudRate 属性值进行连接
        /// 由 DeviceConnectionManager 调用
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            await _connectionLifecycleLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ConnectCoreAsync(_portName, _baudRate, "设备连接服务", ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _connectionLifecycleLock.Release();
            }
        }

        /// <summary>统一执行连接，调用方必须已经持有生命周期门禁。</summary>
        private async Task<bool> ConnectCoreAsync(
            string portName,
            int baudRate,
            string reconnectSource,
            CancellationToken ct)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(HoneywellH1900Scanner));

            if (IsConnected)
            {
                _logger.LogDebug("[扫码连接] 已存在健康连接，跳过重复连接: Port={Port}, Version={Version}",
                    portName, Volatile.Read(ref _connectionVersion));
                return true;
            }

            if (_serialPort != null || _isConnected)
            {
                _logger.LogWarning(
                    "[扫码连接][异常] 现有连接不健康，开始清理旧连接: Port={Port}",
                    _portName);
                await StopCurrentConnectionAsync(stopWatchdog: true).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            _portName = portName;
            var version = Interlocked.Increment(ref _connectionVersion);
            _logger.LogInformation(
                "[扫码连接] 开始建立连接, Port={Port}, Version={Version}, Source={Source}",
                portName,
                version,
                reconnectSource);

            SerialPort? serialPort = null;
            SerialDataReceivedEventHandler? dataReceivedHandler = null;
            try
            {
                var availablePorts = SerialPort.GetPortNames();
                if (!availablePorts.Any(p => p.Equals(portName, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogError("[扫码连接] 端口不存在: Port={Port}, AvailablePorts={AvailablePorts}",
                        portName, string.Join(", ", availablePorts));
                    Notify(NotificationType.Error, $"端口 {portName} 不存在！请检查设备连接和端口配置");
                    return false;
                }

                var parity = ParseParity(_parity);
                var stopBits = ParseStopBits(_stopBits);
                var handshake = ParseFlowControl(_flowControl);
                const bool dtrEnabled = true;
                // H1900 的 USB 虚拟串口在无流控模式下仍需主机拉高 RTS，
                // 否则设备深度恢复后的第一次扫码可能只识读但不上传数据。
                var rtsEnabled = handshake is Handshake.None or Handshake.RequestToSend;
                serialPort = new SerialPort(portName, baudRate, parity, _dataBits, stopBits)
                {
                    WriteTimeout = WRITE_TIMEOUT_MS,
                    Encoding = Encoding.ASCII,
                    DtrEnable = dtrEnabled,
                    RtsEnable = rtsEnabled,
                    Handshake = handshake
                };

                // 先绑定当前连接专属的事件处理器，再打开串口，避免旧连接回调写入新连接缓存。
                dataReceivedHandler = (_, _) => OnSerialDataReceived(serialPort, version);
                serialPort.DataReceived += dataReceivedHandler;
                serialPort.ErrorReceived += OnErrorReceived;
                serialPort.Open();

                Volatile.Write(ref _serialPort, serialPort);
                Volatile.Write(ref _serialDataReceivedHandler, dataReceivedHandler);
                ResetFrameState();
                EnsureRecoveryDrainClockStarted(version);
                _isConnected = true;

                _connectionStableTime = DateTime.Now;
                _disconnectDetectedCount = 0;
                StartMonitoring();

                _logger.LogInformation(
                    "[扫码连接] 连接成功并启用事件驱动接收, Port={Port}, Version={Version}, DataReceivedAttached=true, Source={Source}, Handshake={Handshake}, DTR={Dtr}, RTS={Rts}",
                    portName,
                    version,
                    reconnectSource,
                    handshake,
                    dtrEnabled,
                    rtsEnabled);
                PublishConnectionStateSafely(true);
                Notify(NotificationType.Success, $"扫描枪已连接: {portName} @ {baudRate}bps");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "[扫码连接] 端口被占用: Port={Port}", portName);
                Notify(NotificationType.Error, $"端口 {portName} 被占用！请关闭其他串口工具");
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "[扫码连接] 端口 IO 异常: Port={Port}", portName);
                Notify(NotificationType.Error, $"端口 {portName} 通信异常: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[扫码连接] 打开串口失败: Port={Port}", portName);
                Notify(NotificationType.Error, $"扫描枪连接失败: {ex.Message}");
            }

            if (ReferenceEquals(Volatile.Read(ref _serialPort), serialPort)
                || _isConnected)
            {
                await StopCurrentConnectionAsync(stopWatchdog: true).ConfigureAwait(false);
            }
            else
            {
                Interlocked.Increment(ref _connectionVersion);
                _isConnected = false;
                CloseSerialPort(serialPort, dataReceivedHandler);
                Volatile.Write(ref _serialPort, null);
            }
            return false;
        }

        /// <summary>
        /// 断开连接（ICommunicationDevice 接口实现）
        /// </summary>
        public async Task DisconnectAsync()
        {
            await _connectionLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCurrentConnectionAsync(stopWatchdog: true).ConfigureAwait(false);
                PublishConnectionStateSafely(false);
                _logger.LogInformation("[扫码连接] 已断开连接");
            }
            finally
            {
                _connectionLifecycleLock.Release();
            }
        }

        /// <summary>
        /// 打开扫描枪串口并开始监听（同步版本，保留兼容）
        /// </summary>
        public bool Connect(string portName = "COM9", int baudRate = DEFAULT_BAUD_RATE)
        {
            _portName = portName;
            _baudRate = baudRate;
            return ConnectAsync().GetAwaiter().GetResult();
        }

        private static Parity ParseParity(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "none" => System.IO.Ports.Parity.None,
                "odd" => System.IO.Ports.Parity.Odd,
                "even" => System.IO.Ports.Parity.Even,
                _ => throw new ArgumentException($"不支持的扫描枪校验位：{value}", nameof(value))
            };
        }

        private static StopBits ParseStopBits(string value)
        {
            return value.Trim() switch
            {
                "1" => System.IO.Ports.StopBits.One,
                "1.5" => System.IO.Ports.StopBits.OnePointFive,
                "2" => System.IO.Ports.StopBits.Two,
                _ => throw new ArgumentException($"不支持的扫描枪停止位：{value}", nameof(value))
            };
        }

        private static Handshake ParseFlowControl(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "none" => Handshake.None,
                "xonxoff" => Handshake.XOnXOff,
                "requesttosend" => Handshake.RequestToSend,
                _ => throw new ArgumentException($"不支持的扫描枪流控制：{value}", nameof(value))
            };
        }

        private void CloseSerialPort(
            SerialPort? serialPort,
            SerialDataReceivedEventHandler? dataReceivedHandler = null)
        {
            if (serialPort == null)
                return;

            try
            {
                if (dataReceivedHandler != null)
                    serialPort.DataReceived -= dataReceivedHandler;
                serialPort.ErrorReceived -= OnErrorReceived;

                if (serialPort.IsOpen)
                {
                    serialPort.Close();
                }

                serialPort.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[扫码连接] 关闭串口时出现异常（可忽略）");
            }
        }

        /// <summary>
        /// 停止当前串口和事件接收。调用方必须已经持有生命周期门禁。
        /// 先使版本失效并停止计时器，再解绑并关闭串口，避免旧回调污染新连接。
        /// </summary>
        private Task StopCurrentConnectionAsync(bool stopWatchdog)
        {
            var oldVersion = Interlocked.Increment(ref _connectionVersion);
            var oldPort = Interlocked.Exchange(ref _serialPort, null);
            var oldDataReceivedHandler = Interlocked.Exchange(ref _serialDataReceivedHandler, null);

            _isConnected = false;
            StopFrameTimerAndClear();
            StopRecoveryDrainTimer();
            lock (_serialReceiveSync)
            {
                CloseSerialPort(oldPort, oldDataReceivedHandler);
            }
            _connectionStableTime = DateTime.MinValue;
            _disconnectDetectedCount = 0;

            if (stopWatchdog)
                StopMonitoring();

            _logger.LogDebug("[扫码连接] 当前事件驱动接收已停止, OldVersion={Version}", oldVersion);
            return Task.CompletedTask;
        }

        #endregion

        #region 数据监听

        private void StartMonitoring()
        {
            if (_isMonitoring) return;

            _isMonitoring = true;

            // ⭐ 启动串口热插拔检测
            StartPortWatchdog();

            _logger.LogDebug("扫描枪数据监听已启动");
        }

        private void StopMonitoring()
        {
            _isMonitoring = false;

            // ⭐ 停止热插拔检测
            StopPortWatchdog();

            _logger.LogDebug("扫描枪数据监听已停止");
        }

        /// <summary>
        /// ⭐ 启动串口热插拔检测定时器
        /// 每3秒检查COM口状态
        /// - 宽限期内跳过检测（防止刚连接时误判）
        /// - 断开检测：连续 DISCONNECT_CONFIRM_COUNT 次确认后才触发断开
        /// - 恢复检测：已断开状态下检测到端口重新出现，立即自动重连
        /// </summary>
        private void StartPortWatchdog()
        {
            if (_portWatchdogTimer != null) return;

            _portWatchdogTimer = new System.Timers.Timer(PORT_WATCHDOG_INTERVAL_MS)
            {
                AutoReset = true
            };
            _portWatchdogTimer.Elapsed += OnPortWatchdogTick;
            _portWatchdogTimer.Start();
            _logger.LogDebug("串口热插拔检测已启动，端口: {Port}, 宽限期: {GraceMs}ms",
                _portName, GRACE_PERIOD_MS);
        }

        /// <summary>
        /// ⭐ 停止串口热插拔检测
        /// </summary>
        private void StopPortWatchdog()
        {
            if (_portWatchdogTimer == null) return;

            _portWatchdogTimer.Stop();
            _portWatchdogTimer.Elapsed -= OnPortWatchdogTick;
            _portWatchdogTimer.Dispose();
            _portWatchdogTimer = null;
            _logger.LogDebug("串口热插拔检测已停止");
        }

        /// <summary>
        /// ⭐ 热插拔检测回调（重构版）
        /// 
        /// 三种检测模式：
        /// 
        /// 【模式1 - 宽限期保护】
        ///   连接成功后 GRACE_PERIOD_MS 内跳过所有检测
        ///   防止 Windows 串口枚举缓存延迟导致刚连接就被误判断开
        /// 
        /// 【模式2 - 断开检测（带防抖）】
        ///   已连接状态下检测端口是否消失
        ///   连续 DISCONNECT_CONFIRM_COUNT 次确认后才触发断开
        ///   避免单次瞬态异常（如 IO 抖动）导致误断开
        /// 
        /// 【模式3 - 恢复检测】
        ///   已断开状态下检测端口是否重新出现
        ///   端口重新出现 → 立即自动重连（不等后台监控冷却）
        ///   实现真正的热插拔即插即用
        /// </summary>
        private void OnPortWatchdogTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (_isDisposed) return;

            // ═══════════════════════════════════════════════
            // 模式1：宽限期保护
            // ═══════════════════════════════════════════════
            if (_isConnected && _connectionStableTime != DateTime.MinValue)
            {
                var elapsedSinceConnect = (DateTime.Now - _connectionStableTime).TotalMilliseconds;
                if (elapsedSinceConnect < GRACE_PERIOD_MS)
                {
                    _logger.LogDebug("热插拔检测：宽限期内，跳过检测 (已连接 {Elapsed:F0}ms / {GraceMs}ms)",
                        elapsedSinceConnect, GRACE_PERIOD_MS);
                    return;
                }
            }

            // ═══════════════════════════════════════════════
            // 模式2：断开检测（带防抖确认）
            // ═══════════════════════════════════════════════
            if (_isConnected)
            {
                bool isPortGone = CheckIfPortDisconnected();

                if (isPortGone)
                {
                    _disconnectDetectedCount++;
                    _logger.LogWarning("热插拔检测：端口可能已断开 ({Count}/{ConfirmCount})",
                        _disconnectDetectedCount, DISCONNECT_CONFIRM_COUNT);

                    // 连续确认达到阈值才触发断开
                    if (_disconnectDetectedCount >= DISCONNECT_CONFIRM_COUNT)
                    {
                        _logger.LogWarning("热插拔检测：连续 {Count} 次确认断开，触发断开逻辑",
                            _disconnectDetectedCount);
                        Notify(NotificationType.Warning, $"扫描枪已断开（端口 {_portName} 消失）");

                        _ = HandlePortLostByWatchdogAsync();
                    }
                }
                else
                {
                    // 端口正常，重置防抖计数器
                    if (_disconnectDetectedCount > 0)
                    {
                        _logger.LogDebug("热插拔检测：端口恢复正常，重置防抖计数器");
                        _disconnectDetectedCount = 0;
                    }
                }
            }
            // ═══════════════════════════════════════════════
            // 模式3：恢复检测（⭐核心新增功能）
            // ═══════════════════════════════════════════════
            else
            {
                // 已断开状态下，检测端口是否重新出现
                bool portReappeared = CheckIfPortReappeared();

                if (portReappeared)
                {
                    _logger.LogInformation(
                        "热插拔检测：端口 {PortName} 重新出现，等待 DeviceConnectionService 自动重连",
                        _portName);
                    Notify(NotificationType.Info, $"扫描枪端口 {_portName} 已重新出现，等待连接服务自动重连");
                }
            }
        }

        /// <summary>
        /// ⭐ 检测端口是否已断开
        /// 综合三种方法判断，任一方法确认断开即返回 true
        /// 
        /// 方法1：检查 _serialPort.IsOpen 标志
        /// 方法2：尝试访问 BytesToRead（拔出时通常抛 IOException）
        /// 方法3：检查系统端口列表（GetPortNames）
        /// </summary>
        /// <returns>true = 端口已断开，false = 端口正常</returns>
        private bool CheckIfPortDisconnected()
        {
            // 方法1：检查串口IsOpen标志
            try
            {
                var serialPort = Volatile.Read(ref _serialPort);
                if (serialPort == null || !serialPort.IsOpen)
                {
                    _logger.LogDebug("PortWatchdog 方法1：串口已关闭");
                    return true;
                }
            }
            catch
            {
                _logger.LogDebug("PortWatchdog 方法1：串口访问异常");
                return true;
            }

            // 方法2：尝试访问BytesToRead（拔出后通常抛IOException）
            try
            {
                _ = Volatile.Read(ref _serialPort)!.BytesToRead;
            }
            catch (IOException)
            {
                _logger.LogDebug("PortWatchdog 方法2：IO异常（设备已拔出）");
                return true;
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("PortWatchdog 方法2：串口已不可用");
                return true;
            }
            catch (Exception)
            {
                // 其他异常，保守起见不判定为断开
            }

            // 方法3：检查系统端口列表
            try
            {
                var portExists = SerialPort.GetPortNames()
                    .Any(p => p.Equals(_portName, StringComparison.OrdinalIgnoreCase));
                if (!portExists)
                {
                    _logger.LogDebug("PortWatchdog 方法3：端口 {PortName} 不在系统端口列表中", _portName);
                    return true;
                }
            }
            catch
            {
                // GetPortNames 异常时跳过，依赖其他方法
            }

            return false;
        }

        /// <summary>
        /// 热插拔检测确认端口消失后的断开处理。
        /// 这里不能调用 DisconnectAsync，因为 DisconnectAsync 会停止 PortWatchdog，
        /// 停止后就无法继续检测端口重新出现。
        /// </summary>
        private async Task HandlePortLostByWatchdogAsync()
        {
            var entered = false;
            try
            {
                await _connectionLifecycleLock.WaitAsync().ConfigureAwait(false);
                entered = true;
                if (!_isConnected)
                {
                    return;
                }

                await StopCurrentConnectionAsync(stopWatchdog: false).ConfigureAwait(false);
                PublishConnectionStateSafely(false);
                _logger.LogInformation("[扫码连接] 热插拔确认断开，事件驱动接收已停止，继续等待端口恢复");
            }
            catch (ObjectDisposedException) when (_isDisposed)
            {
                _logger.LogDebug("[扫码连接] Watchdog 退出时生命周期门禁已释放");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[扫码连接][异常] Watchdog 断开处理失败");
            }
            finally
            {
                if (entered)
                    _connectionLifecycleLock.Release();
            }
        }

        /// <summary>
        /// ⭐ 检测端口是否重新出现
        /// 仅在已断开状态下调用，检查系统端口列表中是否存在目标端口
        /// </summary>
        /// <returns>true = 端口已重新出现，false = 端口仍不可用</returns>
        private bool CheckIfPortReappeared()
        {
            try
            {
                var portExists = SerialPort.GetPortNames()
                    .Any(p => p.Equals(_portName, StringComparison.OrdinalIgnoreCase));
                return portExists;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "检测端口重新出现时异常");
                return false;
            }
        }

        /// <summary>
        /// 当前连接唯一的串口数据读取入口。
        /// DataReceived 只读取当前串口并把数据放入帧缓存，条码结束由独立静默计时器判定。
        /// </summary>
        private void OnSerialDataReceived(SerialPort serialPort, int connectionVersion)
        {
            if (_isDisposed || !IsCurrentConnection(serialPort, connectionVersion))
            {
                _logger.LogDebug("[扫码接收][忽略] DataReceived 来自失效连接, Version={Version}", connectionVersion);
                return;
            }

            string data;
            byte[] dataBytes;
            try
            {
                // 串口事件可能在关闭竞态中晚到，读取与关闭使用同一把锁，避免多个回调竞争输入缓冲区。
                lock (_serialReceiveSync)
                {
                    if (!IsCurrentConnection(serialPort, connectionVersion))
                        return;

                    data = serialPort.ReadExisting();
                    if (string.IsNullOrEmpty(data))
                        return;

                    dataBytes = serialPort.Encoding.GetBytes(data);
                }
            }
            catch (ObjectDisposedException) when (!IsCurrentConnection(serialPort, connectionVersion))
            {
                _logger.LogDebug("[扫码接收] 失效连接的串口已释放, Version={Version}", connectionVersion);
                return;
            }
            catch (InvalidOperationException) when (!IsCurrentConnection(serialPort, connectionVersion))
            {
                _logger.LogDebug("[扫码接收] 失效连接的串口已关闭, Version={Version}", connectionVersion);
                return;
            }
            catch (IOException ex) when (!IsCurrentConnection(serialPort, connectionVersion))
            {
                _logger.LogDebug(ex, "[扫码接收] 失效连接读取已停止, Version={Version}", connectionVersion);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[扫码接收][异常] DataReceived 读取失败, Port={Port}, Version={Version}",
                    serialPort.PortName,
                    connectionVersion);
                return;
            }

            if (dataBytes.Length == 0 || !IsCurrentConnection(serialPort, connectionVersion))
                return;

            if (HandleRecoveryDrainBytes(dataBytes.Length))
            {
                ScheduleRecoveryDrainTimer();
                return;
            }

            int bufferLength;
            lock (_frameSync)
            {
                if (!IsCurrentConnection(serialPort, connectionVersion))
                    return;

                if (_frameConnectionVersion != connectionVersion)
                    ResetFrameStateLocked(connectionVersion);

                _frameByteCount += dataBytes.Length;
                if (!_frameRejected)
                {
                    _frameBuffer.Append(data);
                    if (_frameByteCount > MaxBarcodeFrameBytes)
                    {
                        _frameRejected = true;
                        _logger.LogWarning(
                            "[扫码组包][拒绝] 当前帧超过最大长度，继续读取至 100ms 静默: Version={Version}, TotalBytes={TotalBytes}, MaxBytes={MaxBytes}",
                            connectionVersion,
                            _frameByteCount,
                            MaxBarcodeFrameBytes);
                    }
                }

                var previewBytes = Math.Min(
                    dataBytes.Length,
                    REJECTED_FRAME_HEX_PREVIEW_BYTES - _framePreviewBytes.Count);
                if (previewBytes > 0)
                    _framePreviewBytes.AddRange(dataBytes.AsSpan(0, previewBytes).ToArray());

                _lastFrameDataTimestamp = Stopwatch.GetTimestamp();
                bufferLength = _frameByteCount;
            }

            ScheduleFrameEndTimer(BARCODE_COMPLETE_TIMEOUT_MS);
            _logger.LogInformation(
                "[扫码接收] Port={Port}, Version={Version}, BytesRead={BytesRead}, BufferLength={BufferLength}",
                serialPort.PortName,
                connectionVersion,
                dataBytes.Length,
                bufferLength);
            PublishRawDataSafely(data);
        }

        private bool IsCurrentConnection(SerialPort serialPort, int connectionVersion)
        {
            try
            {
                return Volatile.Read(ref _serialPort) == serialPort
                    && Volatile.Read(ref _connectionVersion) == connectionVersion
                    && _isConnected
                    && serialPort.IsOpen;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void CompleteBarcode(
            SerialPort serialPort,
            int connectionVersion,
            string barcodeText)
        {
            if (!IsCurrentConnection(serialPort, connectionVersion))
            {
                _logger.LogDebug("[扫码组包][忽略] 连接版本已失效, Version={Version}", connectionVersion);
                return;
            }

            var barcode = barcodeText.Trim().TrimEnd('\r', '\n', '\t', ' ');
            if (string.IsNullOrWhiteSpace(barcode))
                return;

            _logger.LogInformation(
                "[扫码组包] Version={Version}, BarcodeLength={Length}, Barcode={Barcode}",
                connectionVersion,
                barcode.Length,
                barcode);
            PublishBarcodeReceivedSafely(new BarcodeReceivedEventArgs(barcode, barcode));
        }

        /// <summary>创建或重新安排100ms一次性静默计时器。</summary>
        private void ScheduleFrameEndTimer(int dueTimeMs)
        {
            try
            {
                _frameEndTimer.Change(Math.Max(1, dueTimeMs), Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("[扫码组包] 帧结束计时器已释放");
            }
        }

        /// <summary>静默计时器到期后，在锁内取出完整帧，锁外发布条码或拒绝事件。</summary>
        private void OnFrameEndTimerElapsed(object? state)
        {
            SerialPort? serialPort;
            string? barcodeText = null;
            byte[] previewBytes = Array.Empty<byte>();
            var connectionVersion = -1;
            var totalBytes = 0;
            var frameRejected = false;

            lock (_frameSync)
            {
                if (_frameByteCount <= 0)
                    return;

                connectionVersion = _frameConnectionVersion;
                serialPort = Volatile.Read(ref _serialPort);
                if (serialPort == null || !IsCurrentConnection(serialPort, connectionVersion))
                {
                    ResetFrameStateLocked();
                    return;
                }

                var quietMs = Stopwatch.GetElapsedTime(_lastFrameDataTimestamp).TotalMilliseconds;
                if (quietMs < BARCODE_COMPLETE_TIMEOUT_MS)
                {
                    ScheduleFrameEndTimer((int)Math.Ceiling(BARCODE_COMPLETE_TIMEOUT_MS - quietMs));
                    return;
                }

                totalBytes = _frameByteCount;
                frameRejected = _frameRejected;
                barcodeText = _frameBuffer.ToString();
                previewBytes = _framePreviewBytes.ToArray();
                ResetFrameStateLocked();
            }

            if (frameRejected && serialPort != null && IsCurrentConnection(serialPort, connectionVersion))
            {
                RejectBarcodeFrame(
                    totalBytes,
                    "扫码帧超过允许的最大字节数",
                    previewBytes);
            }
            else if (serialPort != null && barcodeText != null)
            {
                CompleteBarcode(serialPort, connectionVersion, barcodeText);
            }
        }

        /// <summary>停止帧结束计时器并清空当前连接的帧状态。</summary>
        private void StopFrameTimerAndClear()
        {
            try
            {
                _frameEndTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // 释放阶段计时器可能已经停止，继续清理帧状态即可。
            }

            lock (_frameSync)
            {
                ResetFrameStateLocked();
            }
        }

        private void ResetFrameState()
        {
            StopFrameTimerAndClear();
        }

        private void ResetFrameStateLocked(int connectionVersion = -1)
        {
            _frameBuffer.Clear();
            _framePreviewBytes.Clear();
            _frameByteCount = 0;
            _frameRejected = false;
            _frameConnectionVersion = connectionVersion;
            _lastFrameDataTimestamp = 0;
        }

        private bool HandleRecoveryDrainBytes(int bytesRead)
        {
            lock (_recoveryDrainSync)
            {
                if (!_recoveryDrainActive)
                    return false;

                _recoveryDrainBytes += bytesRead;
                _recoveryDrainLastDataTimestamp = Stopwatch.GetTimestamp();
                _logger.LogInformation(
                    "[扫码恢复][排空] 已清理串口字节: Bytes={Bytes}, TotalBytes={TotalBytes}",
                    bytesRead,
                    _recoveryDrainBytes);
                return true;
            }
        }

        private void EnsureRecoveryDrainClockStarted(int connectionVersion)
        {
            var shouldSchedule = false;
            lock (_recoveryDrainSync)
            {
                if (!_recoveryDrainActive || _recoveryDrainConnectionVersion == connectionVersion)
                    return;

                _recoveryDrainConnectionVersion = connectionVersion;
                _recoveryDrainStartedTimestamp = Stopwatch.GetTimestamp();
                _recoveryDrainLastDataTimestamp = _recoveryDrainStartedTimestamp;
                shouldSchedule = true;
                _logger.LogInformation(
                    "[扫码恢复][排空] 已从新串口连接开始计时: Version={Version}, MinQuietMs={MinQuietMs}, MaxMs={MaxMs}",
                    connectionVersion,
                    RECOVERY_DRAIN_QUIET_MS,
                    RECOVERY_DRAIN_MAX_MS);
            }

            if (shouldSchedule)
                ScheduleRecoveryDrainTimer();
        }

        private void OnRecoveryDrainTimerElapsed(object? state)
        {
            if (_isDisposed)
                return;

            if (!TryCompleteRecoveryDrain())
                ScheduleRecoveryDrainTimer();
        }

        /// <summary>按排空最早可能完成的时刻安排下一次一次性检查。</summary>
        private void ScheduleRecoveryDrainTimer()
        {
            int dueTimeMs;
            lock (_recoveryDrainSync)
            {
                if (!_recoveryDrainActive)
                    return;

                var now = Stopwatch.GetTimestamp();
                var elapsedMs = Stopwatch.GetElapsedTime(_recoveryDrainStartedTimestamp, now).TotalMilliseconds;
                var quietMs = Stopwatch.GetElapsedTime(_recoveryDrainLastDataTimestamp, now).TotalMilliseconds;
                var untilMin = Math.Max(0, RECOVERY_DRAIN_MIN_MS - elapsedMs);
                var untilQuiet = Math.Max(0, RECOVERY_DRAIN_QUIET_MS - quietMs);
                var untilMax = Math.Max(0, RECOVERY_DRAIN_MAX_MS - elapsedMs);
                dueTimeMs = (int)Math.Max(
                    1,
                    Math.Min(untilMax <= 0 ? 1 : untilMax, Math.Max(untilMin, untilQuiet)));
            }

            try
            {
                _recoveryDrainTimer.Change(dueTimeMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                _logger.LogDebug("[扫码恢复][排空] 排空计时器已释放");
            }
        }

        private void StopRecoveryDrainTimer()
        {
            try
            {
                _recoveryDrainTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // 释放阶段计时器可能已经停止。
            }
        }

        private bool TryCompleteRecoveryDrain()
        {
            TaskCompletionSource<ScannerRecoveryDrainResult>? completion = null;
            ScannerRecoveryDrainResult? result = null;

            lock (_recoveryDrainSync)
            {
                if (!_recoveryDrainActive)
                    return true;

                var now = Stopwatch.GetTimestamp();
                var elapsedMs = Stopwatch.GetElapsedTime(_recoveryDrainStartedTimestamp, now).TotalMilliseconds;
                var quietMs = Stopwatch.GetElapsedTime(_recoveryDrainLastDataTimestamp, now).TotalMilliseconds;
                var timedOut = elapsedMs >= RECOVERY_DRAIN_MAX_MS;
                if (!timedOut
                    && (elapsedMs < RECOVERY_DRAIN_MIN_MS || quietMs < RECOVERY_DRAIN_QUIET_MS))
                {
                    return false;
                }

                result = CreateRecoveryDrainResult(timedOut);
                completion = _recoveryDrainCompletion;
                _recoveryDrainActive = false;
                _recoveryDrainCompletion = null;
            }

            StopRecoveryDrainTimer();
            completion?.TrySetResult(result!);
            _logger.LogInformation(
                "[扫码恢复][排空] 排空完成: Bytes={Bytes}, ElapsedMs={ElapsedMs}, TimedOut={TimedOut}",
                result!.DrainedBytes,
                result.ElapsedMs,
                result.TimedOut);
            return true;
        }

        private ScannerRecoveryDrainResult CreateRecoveryDrainResult(bool timedOut)
        {
            var elapsedMs = _recoveryDrainStartedTimestamp == 0
                ? 0
                : (int)Math.Min(
                    Stopwatch.GetElapsedTime(_recoveryDrainStartedTimestamp).TotalMilliseconds,
                    int.MaxValue);
            return new ScannerRecoveryDrainResult
            {
                DrainedBytes = _recoveryDrainBytes,
                ElapsedMs = elapsedMs,
                TimedOut = timedOut
            };
        }

        private void RejectBarcodeFrame(
            int totalBytes,
            string reason,
            IReadOnlyCollection<byte> previewBytes)
        {
            var hexPreview = Convert.ToHexString(previewBytes.ToArray());
            var args = new ScannerFrameRejectedEventArgs(totalBytes, reason, hexPreview);
            _logger.LogWarning(
                "[扫码组包][拒绝] 超长帧已丢弃: TotalBytes={TotalBytes}, Reason={Reason}, HexPreview={HexPreview}",
                totalBytes,
                reason,
                hexPreview);
            PublishBarcodeFrameRejectedSafely(args);
        }

        private void OnErrorReceived(object sender, System.IO.Ports.SerialErrorReceivedEventArgs e)
        {
            _logger.LogWarning("扫描枪串口错误: {ErrorType}", e.EventType);

            // ⭐ 严重错误只记录日志，不立即触发断开（交给 PortWatchdog 统一处理）
            if (e.EventType == SerialError.TXFull ||
                e.EventType == SerialError.RXOver ||
                e.EventType == SerialError.Frame ||
                e.EventType == SerialError.Overrun)
            {
                _logger.LogWarning("扫描枪串口严重错误: {ErrorType}，将交由 PortWatchdog 确认", e.EventType);
                Notify(NotificationType.Warning, $"串口错误: {e.EventType}，正在监控中...");
            }
            else
            {
                Notify(NotificationType.Warning, $"串口错误: {e.EventType}");
            }
        }

        #endregion

        #region 主动触发扫描（如果扫描枪支持）

        /// <summary>
        /// 发送触发扫描命令（部分Honeywell型号支持）
        /// </summary>
        public async Task<bool> TriggerScanAsync()
        {
            var serialPort = Volatile.Read(ref _serialPort);
            if (!IsConnected || serialPort == null)
            {
                _logger.LogWarning("扫描枪未连接，无法触发扫描");
                return false;
            }

            try
            {
                // Honeywell 触发扫描命令：SYN T CR
                byte[] triggerCommand = { 0x16, 0x54, 0x0D };
                serialPort.Write(triggerCommand, 0, triggerCommand.Length);

                _logger.LogDebug("已发送触发扫描命令");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送触发扫描命令失败");
                return false;
            }
        }

        #endregion

        #region 扫描枪配置（通过发送配置条码命令）

        /// <summary>
        /// 发送配置命令到扫描枪
        /// 使用 Honeywell 的 EZConfig 语法
        /// </summary>
        public async Task<bool> SendConfigurationAsync(string configCommand)
        {
            var serialPort = Volatile.Read(ref _serialPort);
            if (!IsConnected || serialPort == null)
            {
                _logger.LogWarning("扫描枪未连接");
                return false;
            }

            try
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(configCommand + "\r\n");
                serialPort.Write(cmdBytes, 0, cmdBytes.Length);

                _logger.LogInformation("已发送配置命令: {Command}", configCommand);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送配置命令失败");
                return false;
            }
        }

        #endregion

        #region 通知辅助

        private void Notify(NotificationType type, string message)
        {
            var handlers = OnNotification;
            if (handlers == null)
                return;

            var args = new CommunicationNotification(type, message, "H1900");
            foreach (EventHandler<CommunicationNotification> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[扫码连接][异常] 通知订阅者执行失败: Type={Type}", type);
                }
            }
        }

        private void PublishRawDataSafely(string data)
        {
            var handlers = RawDataReceived;
            if (handlers == null)
                return;

            foreach (EventHandler<string> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, data);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[扫码接收][异常] RawDataReceived 订阅者执行失败");
                }
            }
        }

        private void PublishBarcodeReceivedSafely(BarcodeReceivedEventArgs args)
        {
            var handlers = BarcodeReceived;
            if (handlers == null)
            {
                _logger.LogDebug("[扫码组包] 当前无条码订阅者");
                return;
            }

            foreach (EventHandler<BarcodeReceivedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[扫码接收][异常] BarcodeReceived 订阅者执行失败");
                }
            }
        }

        private void PublishBarcodeFrameRejectedSafely(ScannerFrameRejectedEventArgs args)
        {
            var handlers = BarcodeFrameRejected;
            if (handlers == null)
                return;

            foreach (EventHandler<ScannerFrameRejectedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[扫码组包][异常] BarcodeFrameRejected 订阅者执行失败");
                }
            }
        }

        private void PublishConnectionStateSafely(bool isConnected)
        {
            var handlers = ConnectionStateChanged;
            if (handlers == null)
                return;

            foreach (EventHandler<bool> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, isConnected);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[扫码连接][异常] ConnectionStateChanged 订阅者执行失败: Connected={Connected}",
                        isConnected);
                }
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;

            _connectionLifecycleLock.Wait();
            try
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                StopCurrentConnectionAsync(stopWatchdog: true).GetAwaiter().GetResult();
                CancelRecoveryDrain();
                _frameEndTimer.Dispose();
                _recoveryDrainTimer.Dispose();
                PublishConnectionStateSafely(false);
            }
            finally
            {
                _connectionLifecycleLock.Release();
                _connectionLifecycleLock.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
