// 📁 Devices/Multimeter/GwInstekGDM9060Driver.cs
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter
{
    /// <summary>
    /// 固纬 GDM-9060 万用表 SCPI 驱动
    /// 通讯方式：TCP Socket (LAN口)，SCPI指令集
    /// 默认端口：5025
    /// 
    /// 【指令发送架构】
    /// SendSettingAsync  — 公共设置命令（只写不读），用于 *CLS、CONF:RES、TRIG:SOUR IMM 等
    /// SendQueryAsync    — 公共查询命令（写后读取），用于 *IDN?、READ?、MEAS:CONT?、*OPC?、SYST:ERR? 等
    /// 内部不再有自动分发逻辑，调用方明确意图。
    /// </summary>
    public class GwInstekGDM9060Driver : IMultimeterDevice, IAsyncDisposable, IDisposable
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
        private volatile bool _receiveBufferPossiblyDirty;

        #endregion

        #region 属性

        /// <summary>
        /// 万用表IP地址
        /// 由 DeviceConnectionManager 从 DeviceSettings.json 读取并设置
        /// </summary>
        public string Host
        {
            get => _host;
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                    _host = value;
            }
        }

        /// <summary>
        /// SCPI端口号（默认5025）
        /// 由 DeviceConnectionManager 从 DeviceSettings.json 读取并设置
        /// </summary>
        public int Port
        {
            get => _port;
            set
            {
                if (value > 0 && value <= 65535)
                    _port = value;
            }
        }

        /// <summary>
        /// 通信超时时间（毫秒）
        /// 由 DeviceConnectionManager 从 DeviceSettings.json 读取并设置
        /// </summary>
        public int TimeoutMs
        {
            get => _timeoutMs;
            set
            {
                if (value > 0)
                    _timeoutMs = value;
            }
        }

        /// <summary>
        /// 设备是否已连接
        /// </summary>
        public bool IsConnected => _isConnected && _tcpClient?.Connected == true;

        /// <summary>
        /// 连接信息文本（IP:端口）
        /// </summary>
        public string ConnectionInfo => $"{_host}:{_port}";

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

        #region 构造函数

        public GwInstekGDM9060Driver(ILogger<GwInstekGDM9060Driver> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #endregion

        #region 连接管理

        /// <summary>
        /// 异步连接设备（ICommunicationDevice 接口实现）
        /// 使用已注入的 Host/Port/TimeoutMs 属性值进行连接
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            return await ConnectInternalAsync(_host, _port, _timeoutMs, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 内部连接实现
        /// </summary>
        private async Task<bool> ConnectInternalAsync(string host, int port,
            int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentNullException(nameof(host));

            await _commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_isConnected)
                {
                    _logger.LogWarning("万用表已处于连接状态");
                    return true;
                }

                _logger.LogInformation("正在连接万用表 {Host}:{Port}...", host, port);

                await CleanupConnectionAsync().ConfigureAwait(false);

                _tcpClient = new TcpClient
                {
                    ReceiveTimeout = timeoutMs,
                    SendTimeout = timeoutMs
                };

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await _tcpClient.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
                _networkStream = _tcpClient.GetStream();

                // 发送 *IDN? 验证连接（使用内部查询方法，此时已持有锁）
                var idn = await SendQueryInternalAsync("*IDN?", linkedCts.Token).ConfigureAwait(false);
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
            // 兜底：断开前尽力退出远程控制，失败不阻断断开流程
            await ReleaseToLocalAsync(CancellationToken.None).ConfigureAwait(false);

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

        #region SCPI 核心指令（内部实现）

        /// <summary>
        /// SCPI 查询命令（写后读取响应直到 \n 终止或超时）。
        /// 内部方法，不加 _commandLock，由公共方法 SendQueryAsync 调用。
        /// </summary>
        private async Task<string> SendQueryInternalAsync(string command, CancellationToken ct)
        {
            if (_networkStream == null || _tcpClient == null || !_tcpClient.Connected)
            {
                throw new InvalidOperationException("万用表未连接");
            }

            // 统一追加 \r\n 终止符
            var cmd = command.EndsWith("\n") ? command : command + "\r\n";
            var cmdBytes = Encoding.ASCII.GetBytes(cmd);

            _logger.LogDebug("SCPI查询命令: {Command}", command);
            await _networkStream.WriteAsync(cmdBytes, ct).ConfigureAwait(false);
            await _networkStream.FlushAsync(ct).ConfigureAwait(false);

            // 读取响应直到 \n 终止
            using var memoryStream = new MemoryStream();
            var buffer = new byte[RECEIVE_BUFFER_SIZE];

            using var readTimeoutCts = new CancellationTokenSource(_timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readTimeoutCts.Token);
            bool readTimedOut = false;

            try
            {
                while (true)
                {
                    int bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token)
                        .ConfigureAwait(false);

                    if (bytesRead == 0) break;

                    memoryStream.Write(buffer, 0, bytesRead);

                    var response = Encoding.ASCII.GetString(memoryStream.ToArray());
                    if (response.Contains('\n'))
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                readTimedOut = true;
                if (memoryStream.Length == 0)
                {
                    _receiveBufferPossiblyDirty = true;
                    _logger.LogWarning("SCPI查询命令超时: {Command}", command);
                    return string.Empty;
                }
            }

            var result = Encoding.ASCII.GetString(memoryStream.ToArray()).TrimEnd('\r', '\n', ' ');
            if (readTimedOut)
            {
                _receiveBufferPossiblyDirty = true;
                _logger.LogWarning("SCPI查询命令读取到部分响应但未收到结束符: {Command}, Response={Response}", command, result);
            }
            else
            {
                _receiveBufferPossiblyDirty = false;
            }

            _logger.LogDebug("SCPI响应: {Response}", result);
            return result;
        }

        /// <summary>
        /// SCPI 设置命令（只写不读）。
        /// 内部方法，不加 _commandLock，由公共方法 SendSettingAsync 调用。
        /// </summary>
        private async Task SendSettingInternalAsync(string command, CancellationToken ct)
        {
            if (_networkStream == null || _tcpClient == null || !_tcpClient.Connected)
            {
                throw new InvalidOperationException("万用表未连接");
            }

            // 统一追加 \r\n 终止符
            var cmd = command.EndsWith("\n") ? command : command + "\r\n";
            var cmdBytes = Encoding.ASCII.GetBytes(cmd);

            _logger.LogDebug("SCPI设置命令（只写）: {Command}", command);
            await _networkStream.WriteAsync(cmdBytes, ct).ConfigureAwait(false);
            await _networkStream.FlushAsync(ct).ConfigureAwait(false);
            // 设置命令不读取响应——GDM-9060 对这些命令不返回数据
        }

        /// <summary>
        /// 清理 TCP 接收缓冲区中迟到的上一条查询响应，避免 *IDN? 残留被后续 *OPC? 读走。
        /// 调用方必须已经持有 _commandLock，确保清理期间没有其他 SCPI 命令并发读写。
        /// </summary>
        private async Task DrainReceiveBufferAsync(CancellationToken ct)
        {
            if (_networkStream == null || _tcpClient == null || !_tcpClient.Connected)
                return;

            var buffer = new byte[RECEIVE_BUFFER_SIZE];
            int totalBytes = 0;
            int quietChecks = _receiveBufferPossiblyDirty ? 5 : 2;

            for (int i = 0; i < quietChecks; i++)
            {
                while (_networkStream.DataAvailable)
                {
                    int bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, ct)
                        .ConfigureAwait(false);
                    if (bytesRead <= 0)
                        break;

                    totalBytes += bytesRead;
                    i = 0;
                }

                if (i < quietChecks - 1)
                    await Task.Delay(20, ct).ConfigureAwait(false);
            }

            if (totalBytes > 0)
            {
                _logger.LogWarning("[万用表][审计] 已清理 TCP 接收缓冲区残留响应 {Bytes} 字节，避免查询响应串台", totalBytes);
            }

            _receiveBufferPossiblyDirty = false;
        }

        #endregion

        #region SCPI 公共指令方法

        /// <summary>
        /// 发送 SCPI 设置命令（只写不读）。
        /// 用于 *CLS、CONF:RES、CONF:CONT、SENS:xxx、SAMP:xxx、TRIG:xxx、SYST:LOC 等。
        /// 自动处理连接丢失和重连。
        /// </summary>
        /// <param name="command">SCPI 设置命令（不以 ? 结尾）</param>
        /// <param name="ct">取消令牌</param>
        public async Task SendSettingAsync(string command, CancellationToken ct = default)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("万用表未连接，无法发送设置命令");
                return;
            }

            await _commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SendSettingInternalAsync(command, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送SCPI设置命令失败: {Command}", command);
                Notify(NotificationType.Error, $"设置命令执行失败: {ex.Message}");

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

        /// <summary>
        /// 发送 SCPI 查询命令（写后读取响应）。
        /// 用于 *IDN?、READ?、MEAS?、MEAS:CONT?、*OPC?、SYST:ERR?、*TST? 等。
        /// 自动处理连接丢失和重连。
        /// </summary>
        /// <param name="command">SCPI 查询命令（以 ? 结尾）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>设备返回的响应字符串，超时时返回空字符串</returns>
        public async Task<string> SendQueryAsync(string command, CancellationToken ct = default)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("万用表未连接，无法发送查询命令");
                return string.Empty;
            }

            await _commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await SendQueryInternalAsync(command, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送SCPI查询命令失败: {Command}", command);
                Notify(NotificationType.Error, $"查询命令执行失败: {ex.Message}");

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

        #region 模式切换验证

        /// <summary>
        /// 验证上一组设置命令是否执行完成且无错误。
        /// 发送 *OPC? 等待 1 确认命令队列完成，再发 SYST:ERR? 确认无错误。
        /// 【注意】此方法使用内部 SendQueryInternalAsync，不经过 _commandLock，
        /// 由调用方（InitializeXxxModeAsync）确保已持有锁，避免双重加锁。
        /// </summary>
        private async Task<bool> VerifyCommandCompletionAsync(CancellationToken ct)
        {
            try
            {
                var opc = await SendQueryInternalAsync("*OPC?", ct).ConfigureAwait(false);
                if (opc.Trim() != "1")
                {
                    if (opc.StartsWith("GWInstek", StringComparison.OrdinalIgnoreCase))
                    {
                        _receiveBufferPossiblyDirty = true;
                        _logger.LogWarning("[万用表][审计] *OPC? 读到设备身份响应，疑似上一条 *IDN? 残留或查询响应串台：{Value}", opc);
                    }
                    else
                    {
                        _logger.LogWarning("[万用表] *OPC? 返回非预期值：{Value}", opc);
                    }
                    return false;
                }

                var err = await SendQueryInternalAsync("SYST:ERR?", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(err) && !err.StartsWith("0") && !err.Contains("No error", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[万用表][审计] 模式切换后存在设备错误：{Error}", err);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[万用表] 模式切换验证通信异常");
                return false;
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
                await SendSettingAsync(command, ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 初始化为 2 线电阻测量模式。
        /// SCPI 序列：ABOR → *CLS → CONF:RES → 自动量程 → 采样/触发配置
        /// 完成后用 *OPC? + SYST:ERR? 验证切换成功。
        /// 整个初始化在单次锁内完成，内部使用 SendSettingInternalAsync 避免重复加锁。
        /// </summary>
        public async Task<bool> InitializeResistanceModeAsync(CancellationToken ct = default)
        {
            await _commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await DrainReceiveBufferAsync(ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SYST:REM", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("ABOR", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("*CLS", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("CONF:RES", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SENS:RES:RANG:AUTO ON", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SAMP:COUN 1", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("TRIG:COUN 1", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("TRIG:SOUR IMM", ct).ConfigureAwait(false);

                if (!await VerifyCommandCompletionAsync(ct).ConfigureAwait(false))
                {
                    _logger.LogWarning("[万用表][审计] 电阻模式切换验证失败");
                    return false;
                }

                _logger.LogWarning("[万用表][审计] 万用表已切换为 2 线电阻模式");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "初始化万用表电阻模式失败");
                return false;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>
        /// 初始化为导通测量模式（Continuity），并设置导通阈值。
        /// SCPI 序列：ABOR → *CLS → CONF:CONT → SENS:CONT:THR {阈值} → 采样/触发配置
        /// 完成后用 *OPC? + SYST:ERR? 验证切换成功。
        /// 整个初始化在单次锁内完成。
        /// </summary>
        /// <param name="thresholdOhm">导通阈值(Ω)，默认 10Ω</param>
        public async Task<bool> InitializeContinuityModeAsync(double thresholdOhm = 10.0, CancellationToken ct = default)
        {
            await _commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await DrainReceiveBufferAsync(ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SYST:REM", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("ABOR", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("*CLS", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("CONF:CONT", ct).ConfigureAwait(false);
                await SendSettingInternalAsync($"SENS:CONT:THR {thresholdOhm:F2}", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SAMP:COUN 1", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("TRIG:COUN 1", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("TRIG:SOUR IMM", ct).ConfigureAwait(false);

                if (!await VerifyCommandCompletionAsync(ct).ConfigureAwait(false))
                {
                    _logger.LogWarning("[万用表][审计] 导通模式切换验证失败，导通阈值={ThresholdOhm}Ω", thresholdOhm);
                    return false;
                }

                _logger.LogWarning("[万用表][审计] 万用表已切换为导通模式，导通阈值={ThresholdOhm}Ω", thresholdOhm);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "初始化万用表导通模式失败");
                return false;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>
        /// 读取 GDM-9060 READ? 原始返回文本，不在驱动层做 OPEN/SHORT/范围判定。
        /// </summary>
        public async Task<string> ReadResistanceRawAsync(CancellationToken ct = default)
        {
            return await SendQueryAsync("READ?", ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 执行导通测量，发送 MEAS:CONT? 指令并返回原始字符串
        /// </summary>
        public async Task<string> ReadContinuityRawAsync(CancellationToken ct = default)
        {
            return await SendQueryAsync("MEAS:CONT?", ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 执行单次测量并返回结果
        /// </summary>
        public async Task<MeasurementResult> MeasureAsync(CancellationToken ct = default)
        {
            try
            {
                var rawResponse = await SendQueryAsync("MEAS?", ct).ConfigureAwait(false);

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
            return await SendQueryAsync("*IDN?", ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 自检
        /// </summary>
        public async Task<bool> SelfTestAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await SendQueryAsync("*TST?", ct).ConfigureAwait(false);
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
            await SendSettingAsync("*RST", ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 恢复万用表为远程可控的 2 线电阻空闲态。
        /// SCPI 序列：ABOR → *CLS → CONF:RES → 自动量程 → 采样/触发配置
        /// </summary>
        public async Task<bool> PrepareIdleResistanceModeAsync(CancellationToken ct = default)
        {
            await _commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await DrainReceiveBufferAsync(ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SYST:REM", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("ABOR", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("*CLS", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("CONF:RES", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SENS:RES:RANG:AUTO ON", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SAMP:COUN 1", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("TRIG:COUN 1", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("TRIG:SOUR IMM", ct).ConfigureAwait(false);
                _logger.LogInformation("万用表已恢复为远程 2 线电阻空闲态");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "万用表恢复空闲态失败");
                return false;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>
        /// 退出远程控制，返回本地面板操作。SCPI: SYST:LOC
        /// </summary>
        public async Task ReleaseToLocalAsync(CancellationToken ct = default)
        {
            try
            {
                await SendSettingAsync("SYST:LOC", ct).ConfigureAwait(false);
                _logger.LogInformation("万用表已退出远程控制，返回本地面板操作");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "万用表退出远程控制时异常（不影响主流程）");
            }
        }

        /// <summary>
        /// 轻量级通信验证：发送 *IDN? 并检查是否有非空响应。
        /// 使用同一把 _commandLock 串行化查询，避免 *IDN? 响应迟到后污染后续 *OPC? / READ?。
        /// 带独立短超时（500ms），避免长时间阻塞启动复核。
        /// </summary>
        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            if (!IsConnected)
            {
                _logger.LogDebug("[万用表Ping] 连接标志为 false，跳过通信验证");
                return false;
            }

            try
            {
                // 使用独立短超时，防止网络故障时长时间阻塞
                using var timeoutCts = new CancellationTokenSource(500);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await _commandLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                try
                {
                    await DrainReceiveBufferAsync(linkedCts.Token).ConfigureAwait(false);
                    var response = await SendQueryInternalAsync("*IDN?", linkedCts.Token).ConfigureAwait(false);

                    bool success = !string.IsNullOrWhiteSpace(response);
                    _logger.LogDebug("[万用表Ping] 结果={Result}, 响应={Response}", success, response);
                    return success;
                }
                finally
                {
                    _commandLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _receiveBufferPossiblyDirty = true;
                _logger.LogWarning("[万用表Ping] 超时（500ms），万用表不可通信");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[万用表Ping] 通信异常，万用表不可通信");
                return false;
            }
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
                        if (await ConnectInternalAsync(_host, _port, _timeoutMs, _reconnectCts.Token).ConfigureAwait(false))
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

        /// <summary>测量值分类，默认 Normal</summary>
        public MeasurementValueKind ValueKind { get; set; } = MeasurementValueKind.Normal;
        /// <summary>
        /// 是否应中止检测。
        /// true：NaN / -Infinity / 负电阻值 / 解析失败 → 当前项置 NG 后中止整轮。
        /// false：超量程大数 / +Infinity → 正常判 NG，按 ContinueTestingAfterNg 决定是否继续。
        /// </summary>
        public bool ShouldAbortInspection { get; set; }
        /// <summary>
        /// 显示文本覆写。
        /// 不为空时运行页检查结果列直接显示此值（如 "NG"），避免超长数字进入格式化。
        /// </summary>
        public string DisplayTextOverride { get; set; } = string.Empty;

        public override string ToString() => IsValid
            ? InputValidationHelper.FormatResistanceValue(Value)
            : $"Error: {ErrorMessage}";
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
