using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.IO.Ports;
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
    /// </summary>
    public class HoneywellH1900Scanner : IDisposable
    {
        #region 常量

        private const int BAUD_RATE = 115200;
        private const int DATA_BITS = 8;
        private const Parity PARITY = Parity.None;
        private const StopBits STOP_BITS = StopBits.One;
        private const int READ_TIMEOUT_MS = 100;
        private const int WRITE_TIMEOUT_MS = 100;
        private const int RECONNECT_CHECK_INTERVAL_MS = 3000;

        /// <summary>
        /// 条码完整判定超时（毫秒）
        /// 串口收到数据后，若此时间内无新数据到达，则认为条码已接收完整
        /// H1900扫描枪连续发送字符间隔通常 < 10ms，100ms足够覆盖且无感知延迟
        /// </summary>
        private const int BARCODE_COMPLETE_TIMEOUT_MS = 100;

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
        private volatile bool _isConnected;
        private volatile bool _isDisposed;
        private volatile bool _isMonitoring;

        #endregion

        #region 事件

        /// <summary>
        /// 条码扫描成功事件
        /// </summary>
        public event EventHandler<BarcodeReceivedEventArgs>? BarcodeReceived;

        /// <summary>
        /// 连接状态变更事件
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

        public bool IsConnected => _isConnected && _serialPort?.IsOpen == true;
        public string PortName => _portName;

        #endregion

        #region 构造函数

        public HoneywellH1900Scanner(ILogger<HoneywellH1900Scanner> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #endregion

        #region 连接管理

        /// <summary>
        /// 打开扫描枪串口并开始监听
        /// </summary>
        public bool Connect(string portName = "COM9", int baudRate = BAUD_RATE)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(HoneywellH1900Scanner));

            lock (_lockObject)
            {
                if (_isConnected)
                {
                    _logger.LogWarning("扫描枪已连接");
                    return true;
                }

                _portName = portName;

                try
                {
                    _logger.LogInformation("正在打开扫描枪串口 {PortName}...", portName);

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

                    _logger.LogInformation("扫描枪串口 {PortName} 已打开", portName);
                    ConnectionStateChanged?.Invoke(this, true);
                    Notify(NotificationType.Success, $"扫描枪已连接: {portName}");

                    // 启动数据监听
                    StartMonitoring();

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "打开扫描枪串口失败: {PortName}", portName);
                    Notify(NotificationType.Error, $"扫描枪连接失败: {ex.Message}");

                    _serialPort?.Dispose();
                    _serialPort = null;
                    _isConnected = false;

                    return false;
                }
            }
        }

        /// <summary>
        /// 断开扫描枪连接
        /// </summary>
        public void Disconnect()
        {
            lock (_lockObject)
            {
                StopMonitoring();
                CloseSerialPort();

                _isConnected = false;
                ConnectionStateChanged?.Invoke(this, false);
                _logger.LogInformation("扫描枪已断开连接");
            }
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

            _logger.LogDebug("扫描枪数据监听已启动");
        }

        private void StopMonitoring()
        {
            _isMonitoring = false;

            _readLoopCts?.Cancel();
            _readLoopCts?.Dispose();
            _readLoopCts = null;

            // 停止并释放超时计时器
            _barcodeCompleteTimer?.Stop();
            _barcodeCompleteTimer?.Dispose();
            _barcodeCompleteTimer = null;

            _logger.LogDebug("扫描枪数据监听已停止");
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

                // 触发条码事件（BarcodeReceivedEventArgs 内部会再次 Trim）
                var args = new BarcodeReceivedEventArgs(barcode, barcode);
                BarcodeReceived?.Invoke(this, args);
            }
        }

        private void OnErrorReceived(object sender, System.IO.Ports.SerialErrorReceivedEventArgs e)
        {
            _logger.LogWarning("扫描枪串口错误: {ErrorType}", e.EventType);
            Notify(NotificationType.Warning, $"串口错误: {e.EventType}");

            // 严重错误时尝试重连
            if (e.EventType == System.IO.Ports.SerialError.TXFull ||
                e.EventType == System.IO.Ports.SerialError.RXOver)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    if (_isConnected && !_isDisposed)
                    {
                        _logger.LogInformation("尝试重新打开扫描枪串口...");
                        lock (_lockObject)
                        {
                            CloseSerialPort();
                            Connect(_portName);
                        }
                    }
                });
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

            Disconnect();
            _readLoopCts?.Dispose();
            _barcodeCompleteTimer?.Stop();
            _barcodeCompleteTimer?.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}