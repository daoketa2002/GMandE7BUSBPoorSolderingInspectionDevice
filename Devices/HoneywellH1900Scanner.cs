using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
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
    /// 工作原理：监听串口，通过超时判定提取完整条码
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
        private const int READ_TIMEOUT_MS = 100;
        private const int WRITE_TIMEOUT_MS = 100;

        /// <summary>
        /// 条码完整判定超时（毫秒）
        /// 串口收到数据后，若此时间内无新数据到达，则认为条码已接收完整
        /// H1900扫描枪连续发送字符间隔通常 < 10ms，100ms足够覆盖且无感知延迟
        /// </summary>
        private const int BARCODE_COMPLETE_TIMEOUT_MS = 100;

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
        private CancellationTokenSource? _readLoopCts;
        private Task? _readLoopTask;
        private int _connectionVersion;

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

        #endregion

        #region 事件

        /// <summary>
        /// 条码扫描成功事件（IScannerDevice 接口实现）
        /// </summary>
        public event EventHandler<BarcodeReceivedEventArgs>? BarcodeReceived;

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
                var readLoopTask = Volatile.Read(ref _readLoopTask);
                return _isConnected
                    && serialPort?.IsOpen == true
                    && readLoopTask is { IsCompleted: false };
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

            var currentTask = Volatile.Read(ref _readLoopTask);
            if (IsConnected)
            {
                _logger.LogDebug("[扫码连接] 已存在健康连接，跳过重复连接: Port={Port}, Version={Version}",
                    portName, Volatile.Read(ref _connectionVersion));
                return true;
            }

            if (_serialPort != null || currentTask != null || _isConnected)
            {
                _logger.LogWarning(
                    "[扫码连接][异常] 现有连接不健康，开始清理旧连接: Port={Port}, ReadTaskCompleted={Completed}",
                    _portName,
                    currentTask?.IsCompleted);
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
                serialPort = new SerialPort(portName, baudRate, parity, _dataBits, stopBits)
                {
                    ReadTimeout = READ_TIMEOUT_MS,
                    WriteTimeout = WRITE_TIMEOUT_MS,
                    Encoding = Encoding.ASCII,
                    DtrEnable = true,
                    RtsEnable = handshake == Handshake.RequestToSend,
                    Handshake = handshake
                };

                // 读取统一由 RunReadLoop 负责，串口事件只保留错误通知。
                serialPort.ErrorReceived += OnErrorReceived;
                serialPort.Open();

                var readLoopCts = new CancellationTokenSource();
                Volatile.Write(ref _serialPort, serialPort);
                Volatile.Write(ref _readLoopCts, readLoopCts);
                // 先标记当前连接有效，再启动任务，避免任务刚启动就因状态快照为 false 退出。
                _isConnected = true;
                var readLoopTask = Task.Run(
                    () => RunReadLoopAsync(serialPort, version, readLoopCts.Token),
                    CancellationToken.None);
                Volatile.Write(ref _readLoopTask, readLoopTask);

                _connectionStableTime = DateTime.Now;
                _disconnectDetectedCount = 0;
                StartMonitoring();

                _logger.LogInformation(
                    "[扫码连接] 连接成功并启动读取任务, Port={Port}, Version={Version}, ReadTaskStarted=true, Source={Source}",
                    portName,
                    version,
                    reconnectSource);
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
                || Volatile.Read(ref _readLoopTask) != null)
            {
                await StopCurrentConnectionAsync(stopWatchdog: true).ConfigureAwait(false);
            }
            else
            {
                Interlocked.Increment(ref _connectionVersion);
                _isConnected = false;
                CloseSerialPort(serialPort);
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

        private void CloseSerialPort(SerialPort? serialPort)
        {
            if (serialPort == null)
                return;

            try
            {
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
        /// 停止当前读取任务和串口。调用方必须已经持有生命周期门禁。
        /// 先使版本失效并关闭串口，再等待读取任务退出，避免旧任务污染新连接。
        /// </summary>
        private async Task StopCurrentConnectionAsync(bool stopWatchdog)
        {
            var oldVersion = Interlocked.Increment(ref _connectionVersion);
            var oldCts = Interlocked.Exchange(ref _readLoopCts, null);
            var oldTask = Interlocked.Exchange(ref _readLoopTask, null);
            var oldPort = Interlocked.Exchange(ref _serialPort, null);

            _isConnected = false;
            oldCts?.Cancel();
            CloseSerialPort(oldPort);

            if (oldTask != null && !oldTask.IsCompleted && Task.CurrentId != oldTask.Id)
            {
                try
                {
                    await oldTask.ConfigureAwait(false);
                    _logger.LogDebug("[扫码连接] 旧读取任务已退出, OldVersion={Version}", oldVersion);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("[扫码连接] 旧读取任务已取消, OldVersion={Version}", oldVersion);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[扫码连接] 等待旧读取任务退出时出现异常, OldVersion={Version}", oldVersion);
                }
            }

            oldCts?.Dispose();
            _connectionStableTime = DateTime.MinValue;
            _disconnectDetectedCount = 0;

            if (stopWatchdog)
                StopMonitoring();
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
                var readLoopTask = Volatile.Read(ref _readLoopTask);
                if (readLoopTask == null || readLoopTask.IsCompleted)
                {
                    _logger.LogWarning("[扫码连接][异常] 串口读取任务已退出，交由重连流程恢复");
                    return true;
                }

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
                _logger.LogInformation("[扫码连接] 热插拔确认断开，读取任务已停止，继续等待端口恢复");
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
        /// 单一串口读取任务。
        /// 每次连接只创建一个任务和一组局部缓冲区，读取超时 100ms 即完成当前条码组包。
        /// </summary>
        private async Task RunReadLoopAsync(
            SerialPort serialPort,
            int connectionVersion,
            CancellationToken cancellationToken)
        {
            var readBuffer = new byte[256];
            var barcodeBuffer = new StringBuilder();
            var normalExit = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested
                    && IsCurrentConnection(serialPort, connectionVersion))
                {
                    try
                    {
                        var bytesRead = serialPort.Read(readBuffer, 0, readBuffer.Length);
                        if (bytesRead <= 0)
                            continue;

                        if (!IsCurrentConnection(serialPort, connectionVersion))
                        {
                            _logger.LogDebug("[扫码接收][忽略] 读取到旧连接数据, Version={Version}", connectionVersion);
                            break;
                        }

                        var data = serialPort.Encoding.GetString(readBuffer, 0, bytesRead);
                        barcodeBuffer.Append(data);
                        _logger.LogInformation(
                            "[扫码接收] Port={Port}, Version={Version}, BytesRead={BytesRead}, BufferLength={BufferLength}",
                            serialPort.PortName,
                            connectionVersion,
                            bytesRead,
                            barcodeBuffer.Length);
                        PublishRawDataSafely(data);
                    }
                    catch (TimeoutException)
                    {
                        if (barcodeBuffer.Length > 0)
                        {
                            CompleteBarcode(serialPort, connectionVersion, barcodeBuffer);
                            barcodeBuffer.Clear();
                        }
                    }
                    catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested
                        || !IsCurrentConnection(serialPort, connectionVersion))
                    {
                        normalExit = true;
                        break;
                    }
                    catch (IOException ex) when (cancellationToken.IsCancellationRequested
                        || !IsCurrentConnection(serialPort, connectionVersion))
                    {
                        normalExit = true;
                        _logger.LogDebug(ex, "[扫码接收] 读取任务因连接停止而退出, Version={Version}", connectionVersion);
                        break;
                    }
                    catch (InvalidOperationException ex) when (cancellationToken.IsCancellationRequested
                        || !IsCurrentConnection(serialPort, connectionVersion))
                    {
                        normalExit = true;
                        _logger.LogDebug(ex, "[扫码接收] 读取任务因串口关闭而退出, Version={Version}", connectionVersion);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[扫码接收] 读取任务未预期异常, Version={Version}", connectionVersion);
            }

            if (!normalExit
                && !cancellationToken.IsCancellationRequested
                && IsCurrentConnection(serialPort, connectionVersion))
            {
                HandleReadLoopUnexpectedExit(serialPort, connectionVersion);
            }

            _logger.LogDebug("[扫码接收] 读取任务已正常退出, Version={Version}", connectionVersion);
            await Task.CompletedTask;
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
            StringBuilder barcodeBuffer)
        {
            if (!IsCurrentConnection(serialPort, connectionVersion))
            {
                _logger.LogDebug("[扫码组包][忽略] 连接版本已失效, Version={Version}", connectionVersion);
                return;
            }

            var barcode = barcodeBuffer.ToString().Trim().TrimEnd('\r', '\n', '\t', ' ');
            if (string.IsNullOrWhiteSpace(barcode))
                return;

            _logger.LogInformation(
                "[扫码组包] Version={Version}, BarcodeLength={Length}, Barcode={Barcode}",
                connectionVersion,
                barcode.Length,
                barcode);
            PublishBarcodeReceivedSafely(new BarcodeReceivedEventArgs(barcode, barcode));
        }

        private void HandleReadLoopUnexpectedExit(
            SerialPort serialPort,
            int connectionVersion)
        {
            if (Interlocked.CompareExchange(
                    ref _connectionVersion,
                    connectionVersion + 1,
                    connectionVersion) != connectionVersion)
            {
                return;
            }

            _isConnected = false;
            _logger.LogWarning(
                "[扫码接收][异常] 读取任务意外退出，连接已失效: Port={Port}, Version={Version}",
                serialPort.PortName,
                connectionVersion);

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _serialPort, null, serialPort),
                    serialPort))
            {
                CloseSerialPort(serialPort);
            }

            PublishConnectionStateSafely(false);
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
