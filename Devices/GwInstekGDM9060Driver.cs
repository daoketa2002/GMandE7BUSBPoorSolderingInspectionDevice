// 📁 Devices/Multimeter/GwInstekGDM9060Driver.cs
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter
{
    /// <summary>
    /// 固纬 GDM-9060 万用表 SCPI 驱动
    /// 通讯方式：TCP Socket (LAN口)，SCPI指令集
    /// 默认端口：5025
    /// </summary>
    public class GwInstekGDM9060Driver : IAsyncDisposable, IDisposable
    {
        #region 常量

        private const int DEFAULT_PORT = 5025;
        private const int DEFAULT_TIMEOUT_MS = 5000;
        private const int RECONNECT_DELAY_MS = 3000;
        private const int MAX_RECONNECT_ATTEMPTS = 5;
        private const int RECEIVE_BUFFER_SIZE = 4096;

        #endregion

        #region 字段

        private readonly ILogger<GwInstekGDM9060Driver> _logger;
        private TcpClient? _tcpClient;
        private NetworkStream? _networkStream;
        private readonly SemaphoreSlim _commandLock = new(1, 1);
        private CancellationTokenSource? _reconnectCts;

        private string _host = "192.168.1.4";
        private int _port = DEFAULT_PORT;
        private int _timeoutMs = DEFAULT_TIMEOUT_MS;

        private volatile bool _isConnected;
        private volatile bool _isDisposed;
        private volatile int _isReconnecting;

        #endregion

        #region 事件

        /// <summary>
        /// 连接状态变更事件
        /// </summary>
        public event EventHandler<bool>? ConnectionStateChanged;

        /// <summary>
        /// 通信通知事件
        /// </summary>
        public event EventHandler<CommunicationNotification>? OnNotification;

        /// <summary>
        /// 测量数据接收事件
        /// </summary>
        public event EventHandler<MeasurementEventArgs>? MeasurementReceived;

        #endregion

        #region 属性

        public bool IsConnected => _isConnected && _tcpClient?.Connected == true;
        public string ConnectionInfo => $"{_host}:{_port}";

        #endregion

        #region 构造函数

        public GwInstekGDM9060Driver(ILogger<GwInstekGDM9060Driver> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #endregion

        #region 连接管理

        /// <summary>
        /// 连接到万用表（使用配置中的地址和端口）
        /// </summary>
        public async Task<bool> ConnectAsync(string host, int port = DEFAULT_PORT,
            int timeoutMs = DEFAULT_TIMEOUT_MS, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentNullException(nameof(host));

            _host = host;
            _port = port;
            _timeoutMs = timeoutMs;

            await _commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_isConnected)
                {
                    _logger.LogWarning("万用表已处于连接状态");
                    return true;
                }

                _logger.LogInformation("正在连接万用表 {Host}:{Port}...", _host, _port);

                await CleanupConnectionAsync().ConfigureAwait(false);

                _tcpClient = new TcpClient
                {
                    ReceiveTimeout = _timeoutMs,
                    SendTimeout = _timeoutMs
                };

                using var timeoutCts = new CancellationTokenSource(_timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await _tcpClient.ConnectAsync(_host, _port, linkedCts.Token).ConfigureAwait(false);
                _networkStream = _tcpClient.GetStream();

                // 发送 *IDN? 验证连接
                var idn = await SendCommandInternalAsync("*IDN?", linkedCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(idn))
                {
                    throw new InvalidOperationException("万用表连接验证失败：未收到 *IDN? 响应");
                }

                _isConnected = true;
                _logger.LogInformation("万用表连接成功！设备信息: {IDN}", idn);

                ConnectionStateChanged?.Invoke(this, true);
                Notify(NotificationType.Success, $"万用表已连接: {idn}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "连接万用表失败");
                await CleanupConnectionAsync().ConfigureAwait(false);
                Notify(NotificationType.Error, $"连接失败: {ex.Message}");
                return false;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            await _commandLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await CleanupConnectionAsync().ConfigureAwait(false);
                _isConnected = false;
                ConnectionStateChanged?.Invoke(this, false);
                _logger.LogInformation("万用表已断开连接");
            }
            finally
            {
                _commandLock.Release();
            }
        }

        private async Task CleanupConnectionAsync()
        {
            try
            {
                _reconnectCts?.Cancel();
                _reconnectCts?.Dispose();
                _reconnectCts = null;

                if (_networkStream != null)
                {
                    await _networkStream.DisposeAsync().ConfigureAwait(false);
                    _networkStream = null;
                }

                _tcpClient?.Close();
                _tcpClient?.Dispose();
                _tcpClient = null;

                _isConnected = false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "清理连接资源时出现异常（可忽略）");
            }
        }

        #endregion

        #region SCPI 核心指令

        /// <summary>
        /// 发送SCPI命令并读取响应
        /// </summary>
        private async Task<string> SendCommandInternalAsync(string command, CancellationToken ct)
        {
            if (_networkStream == null || _tcpClient == null || !_tcpClient.Connected)
            {
                throw new InvalidOperationException("万用表未连接");
            }

            // 确保命令以换行符结尾
            var cmd = command.EndsWith("\n") ? command : command + "\n";
            var cmdBytes = Encoding.ASCII.GetBytes(cmd);

            _logger.LogDebug("发送SCPI命令: {Command}", command);
            await _networkStream.WriteAsync(cmdBytes, ct).ConfigureAwait(false);
            await _networkStream.FlushAsync(ct).ConfigureAwait(false);

            // 读取响应
            using var memoryStream = new MemoryStream();
            var buffer = new byte[RECEIVE_BUFFER_SIZE];

            using var readTimeoutCts = new CancellationTokenSource(_timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readTimeoutCts.Token);

            try
            {
                while (true)
                {
                    int bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token)
                        .ConfigureAwait(false);

                    if (bytesRead == 0) break;

                    memoryStream.Write(buffer, 0, bytesRead);

                    // GDM-9060 响应通常以 \n 结尾
                    var response = Encoding.ASCII.GetString(memoryStream.ToArray());
                    if (response.Contains('\n'))
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                if (memoryStream.Length == 0)
                {
                    _logger.LogWarning("SCPI命令超时: {Command}", command);
                    return string.Empty;
                }
            }

            var result = Encoding.ASCII.GetString(memoryStream.ToArray()).TrimEnd('\r', '\n', ' ');
            _logger.LogDebug("SCPI响应: {Response}", result);
            return result;
        }

        /// <summary>
        /// 发送SCPI命令（公共接口）
        /// </summary>
        public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("万用表未连接，无法发送命令");
                return string.Empty;
            }

            await _commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await SendCommandInternalAsync(command, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送SCPI命令失败: {Command}", command);
                Notify(NotificationType.Error, $"命令执行失败: {ex.Message}");

                // 检测连接是否丢失
                if (ex is IOException or SocketException)
                {
                    await HandleConnectionLossAsync().ConfigureAwait(false);
                }

                throw;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        #endregion

        #region 测量功能（业务专用）

        /// <summary>
        /// 设置测量功能
        /// </summary>
        public async Task<bool> SetMeasureFunctionAsync(MeasureFunction function, CancellationToken ct = default)
        {
            var command = function switch
            {
                MeasureFunction.DCVoltage => "CONF:VOLT:DC",
                MeasureFunction.ACVoltage => "CONF:VOLT:AC",
                MeasureFunction.DCCurrent => "CONF:CURR:DC",
                MeasureFunction.ACCurrent => "CONF:CURR:AC",
                MeasureFunction.Resistance2W => "CONF:RES",
                MeasureFunction.Resistance4W => "CONF:FRES",
                MeasureFunction.Continuity => "CONF:CONT",
                MeasureFunction.Diode => "CONF:DIOD",
                MeasureFunction.Frequency => "CONF:FREQ",
                _ => throw new ArgumentException($"不支持的测量功能: {function}")
            };

            try
            {
                await SendCommandAsync(command, ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 执行单次测量并返回结果
        /// </summary>
        public async Task<MeasurementResult> MeasureAsync(CancellationToken ct = default)
        {
            try
            {
                var rawResponse = await SendCommandAsync("MEAS?", ct).ConfigureAwait(false);

                var result = new MeasurementResult
                {
                    RawValue = rawResponse,
                    Timestamp = DateTime.Now,
                    IsValid = false
                };

                if (string.IsNullOrWhiteSpace(rawResponse))
                {
                    result.ErrorMessage = "万用表无响应";
                    return result;
                }

                // 解析数值（去除单位）
                if (double.TryParse(rawResponse,
                    System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                    System.Globalization.CultureInfo.InvariantCulture, out double value))
                {
                    result.Value = value;
                    result.IsValid = true;
                }
                else
                {
                    // 尝试提取数值部分
                    var match = System.Text.RegularExpressions.Regex.Match(rawResponse, @"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?");
                    if (match.Success && double.TryParse(match.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value))
                    {
                        result.Value = value;
                        result.IsValid = true;
                    }
                    else
                    {
                        result.ErrorMessage = $"无法解析测量值: {rawResponse}";
                    }
                }

                _logger.LogDebug("测量结果: {Value} (原始: {Raw})", result.Value, rawResponse);
                MeasurementReceived?.Invoke(this, new MeasurementEventArgs(result));
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测量执行失败");
                return new MeasurementResult
                {
                    IsValid = false,
                    ErrorMessage = ex.Message,
                    Timestamp = DateTime.Now
                };
            }
        }

        /// <summary>
        /// 测量电阻（2线制）— 用于焊接不良检查
        /// </summary>
        public async Task<MeasurementResult> MeasureResistanceAsync(CancellationToken ct = default)
        {
            await SetMeasureFunctionAsync(MeasureFunction.Resistance2W, ct).ConfigureAwait(false);
            await Task.Delay(100, ct).ConfigureAwait(false); // 等待测量稳定
            return await MeasureAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 测量电阻（4线制）— 高精度焊接检查
        /// </summary>
        public async Task<MeasurementResult> MeasureResistance4WireAsync(CancellationToken ct = default)
        {
            await SetMeasureFunctionAsync(MeasureFunction.Resistance4W, ct).ConfigureAwait(false);
            await Task.Delay(100, ct).ConfigureAwait(false);
            return await MeasureAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 获取设备标识
        /// </summary>
        public async Task<string> GetDeviceIdentifierAsync(CancellationToken ct = default)
        {
            return await SendCommandAsync("*IDN?", ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 自检
        /// </summary>
        public async Task<bool> SelfTestAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await SendCommandAsync("*TST?", ct).ConfigureAwait(false);
                return result == "0" || result.Contains("PASS", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 重置设备
        /// </summary>
        public async Task ResetAsync(CancellationToken ct = default)
        {
            await SendCommandAsync("*RST", ct).ConfigureAwait(false);
        }

        #endregion

        #region 断线重连

        private async Task HandleConnectionLossAsync()
        {
            if (Interlocked.CompareExchange(ref _isReconnecting, 1, 0) == 1)
            {
                _logger.LogDebug("重连已在进行中，跳过");
                return;
            }

            try
            {
                _isConnected = false;
                ConnectionStateChanged?.Invoke(this, false);
                Notify(NotificationType.Warning, "万用表连接丢失，开始重连...");

                await CleanupConnectionAsync().ConfigureAwait(false);

                _reconnectCts = new CancellationTokenSource();
                int attempts = 0;

                while (!_isDisposed && attempts < MAX_RECONNECT_ATTEMPTS)
                {
                    attempts++;
                    _logger.LogInformation("万用表重连尝试 {Attempt}/{Max}", attempts, MAX_RECONNECT_ATTEMPTS);

                    try
                    {
                        if (await ConnectAsync(_host, _port, _timeoutMs, _reconnectCts.Token).ConfigureAwait(false))
                        {
                            _logger.LogInformation("万用表重连成功！");
                            Notify(NotificationType.ConnectionRestored, "万用表已恢复连接");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "万用表重连失败 ({Attempt}/{Max})", attempts, MAX_RECONNECT_ATTEMPTS);
                    }

                    if (attempts < MAX_RECONNECT_ATTEMPTS)
                    {
                        await Task.Delay(RECONNECT_DELAY_MS, _reconnectCts.Token).ConfigureAwait(false);
                    }
                }

                Notify(NotificationType.Critical, "万用表重连失败，已达最大尝试次数");
            }
            finally
            {
                Interlocked.Exchange(ref _isReconnecting, 0);
            }
        }

        #endregion

        #region 通知辅助

        private void Notify(NotificationType type, string message)
        {
            OnNotification?.Invoke(this, new CommunicationNotification(type, message, "GDM-9060"));
        }

        #endregion

        #region IDisposable / IAsyncDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();

            _tcpClient?.Close();
            _tcpClient?.Dispose();
            _networkStream?.Dispose();
            _commandLock?.Dispose();

            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            await DisconnectAsync().ConfigureAwait(false);
            _reconnectCts?.Dispose();
            _commandLock?.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion
    }

    #region 相关类型

    /// <summary>
    /// 测量功能枚举
    /// </summary>
    public enum MeasureFunction
    {
        DCVoltage,
        ACVoltage,
        DCCurrent,
        ACCurrent,
        Resistance2W,
        Resistance4W,
        Continuity,
        Diode,
        Frequency
    }

    /// <summary>
    /// 测量结果
    /// </summary>
    public class MeasurementResult
    {
        public double Value { get; set; }
        public string RawValue { get; set; } = string.Empty;
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public override string ToString() => IsValid ? Value.ToString("F4") : $"Error: {ErrorMessage}";
    }

    /// <summary>
    /// 测量事件参数
    /// </summary>
    public class MeasurementEventArgs : EventArgs
    {
        public MeasurementResult Result { get; }

        public MeasurementEventArgs(MeasurementResult result)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }
    }

    #endregion
}