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

        #endregion

        #region 属性（⭐新增：供 DeviceConnectionManager 注入配置）

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
        /// 设备是否已连接（ICommunicationDevice 接口实现）
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

        #region 连接管理（⭐接口实现 + 内部重载）

        /// <summary>
        /// 连接到万用表（使用配置中的地址和端口）
        /// </summary>
        /// <summary>
        /// 异步连接设备（ICommunicationDevice 接口实现）
        /// 使用已注入的 Host/Port/TimeoutMs 属性值进行连接
        /// 由 DeviceConnectionManager 调用
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            return await ConnectInternalAsync(_host, _port, _timeoutMs, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 内部连接实现（保留原有逻辑，仅改为 private）
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

                // 发送 *IDN? 验证连接
                var idn = await SendQueryInternalAsync("*IDN?", linkedCts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(idn))
                {
                    throw new InvalidOperationException("万用表连接验证失败：未收到 *IDN? 响应");
                }

                _isConnected = true;
                _logger.LogInformation("万用表连接成功！设备信息: {IDN}", idn);

                // ⭐ 通过统一接口事件通知状态变更
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
        /// 断开连接（ICommunicationDevice 接口实现）
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

        // ============================================================
        // 以下 SCPI 指令、测量功能、断线重连、通知辅助、
        // IDisposable / IAsyncDisposable 实现
        // ⭐ 完全保留原有代码，此处省略以节省篇幅
        // 仅修改 HandleConnectionLossAsync 中的重连调用
        // ============================================================
        #region SCPI 核心指令

        /// <summary>
        /// SCPI 查询命令（以 ? 结尾）：发送后读取响应直到 \n 终止或超时。
        /// 命令结束符使用 \r\n（CRLF），与 GDM-9060 参考文档一致。
        /// </summary>
        private async Task<string> SendQueryInternalAsync(string command, CancellationToken ct)
        {
            if (_networkStream == null || _tcpClient == null || !_tcpClient.Connected)
            {
                throw new InvalidOperationException("万用表未连接");
            }

            // 确保查询命令以 \r\n 结尾
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
                if (memoryStream.Length == 0)
                {
                    _logger.LogWarning("SCPI查询命令超时: {Command}", command);
                    return string.Empty;
                }
            }

            var result = Encoding.ASCII.GetString(memoryStream.ToArray()).TrimEnd('\r', '\n', ' ');
            _logger.LogDebug("SCPI响应: {Response}", result);
            return result;
        }

        /// <summary>
        /// SCPI 设置命令（不以 ? 结尾）：只发送不等待响应。
        /// GDM-9060 的 *CLS、CONF:RES、CONF:CONT、SENS、SAMP、TRIG 等设置命令
        /// 正常不返回数据，不应等待响应以免每次超时。
        /// 命令结束符使用 \r\n（CRLF）。
        /// </summary>
        private async Task SendSettingInternalAsync(string command, CancellationToken ct)
        {
            if (_networkStream == null || _tcpClient == null || !_tcpClient.Connected)
            {
                throw new InvalidOperationException("万用表未连接");
            }

            // 确保设置命令以 \r\n 结尾
            var cmd = command.EndsWith("\n") ? command : command + "\r\n";
            var cmdBytes = Encoding.ASCII.GetBytes(cmd);

            _logger.LogDebug("SCPI设置命令（只写）: {Command}", command);
            await _networkStream.WriteAsync(cmdBytes, ct).ConfigureAwait(false);
            await _networkStream.FlushAsync(ct).ConfigureAwait(false);
            // 设置命令不读取响应——GDM-9060 对这些命令不返回数据
        }

        /// <summary>
        /// 发送SCPI命令（公共接口）。
        /// 自动根据命令是否以 ? 结尾区分查询/设置：
        /// - 查询命令（*IDN?、READ?、MEAS?、SYST:ERR?、*OPC? 等）：发送后读取响应
        /// - 设置命令（*CLS、CONF:RES、CONF:CONT、SENS:...、SAMP:...、TRIG:... 等）：只发送不等待响应
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
                // 根据命令是否以 ? 结尾自动分发：查询命令读响应，设置命令只写
                string trimmed = command.TrimEnd();
                if (trimmed.EndsWith("?"))
                {
                    return await SendQueryInternalAsync(command, ct).ConfigureAwait(false);
                }
                else
                {
                    await SendSettingInternalAsync(command, ct).ConfigureAwait(false);
                    return string.Empty;
                }
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
        /// 初始化为 2 线电阻测量模式。最小闭环阶段统一由上位机按电阻值判定 OPEN/SHORT/范围。
        /// 先 ABOR 中止上一轮测量，设置完成后用 *OPC? + SYST:ERR? 验证切换成功。
        /// </summary>
        public async Task<bool> InitializeResistanceModeAsync(CancellationToken ct = default)
        {
            try
            {
                await SendCommandAsync("ABOR", ct).ConfigureAwait(false);
                await SendCommandAsync("*CLS", ct).ConfigureAwait(false);
                await SendCommandAsync("CONF:RES", ct).ConfigureAwait(false);
                await SendCommandAsync("SENS:RES:RANG:AUTO ON", ct).ConfigureAwait(false);
                await SendCommandAsync("SAMP:COUN 1", ct).ConfigureAwait(false);
                await SendCommandAsync("TRIG:COUN 1", ct).ConfigureAwait(false);
                await SendCommandAsync("TRIG:SOUR IMM", ct).ConfigureAwait(false);

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
        }

        /// <summary>
        /// 初始化为导通测量模式（Continuity），并设置导通阈值。
        /// SCPI 序列：ABOR, *CLS, CONF:CONT, SENS:CONT:THR {阈值}, SAMP:COUN 1, TRIG:COUN 1, TRIG:SOUR IMM
        /// 设置完成后用 *OPC? + SYST:ERR? 验证切换成功。
        /// </summary>
        /// <param name="thresholdOhm">导通阈值(Ω)，默认 10Ω</param>
        public async Task<bool> InitializeContinuityModeAsync(double thresholdOhm = 10.0, CancellationToken ct = default)
        {
            try
            {
                await SendCommandAsync("ABOR", ct).ConfigureAwait(false);
                await SendCommandAsync("*CLS", ct).ConfigureAwait(false);
                await SendCommandAsync("CONF:CONT", ct).ConfigureAwait(false);
                await SendCommandAsync($"SENS:CONT:THR {thresholdOhm:F2}", ct).ConfigureAwait(false);
                await SendCommandAsync("SAMP:COUN 1", ct).ConfigureAwait(false);
                await SendCommandAsync("TRIG:COUN 1", ct).ConfigureAwait(false);
                await SendCommandAsync("TRIG:SOUR IMM", ct).ConfigureAwait(false);

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
        }

        /// <summary>
        /// 读取 GDM-9060 READ? 原始返回文本，不在驱动层做 OPEN/SHORT/范围判定。
        /// </summary>
        public async Task<string> ReadResistanceRawAsync(CancellationToken ct = default)
        {
            return await SendCommandAsync("READ?", ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 执行导通测量，发送 MEAS:CONT? 指令并返回原始字符串
        /// </summary>
        public async Task<string> ReadContinuityRawAsync(CancellationToken ct = default)
        {
            return await SendCommandAsync("MEAS:CONT?", ct).ConfigureAwait(false);
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

        /// <summary>
        /// 恢复万用表为远程可控的 2 线电阻空闲态。
        /// 指令序列：ABOR → *CLS → CONF:RES → SENS:RES:RANG:AUTO ON → SAMP:COUN 1 → TRIG:COUN 1 → TRIG:SOUR IMM
        /// </summary>
        public async Task<bool> PrepareIdleResistanceModeAsync(CancellationToken ct = default)
        {
            try
            {
                await SendCommandAsync("ABOR", ct).ConfigureAwait(false);
                await SendCommandAsync("*CLS", ct).ConfigureAwait(false);
                await SendCommandAsync("CONF:RES", ct).ConfigureAwait(false);
                await SendCommandAsync("SENS:RES:RANG:AUTO ON", ct).ConfigureAwait(false);
                await SendCommandAsync("SAMP:COUN 1", ct).ConfigureAwait(false);
                await SendCommandAsync("TRIG:COUN 1", ct).ConfigureAwait(false);
                await SendCommandAsync("TRIG:SOUR IMM", ct).ConfigureAwait(false);
                _logger.LogInformation("万用表已恢复为远程 2 线电阻空闲态");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "万用表恢复空闲态失败");
                return false;
            }
        }

        /// <summary>
        /// 退出远程控制，返回本地面板操作。SCPI: SYST:LOC
        /// </summary>
        public async Task ReleaseToLocalAsync(CancellationToken ct = default)
        {
            try
            {
                await SendCommandAsync("SYST:LOC", ct).ConfigureAwait(false);
                _logger.LogInformation("万用表已退出远程控制，返回本地面板操作");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "万用表退出远程控制时异常（不影响主流程）");
            }
        }

        #endregion

        #region 模式切换验证

        /// <summary>
        /// 验证上一组设置命令是否执行完成且无错误。
        /// 发送 *OPC? 等待 1 确认命令队列完成，再发 SYST:ERR? 确认无错误。
        /// </summary>
        private async Task<bool> VerifyCommandCompletionAsync(CancellationToken ct)
        {
            try
            {
                var opc = await SendCommandAsync("*OPC?", ct).ConfigureAwait(false);
                if (opc.Trim() != "1")
                {
                    _logger.LogWarning("[万用表] *OPC? 返回非预期值：{Value}", opc);
                    return false;
                }

                var err = await SendCommandAsync("SYST:ERR?", ct).ConfigureAwait(false);
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

        #region 断线重连（修改：使用属性而非硬编码）

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
                        // ⭐ 使用内部方法重连（使用已注入的属性值）
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
