// 📁 Devices/Scanner/HoneywellH1900Scanner.cs
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner
{
    /// <summary>
    /// 霍尼韦尔 H1900 条码扫描枪驱动
    /// 通讯方式：USB虚拟串口 (CDC类)
    /// 工作原理：监听串口，解析条码数据（通常以 \r\n 结尾）
    /// 
    /// ⚠️ 使用前请用Honeywell配置条码将扫描枪切换为 USB Serial 模式
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

        #endregion

        #region 字段

        private readonly ILogger<HoneywellH1900Scanner> _logger;
        private SerialPort? _serialPort;
        private CancellationTokenSource? _readLoopCts;
        private readonly StringBuilder _dataBuffer = new();
        private readonly object _lockObject = new();

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

            _logger.LogDebug("扫描枪数据监听已停止");
        }

        /// <summary>
        /// 串口数据接收事件处理
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
                        ProcessIncomingData(data);
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
        /// 处理接收到的数据，提取完整条码
        /// H1900默认配置：条码 + \r\n (CRLF)
        /// </summary>
        private void ProcessIncomingData(string data)
        {
            _dataBuffer.Append(data);
            var bufferContent = _dataBuffer.ToString();

            // 按换行符分割
            int newLineIndex;
            while ((newLineIndex = bufferContent.IndexOf('\r')) != -1 ||
                   (newLineIndex = bufferContent.IndexOf('\n')) != -1)
            {
                string barcode = bufferContent.Substring(0, newLineIndex).Trim();

                // 移除剩余数据
                int skipLength = newLineIndex + 1;
                if (bufferContent.Length > newLineIndex + 1 &&
                    bufferContent[newLineIndex] == '\r' && bufferContent[newLineIndex + 1] == '\n')
                {
                    skipLength = newLineIndex + 2;
                }

                bufferContent = bufferContent.Substring(skipLength);

                // 处理有效条码
                if (!string.IsNullOrWhiteSpace(barcode))
                {
                    _logger.LogInformation("扫描到条码: {Barcode}", barcode);

                    // 在UI线程触发事件
                    var args = new BarcodeReceivedEventArgs(barcode, data);
                    BarcodeReceived?.Invoke(this, args);
                }
            }

            // 更新缓冲区
            _dataBuffer.Clear();
            if (!string.IsNullOrEmpty(bufferContent))
            {
                _dataBuffer.Append(bufferContent);
            }
        }

        private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            _logger.LogWarning("扫描枪串口错误: {ErrorType}", e.EventType);
            Notify(NotificationType.Warning, $"串口错误: {e.EventType}");

            // 严重错误时尝试重连
            if (e.EventType == SerialError.TXFull || e.EventType == SerialError.RXOver)
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

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}