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
        private const int DATA_BITS = 8;
        private const Parity PARITY = Parity.None;
        private const StopBits STOP_BITS = StopBits.One;
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
        private CancellationTokenSource? _readLoopCts;
        private readonly StringBuilder _dataBuffer = new();
        private readonly object _lockObject = new();

        /// <summary>
        /// 条码完整超时计时器
        /// 原理：每次收到串口数据时重置，超时后认为一条完整条码已接收
        /// 不依赖 \r\n 结束符，兼容扫描枪的各种配置模式
        /// </summary>
        private System.Timers.Timer? _barcodeCompleteTimer;

        private string _portName = "COM9";
        private int _baudRate = DEFAULT_BAUD_RATE;

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
        public bool IsConnected => _isConnected && _serialPort?.IsOpen == true;

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
            return await Task.Run(() => ConnectInternal(_portName, _baudRate), ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 内部连接实现
        /// ⭐ 修复：不再信任缓存的 _isConnected，改为检查实际串口状态
        /// </summary>
        private bool ConnectInternal(string portName, int baudRate)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(HoneywellH1900Scanner));

            lock (_lockObject)
            {
                // ⭐ 修复：检查实际串口状态，不信任缓存的 _isConnected
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _logger.LogInformation("扫描枪串口已打开（{PortName}），跳过重复连接", _serialPort.PortName);
                    _isConnected = true;
                    return true;
                }

                // ⭐ 如果 _isConnected 为 true 但串口实际已关闭，重置状态
                if (_isConnected && (_serialPort == null || !_serialPort.IsOpen))
                {
                    _logger.LogWarning("⚠️ 检测到状态不一致：_isConnected=true 但串口未打开，强制重置状态");
                    _isConnected = false;
                    CloseSerialPort();
                }

                _portName = portName;

                try
                {
                    // ⭐ 检查端口是否存在于系统中
                    var availablePorts = SerialPort.GetPortNames();
                    if (!availablePorts.Any(p => p.Equals(portName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogError("❌ 端口 {PortName} 不存在于系统中！可用端口: {AvailablePorts}",
                            portName, string.Join(", ", availablePorts));
                        Notify(NotificationType.Error, $"端口 {portName} 不存在！请检查设备连接和端口配置");
                        return false;
                    }

                    _logger.LogInformation("正在打开扫描枪串口 {PortName} @ {BaudRate}bps...", portName, baudRate);

                    _serialPort = new SerialPort(portName, baudRate, PARITY, DATA_BITS, STOP_BITS)
                    {
                        ReadTimeout = READ_TIMEOUT_MS,
                        WriteTimeout = WRITE_TIMEOUT_MS,
                        Encoding = Encoding.ASCII,
                        DtrEnable = true,
                        RtsEnable = true,
                        Handshake = Handshake.None
                    };

                    _serialPort.DataReceived += OnDataReceived;
                    _serialPort.ErrorReceived += OnErrorReceived;

                    _serialPort.Open();
                    _isConnected = true;

                    // 记录连接成功时间，用于 PortWatchdog 宽限期判断
                    _connectionStableTime = DateTime.Now;

                    // 重置防抖计数器
                    _disconnectDetectedCount = 0;

                    _logger.LogInformation("✅ 扫描枪串口 {PortName} 已成功打开 @ {BaudRate}bps", portName, baudRate);
                    ConnectionStateChanged?.Invoke(this, true);
                    Notify(NotificationType.Success, $"扫描枪已连接: {portName} @ {baudRate}bps");

                    StartMonitoring();

                    return true;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError(ex, "❌ 端口 {PortName} 被其他程序占用", portName);
                    Notify(NotificationType.Error, $"端口 {portName} 被占用！请关闭其他串口工具");
                    _serialPort?.Dispose();
                    _serialPort = null;
                    _isConnected = false;
                    return false;
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "❌ 端口 {PortName} IO异常: {Message}", portName, ex.Message);
                    Notify(NotificationType.Error, $"端口 {portName} 通信异常: {ex.Message}");
                    _serialPort?.Dispose();
                    _serialPort = null;
                    _isConnected = false;
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 打开扫描枪串口失败: {PortName}, 错误: {Message}", portName, ex.Message);
                    Notify(NotificationType.Error, $"扫描枪连接失败: {ex.Message}");
                    _serialPort?.Dispose();
                    _serialPort = null;
                    _isConnected = false;
                    return false;
                }
            }
        }

        /// <summary>
        /// 断开连接（ICommunicationDevice 接口实现）
        /// </summary>
        public async Task DisconnectAsync()
        {
            await Task.Run(() =>
            {
                lock (_lockObject)
                {
                    StopMonitoring();
                    CloseSerialPort();
                    _isConnected = false;
                    _connectionStableTime = DateTime.MinValue;
                    _disconnectDetectedCount = 0;
                    ConnectionStateChanged?.Invoke(this, false);
                    _logger.LogInformation("扫描枪已断开连接");
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 打开扫描枪串口并开始监听（同步版本，保留兼容）
        /// </summary>
        public bool Connect(string portName = "COM9", int baudRate = DEFAULT_BAUD_RATE)
        {
            return ConnectInternal(portName, baudRate);
        }

        private void CloseSerialPort()
        {
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= OnDataReceived;
                    _serialPort.ErrorReceived -= OnErrorReceived;

                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "关闭串口时出现异常（可忽略）");
            }
        }

        #endregion

        #region 数据监听

        private void StartMonitoring()
        {
            if (_isMonitoring) return;

            _isMonitoring = true;
            _readLoopCts = new CancellationTokenSource();

            // ⭐ 启动串口热插拔检测
            StartPortWatchdog();

            _logger.LogDebug("扫描枪数据监听已启动");
        }

        private void StopMonitoring()
        {
            _isMonitoring = false;

            _readLoopCts?.Cancel();
            _readLoopCts?.Dispose();
            _readLoopCts = null;

            // ⭐ 停止热插拔检测
            StopPortWatchdog();

            // 停止并释放超时计时器
            _barcodeCompleteTimer?.Stop();
            _barcodeCompleteTimer?.Dispose();
            _barcodeCompleteTimer = null;

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

                        HandlePortLostByWatchdog();
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
                    _logger.LogInformation("热插拔检测：端口 {PortName} 重新出现，立即自动重连", _portName);
                    Notify(NotificationType.Info, $"检测到扫描枪重新插入，正在重连...");

                    // 同步执行重连（在 Timer 回调线程中，ConnectInternal 内部有锁保护）
                    try
                    {
                        var reconnected = ConnectInternal(_portName, _baudRate);
                        if (reconnected)
                        {
                            _logger.LogInformation("热插拔检测：自动重连成功！");
                            Notify(NotificationType.Success, "扫描枪已重新连接");
                        }
                        else
                        {
                            _logger.LogWarning("热插拔检测：自动重连失败，将在下次检测周期重试");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "热插拔检测：自动重连异常");
                    }
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
                if (_serialPort == null || !_serialPort.IsOpen)
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
                _ = _serialPort.BytesToRead;
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
        private void HandlePortLostByWatchdog()
        {
            lock (_lockObject)
            {
                if (!_isConnected)
                {
                    return;
                }

                CloseSerialPort();
                _isConnected = false;
                _connectionStableTime = DateTime.MinValue;
                _disconnectDetectedCount = 0;

                _barcodeCompleteTimer?.Stop();
                _barcodeCompleteTimer?.Dispose();
                _barcodeCompleteTimer = null;

                ConnectionStateChanged?.Invoke(this, false);
                _logger.LogInformation("扫描枪已断开连接，继续保持热插拔检测等待恢复");
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
        /// 串口数据接收事件处理
        /// 
        /// 工作原理（超时判定法）：
        /// 1. 扫描枪通过串口连续发送条码字符，间隔通常 < 10ms
        /// 2. 每次收到数据追加到缓冲区，同时重置 100ms 计时器
        /// 3. 100ms 内无新数据 → 认为条码已完整发送 → 触发回调提取条码
        /// 
        /// 优势：不依赖 \r\n 结束符，兼容扫描枪有/无结束符两种配置模式
        /// </summary>
        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            try
            {
                lock (_lockObject)
                {
                    // 一次性读取所有可用数据
                    int bytesToRead = _serialPort.BytesToRead;
                    if (bytesToRead <= 0) return;

                    byte[] buffer = new byte[bytesToRead];
                    int bytesRead = _serialPort.Read(buffer, 0, bytesToRead);

                    if (bytesRead > 0)
                    {
                        string data = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        _logger.LogDebug("扫描枪原始数据: {Data}", data.Replace("\r", "\\r").Replace("\n", "\\n"));

                        RawDataReceived?.Invoke(this, data);

                        // 追加到缓冲区（累积字符直到超时判定完整）
                        _dataBuffer.Append(data);

                        // 初始化或重置超时计时器
                        // 每次收到数据都重新计时，直到100ms内无新数据才认为条码完整
                        if (_barcodeCompleteTimer == null)
                        {
                            _barcodeCompleteTimer = new System.Timers.Timer(BARCODE_COMPLETE_TIMEOUT_MS);
                            _barcodeCompleteTimer.AutoReset = false;  // 只触发一次
                            _barcodeCompleteTimer.Elapsed += OnBarcodeCompleteTimeout;
                        }
                        _barcodeCompleteTimer.Stop();    // 停止上一次计时
                        _barcodeCompleteTimer.Start();   // 重新开始100ms倒计时
                    }
                }
            }
            catch (TimeoutException)
            {
                // 读取超时，正常情况
            }
            catch (IOException ex)
            {
                // ⭐ IO异常通常意味着设备已拔出
                _logger.LogWarning(ex, "扫描枪IO异常（可能已拔出）");
                // 不在此处立即触发断开，交给 PortWatchdog 防抖机制统一处理
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫描枪数据接收异常");
            }
        }

        /// <summary>
        /// 条码完整超时回调
        /// 
        /// 触发条件：最后一次收到串口数据后 100ms 内无新数据到达
        /// 此时认为扫描枪已完成一条条码的发送，提取缓冲区内容
        /// 
        /// 清理逻辑：
        /// - 去除首尾空白字符
        /// - 去除末尾的 \r、\n、\t（如果有配置结束符）
        /// - 空条码忽略
        /// </summary>
        private void OnBarcodeCompleteTimeout(object? sender, System.Timers.ElapsedEventArgs e)
        {
            string barcode;
            lock (_lockObject)
            {
                barcode = _dataBuffer.ToString();
                _dataBuffer.Clear();
            }

            // 清理条码：去首尾空白，去末尾换行符（兼容有/无结束符两种模式）
            barcode = barcode.Trim().TrimEnd('\r', '\n', '\t', ' ');

            if (!string.IsNullOrWhiteSpace(barcode))
            {
                _logger.LogInformation("扫描到条码: {Barcode}", barcode);

                // 触发条码事件
                var args = new BarcodeReceivedEventArgs(barcode, barcode);
                BarcodeReceived?.Invoke(this, args);
            }
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
            if (!_isConnected || _serialPort == null || !_serialPort.IsOpen)
            {
                _logger.LogWarning("扫描枪未连接，无法触发扫描");
                return false;
            }

            try
            {
                // Honeywell 触发扫描命令：SYN T CR
                byte[] triggerCommand = { 0x16, 0x54, 0x0D };

                lock (_lockObject)
                {
                    _serialPort.Write(triggerCommand, 0, triggerCommand.Length);
                }

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
            if (!_isConnected || _serialPort == null || !_serialPort.IsOpen)
            {
                _logger.LogWarning("扫描枪未连接");
                return false;
            }

            try
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(configCommand + "\r\n");

                lock (_lockObject)
                {
                    _serialPort.Write(cmdBytes, 0, cmdBytes.Length);
                }

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
            OnNotification?.Invoke(this, new CommunicationNotification(type, message, "H1900"));
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // ⭐ 同步清理连接资源（不能调用异步 DisconnectAsync）
            lock (_lockObject)
            {
                StopMonitoring();
                CloseSerialPort();
                _isConnected = false;
                _connectionStableTime = DateTime.MinValue;
                _disconnectDetectedCount = 0;
                ConnectionStateChanged?.Invoke(this, false);
            }

            _readLoopCts?.Dispose();
            _barcodeCompleteTimer?.Stop();
            _barcodeCompleteTimer?.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
