// 📁 Devices/Multimeter/GwInstekGDM9060Driver.cs
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
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

        /// <summary>当前测量模式缓存，用于跳过相同模式的重复完整初始化</summary>
        private DmmCachedMode _cachedMode = DmmCachedMode.Unknown;
        /// <summary>缓存的上一次导通阈值（标准化后），用于判断导通阈值是否变化</summary>
        private double? _cachedContinuityThresholdOhm;

        /// <summary>驱动内部的测量模式缓存枚举，仅用于跳过重复完整初始化</summary>
        private enum DmmCachedMode
        {
            /// <summary>未知/缓存不可信（首次或断线后）</summary>
            Unknown,
            /// <summary>2 线电阻模式</summary>
            Resistance,
            /// <summary>导通测量模式</summary>
            Continuity
        }

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
                ResetDmmModeCache(); // 新连接，设备模式不可确认，缓存置为 Unknown
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
                ResetDmmModeCache(); // 断开连接，缓存不可信
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
        /// 如果缓存命中（当前已是 Resistance 模式），跳过完整初始化。
        /// SCPI 序列：ABOR → *CLS → CONF:RES → 自动量程 → 采样/触发配置
        /// 完成后用 *OPC? + SYST:ERR? 验证切换成功。
        /// 整个初始化在单次锁内完成，内部使用 SendSettingInternalAsync 避免重复加锁。
        /// </summary>
        public async Task<bool> InitializeResistanceModeAsync(CancellationToken ct = default)
        {
            // ── 缓存命中：当前已是 Resistance，跳过完整初始化 ──
            if (_cachedMode == DmmCachedMode.Resistance)
            {
                _logger.LogWarning(
                    "[DMM模式][缓存命中] Requested=Resistance, Current=Resistance, Reconfigured=false");
                return true;
            }

            var sw = Stopwatch.StartNew();
            var previousMode = _cachedMode;
            _logger.LogWarning(
                "[DMM模式][配置开始] From={From}, To=Resistance, ThresholdOhm=null",
                previousMode);

            bool result = await ConfigureResistanceModeInternalAsync("电阻模式", "CONF:RES", null, ct).ConfigureAwait(false);

            sw.Stop();
            if (result)
            {
                _cachedMode = DmmCachedMode.Resistance;
                _cachedContinuityThresholdOhm = null;
                _logger.LogWarning(
                    "[DMM模式][配置完成] From={From}, To=Resistance, ThresholdOhm=null, ElapsedMs={ElapsedMs}",
                    previousMode, sw.ElapsedMilliseconds);
            }
            else
            {
                ResetDmmModeCache();
                _logger.LogWarning(
                    "[DMM模式][配置失败] From={From}, To=Resistance, ThresholdOhm=null, ElapsedMs={ElapsedMs}, CacheReset=Unknown",
                    previousMode, sw.ElapsedMilliseconds);
            }
            return result;
        }

        /// <summary>
        /// 恢复万用表为远程可控的 2 线电阻空闲态。
        /// SCPI 序列与 InitializeResistanceModeAsync 相同，但不执行验证。
        /// 成功后更新模式缓存为 Resistance。
        /// </summary>
        public async Task<bool> PrepareIdleResistanceModeAsync(CancellationToken ct = default)
        {
            // 如果缓存已经是 Resistance，但 PrepareIdle 是显式请求，仍执行配置
            // （调用方不期望跳过，因为 PrepareIdle 目的就是确保设备处于电阻模式）
            var sw = Stopwatch.StartNew();
            bool result = await ConfigureResistanceModeInternalAsync("空闲态(电阻)", "CONF:RES", null, ct, verify: false).ConfigureAwait(false);
            sw.Stop();

            if (result)
            {
                _cachedMode = DmmCachedMode.Resistance;
                _cachedContinuityThresholdOhm = null;
                _logger.LogWarning(
                    "[DMM模式][配置完成] From=*, To=Resistance(空闲态), ElapsedMs={ElapsedMs}",
                    sw.ElapsedMilliseconds);
            }
            else
            {
                ResetDmmModeCache();
            }
            return result;
        }

        /// <summary>
        /// 初始化为导通测量模式（Continuity），并设置导通阈值。
        /// 如果缓存命中（当前已是 Continuity 且阈值相同），跳过完整初始化。
        /// SCPI 序列：ABOR → *CLS → CONF:CONT → SENS:CONT:THR {阈值} → 采样/触发配置
        /// 完成后用 *OPC? + SYST:ERR? 验证切换成功。
        /// 使用内部方法避免重复加锁。
        /// </summary>
        public async Task<bool> InitializeContinuityModeAsync(double thresholdOhm = 10.0, CancellationToken ct = default)
        {
            // 标准化阈值，与设备实际发送精度一致（保留2位小数）
            double normalizedThreshold = Math.Round(thresholdOhm, 2, MidpointRounding.AwayFromZero);

            // ── 缓存命中：当前已是 Continuity 且阈值相同，跳过完整初始化 ──
            if (_cachedMode == DmmCachedMode.Continuity
                && _cachedContinuityThresholdOhm.HasValue
                && Math.Abs(_cachedContinuityThresholdOhm.Value - normalizedThreshold) < 0.001)
            {
                _logger.LogWarning(
                    "[DMM模式][缓存命中] Requested=Continuity, Current=Continuity, ThresholdOhm={ThresholdOhm}, Reconfigured=false",
                    normalizedThreshold);
                return true;
            }

            var sw = Stopwatch.StartNew();
            string fromMode = _cachedMode.ToString();
            _logger.LogWarning(
                "[DMM模式][配置开始] From={From}, To=Continuity, ThresholdOhm={ThresholdOhm}",
                fromMode, normalizedThreshold);

            bool result = await ConfigureResistanceModeInternalAsync(
                $"导通模式(阈值={normalizedThreshold:F2}Ω)",
                "CONF:CONT",
                $"SENS:CONT:THR {normalizedThreshold:F2}",
                ct,
                verify: true).ConfigureAwait(false);

            sw.Stop();
            if (result)
            {
                _cachedMode = DmmCachedMode.Continuity;
                _cachedContinuityThresholdOhm = normalizedThreshold;
                _logger.LogWarning(
                    "[DMM模式][配置完成] From={From}, To=Continuity, ThresholdOhm={ThresholdOhm}, ElapsedMs={ElapsedMs}",
                    fromMode, normalizedThreshold, sw.ElapsedMilliseconds);
            }
            else
            {
                ResetDmmModeCache();
                _logger.LogWarning(
                    "[DMM模式][配置失败] From={From}, To=Continuity, ThresholdOhm={ThresholdOhm}, ElapsedMs={ElapsedMs}, CacheReset=Unknown",
                    fromMode, normalizedThreshold, sw.ElapsedMilliseconds);
            }
            return result;
        }

        /// <summary>
        /// 内部：配置万用表公共 SCPI 序列。
        /// SYST:REM → ABOR → *CLS → CONF → 量程 → 单次采样/触发
        /// </summary>
        private async Task<bool> ConfigureResistanceModeInternalAsync(
            string modeLabel, string confCommand, string? extraCommand, CancellationToken ct, bool verify = true)
        {
            await _commandLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await DrainReceiveBufferAsync(ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SYST:REM", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("ABOR", ct).ConfigureAwait(false);
                await SendSettingInternalAsync("*CLS", ct).ConfigureAwait(false);
                await SendSettingInternalAsync(confCommand, ct).ConfigureAwait(false);
                await SendSettingInternalAsync("SENS:RES:RANG:AUTO ON", ct).ConfigureAwait(false);
                await ConfigureSingleImmediateTriggerInternalAsync(ct).ConfigureAwait(false);

                if (extraCommand != null)
                    await SendSettingInternalAsync(extraCommand, ct).ConfigureAwait(false);

                if (verify && !await VerifyCommandCompletionAsync(ct).ConfigureAwait(false))
                {
                    _logger.LogWarning("[万用表][审计] {ModeLabel} 切换验证失败", modeLabel);
                    return false;
                }

                _logger.LogWarning("[万用表][审计] 万用表已切换为 {ModeLabel}", modeLabel);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "初始化万用表 {ModeLabel} 失败", modeLabel);
                return false;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>公共触发配置：SAMP:COUN 1 → TRIG:COUN 1 → TRIG:SOUR IMM</summary>
        private async Task ConfigureSingleImmediateTriggerInternalAsync(CancellationToken ct)
        {
            await SendSettingInternalAsync("SAMP:COUN 1", ct).ConfigureAwait(false);
            await SendSettingInternalAsync("TRIG:COUN 1", ct).ConfigureAwait(false);
            await SendSettingInternalAsync("TRIG:SOUR IMM", ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 读取 GDM-9060 READ? 原始返回文本，不在驱动层做 OPEN/SHORT/范围判定。
        /// 增加 [DMM测量] 耗时日志。
        /// </summary>
        public async Task<string> ReadResistanceRawAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                string result = await SendQueryAsync("READ?", ct).ConfigureAwait(false);
                sw.Stop();
                _logger.LogWarning(
                    "[DMM测量][完成] Command=READ?, Mode=Resistance, ElapsedMs={ElapsedMs}, RawText={RawText}",
                    sw.ElapsedMilliseconds, result);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogWarning(
                    "[DMM测量][失败] Command=READ?, Mode=Resistance, ElapsedMs={ElapsedMs}, Error={Error}",
                    sw.ElapsedMilliseconds, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 执行导通测量，发送 MEAS:CONT? 指令并返回原始字符串
        /// 增加 [DMM测量] 耗时日志。
        /// </summary>
        public async Task<string> ReadContinuityRawAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                string result = await SendQueryAsync("MEAS:CONT?", ct).ConfigureAwait(false);
                sw.Stop();
                _logger.LogWarning(
                    "[DMM测量][完成] Command=MEAS:CONT?, Mode=Continuity, ThresholdOhm={ThresholdOhm}, ElapsedMs={ElapsedMs}, RawText={RawText}",
                    _cachedContinuityThresholdOhm, sw.ElapsedMilliseconds, result);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogWarning(
                    "[DMM测量][失败] Command=MEAS:CONT?, Mode=Continuity, ElapsedMs={ElapsedMs}, Error={Error}",
                    sw.ElapsedMilliseconds, ex.Message);
                throw;
            }
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
        /// 退出远程控制，返回本地面板操作。SCPI: SYST:LOC
        /// </summary>
        public async Task ReleaseToLocalAsync(CancellationToken ct = default)
        {
            try
            {
                await SendSettingAsync("SYST:LOC", ct).ConfigureAwait(false);
                ResetDmmModeCache(); // 进入本地控制后，操作员可能修改仪表模式，缓存不可信
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
                var sw = Stopwatch.StartNew();
                try
                {
                    await DrainReceiveBufferAsync(linkedCts.Token).ConfigureAwait(false);
                    var response = await SendQueryInternalAsync("*IDN?", linkedCts.Token).ConfigureAwait(false);

                    bool success = !string.IsNullOrWhiteSpace(response);
                    sw.Stop();
                    _logger.LogWarning(
                        "[DMM性能][Ping] Command=*IDN?, ElapsedMs={ElapsedMs}, Success={Success}, Response={Response}",
                        sw.ElapsedMilliseconds, success, response);
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
                ResetDmmModeCache(); // 连接丢失，缓存不可信
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

        /// <summary>
        /// 重置模式缓存为 Unknown，标记设备当前模式不可信。
        /// 在断线、重连、通信异常、ReleaseToLocal 等场景调用。
        /// </summary>
        private void ResetDmmModeCache()
        {
            _cachedMode = DmmCachedMode.Unknown;
            _cachedContinuityThresholdOhm = null;
        }

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
}
