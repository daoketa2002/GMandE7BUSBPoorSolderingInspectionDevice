// ============================================================
// LEGACY: 欧姆龙 FINS/TCP 协议实现
// 来源：其他项目（马达特性履历/风扇压入机履历）
// 说明：本项目使用松下 FP0H + Modbus TCP 协议，此文件不使用
// 如需删除，请先确认没有被任何代码引用
// ============================================================

using Serilog;
using System;
using System.Buffers;
using System.Diagnostics;  // ⭐ 添加 Debug
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.PLC通讯模块
{
    /// <summary>
    /// FINS/TCP 通信客户端
    /// 严格遵循欧姆龙官方手册 W342-E1-17
    /// 底层协议参考: Joan Magnet 标准实现 (2015)
    /// </summary>
    public sealed class FinsTcpUtil : IAsyncDisposable
    {
        #region 常量定义

        private const int FINS_TCP_HEADER_LEN = 8;
        private const int FINS_TCP_COMMAND_LEN = 4;
        private const int FINS_TCP_ERROR_LEN = 4;
        private const int FINS_HEADER_LEN = 10;
        private const int FINS_COMMAND_CODE_LEN = 2;
        private const int FINS_PARAM_LEN = 6;
        private const int FINS_MIN_RESPONSE_LEN = 14;

        private static readonly byte[] FINS_HEADER_BYTES = { 0x46, 0x49, 0x4E, 0x53 };
        private const uint FINS_TCP_COMMAND_SEND = 0x00000002;
        private const uint FINS_TCP_COMMAND_RECV = 0x00000003;
        private const uint FINS_TCP_ERROR_OK = 0x00000000;

        private const byte ICF_COMMAND = 0x80;
        private const byte ICF_RESPONSE = 0xC0;
        private const byte RSV_DEFAULT = 0x00;
        private const byte DNA_LOCAL = 0x00;
        private const byte DA2_CPU_UNIT = 0x00;
        private const byte SNA_LOCAL = 0x00;
        private const byte SA2_HOST = 0x00;

        private const byte MRC_MEMORY_AREA = 0x01;
        private const byte SRC_READ = 0x01;
        private const byte SRC_WRITE = 0x02;

        private const int MAX_RETRY_COUNT = 3;
        private const int RETRY_BASE_DELAY_MS = 100;
        private const int LOG_SAMPLE_RATE = 100;

        #endregion

        #region 字段

        private readonly FinsTcpConfig _config;
        private readonly ILogger _logger;
        private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
        private readonly SemaphoreSlim _asyncLock = new SemaphoreSlim(1, 1);
        private readonly FinsPerformanceCounters _counters;

        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private byte _clientNode;
        private byte _serverNode;
        private string _serverIP;
        private int _serverPort;

        private int _isConnected;
        private int _disposed;
        private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();

        private int _reconnectAttempts;
        private Timer _reconnectTimer;
        private int _logCounter;

        #endregion

        #region 事件和属性

        public event EventHandler<bool> ConnectionStateChanged;
        public event EventHandler<FinsTraceEventArgs> DataTraced;

        public bool IsConnected => Interlocked.CompareExchange(ref _isConnected, 0, 0) == 1;
        public FinsPerformanceCounters Counters => _counters;
        public string ConnectionInfo => $"PLC [{_serverIP}:{_serverPort}] - 本地节点: {_clientNode}, 远程节点: {_serverNode}, 状态: {(IsConnected ? "已连接" : "未连接")}";

        #endregion

        #region 构造函数

        public FinsTcpUtil(FinsTcpConfig config, ILogger logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _counters = new FinsPerformanceCounters();

            if (_config.AutoReconnect)
            {
                _reconnectTimer = new Timer(ReconnectCallback, null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        #endregion

        #region 日志辅助方法

        private void LogVerbose(string message, params object[] args)
        {
            if (_config.LogLevel >= LogLevel.Verbose)
                _logger.Verbose(message, args);
        }

        private void LogDebug(string message, params object[] args)
        {
            if (_config.LogLevel >= LogLevel.Debug)
                _logger.Debug(message, args);
        }

        private void LogSampled(string message, params object[] args)
        {
            if (_config.LogLevel >= LogLevel.Debug &&
                Interlocked.Increment(ref _logCounter) % LOG_SAMPLE_RATE == 0)
            {
                _logger.Debug(message, args);
            }
        }

        #endregion

        #region 区域码转换

        private byte GetAreaCode(OmronAddressType type, bool isBit)
        {
            return (type, isBit) switch
            {
                (OmronAddressType.CIO, true) => 0x30,
                (OmronAddressType.CIO, false) => 0xB0,
                (OmronAddressType.WR, true) => 0x31,
                (OmronAddressType.WR, false) => 0xB1,
                (OmronAddressType.HR, true) => 0x32,
                (OmronAddressType.HR, false) => 0xB2,
                (OmronAddressType.AR, true) => 0x33,
                (OmronAddressType.AR, false) => 0xB3,
                (OmronAddressType.DM, true) => 0x02,
                (OmronAddressType.DM, false) => 0x82,
                (OmronAddressType.TIM, true) => 0x09,
                (OmronAddressType.TIM, false) => 0x89,
                (OmronAddressType.CNT, true) => 0x09,
                (OmronAddressType.CNT, false) => 0x89,
                _ => throw new ArgumentException($"不支持的区域类型: {type}, isBit={isBit}")
            };
        }

        #endregion

        #region 地址验证

        private bool ValidateAddress(OmronAddressType type, int address, int? bit = null)
        {
            bool addressValid = type switch
            {
                OmronAddressType.CIO => address >= 0 && address <= 6143,
                OmronAddressType.WR => address >= 0 && address <= 511,
                OmronAddressType.HR => address >= 0 && address <= 511,
                OmronAddressType.AR => address >= 0 && address <= 959,
                OmronAddressType.DM => address >= 0 && address <= 32767,
                OmronAddressType.TIM => address >= 0 && address <= 4095,
                OmronAddressType.CNT => address >= 0 && address <= 4095,
                _ => false
            };

            if (!addressValid) return false;

            if (bit.HasValue)
            {
                if (type == OmronAddressType.TIM || type == OmronAddressType.CNT)
                    return bit.Value == 0;
                return bit.Value >= 0 && bit.Value <= 15;
            }

            return true;
        }

        #endregion

        #region 连接管理

        public async Task<bool> ConnectAsync(string ip, int port = 9600, byte localNode = 0, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ip)) throw new ArgumentNullException(nameof(ip));

            await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsConnected)
                {
                    _logger.Warning("已经处于连接状态，请先断开");
                    return true;
                }

                _serverIP = ip;
                _serverPort = port;

                _logger.Information($"正在连接PLC {ip}:{port}...");

                using var timeoutCts = new CancellationTokenSource(_config.HandshakeTimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _disposeCts.Token);

                _tcpClient = new TcpClient();
                var connectTask = _tcpClient.ConnectAsync(ip, port);
                var completedTask = await Task.WhenAny(connectTask, Task.Delay(_config.HandshakeTimeoutMs, linkedCts.Token)).ConfigureAwait(false);

                if (completedTask != connectTask)
                    throw new TimeoutException($"连接超时 ({_config.HandshakeTimeoutMs}ms)");

                await connectTask.ConfigureAwait(false);

                _tcpClient.ReceiveTimeout = _config.ReceiveTimeoutMs;
                _tcpClient.SendTimeout = _config.SendTimeoutMs;
                _stream = _tcpClient.GetStream();

                if (_config.EnableKeepAlive)
                    _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                if (!await PerformHandshakeAsync(localNode, linkedCts.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("FINS/TCP握手失败");

                Interlocked.Exchange(ref _isConnected, 1);
                Interlocked.Exchange(ref _reconnectAttempts, 0);

                _logger.Information($"PLC连接成功 - 本地节点: {_clientNode}, 远程节点: {_serverNode}");
                ConnectionStateChanged?.Invoke(this, true);

                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("连接操作被取消");
                await CleanupAsync().ConfigureAwait(false);
                ScheduleReconnect();
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"连接失败: {ex.Message}");
                await CleanupAsync().ConfigureAwait(false);
                ScheduleReconnect();
                return false;
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        private async Task<bool> PerformHandshakeAsync(byte requestedNode, CancellationToken cancellationToken)
        {
            _logger.Information("开始 FINS/TCP 握手...");

            byte[] request = new byte[20];
            request[0] = 0x46; request[1] = 0x49; request[2] = 0x4E; request[3] = 0x53;
            request[4] = 0x00; request[5] = 0x00; request[6] = 0x00; request[7] = 0x0C;
            request[8] = 0x00; request[9] = 0x00; request[10] = 0x00; request[11] = 0x00;
            request[12] = 0x00; request[13] = 0x00; request[14] = 0x00; request[15] = 0x00;
            request[16] = 0x00; request[17] = 0x00; request[18] = 0x00; request[19] = requestedNode;

            _logger.Information($"发送握手命令: {BitConverter.ToString(request)}");
            await _stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

            byte[] response = await ReceiveExactAsync(24, cancellationToken).ConfigureAwait(false);
            _logger.Information($"收到握手响应: {BitConverter.ToString(response)}");

            for (int i = 0; i < 4; i++)
            {
                if (response[i] != FINS_HEADER_BYTES[i])
                {
                    _logger.Error($"FINS头不匹配: 期望{FINS_HEADER_BYTES[i]:X2}, 实际{response[i]:X2}");
                    return false;
                }
            }

            uint command = (uint)((response[8] << 24) | (response[9] << 16) | (response[10] << 8) | response[11]);
            if (command != 0x00000001)
            {
                _logger.Error($"握手响应Command异常: 期望0x00000001, 实际0x{command:X8}");
                return false;
            }

            uint errorCode = (uint)((response[12] << 24) | (response[13] << 16) | (response[14] << 8) | response[15]);
            if (errorCode != 0)
            {
                _logger.Error($"握手响应ErrorCode异常: {errorCode}");
                return false;
            }

            _clientNode = response[19];
            _serverNode = response[23];

            _logger.Information($"握手成功 - 本地节点(SA1):{_clientNode} (0x{_clientNode:X2}), 远程节点(DA1):{_serverNode} (0x{_serverNode:X2})");
            return true;
        }

        public async Task DisconnectAsync()
        {
            await _asyncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsConnected)
                {
                    _logger.Warning("已经处于断开状态");
                    return;
                }

                await CleanupAsync().ConfigureAwait(false);
                ConnectionStateChanged?.Invoke(this, false);
                _logger.Information("PLC 已主动断开连接");
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        private async Task CleanupAsync()
        {
            Interlocked.Exchange(ref _isConnected, 0);

            if (_stream != null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }

            if (_tcpClient != null)
            {
                _tcpClient.Close();
                _tcpClient.Dispose();
                _tcpClient = null;
            }

            _clientNode = 0;
            _serverNode = 0;
            _reconnectTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            Interlocked.Exchange(ref _reconnectAttempts, 0);
        }

        private void ScheduleReconnect()
        {
            if (_config.AutoReconnect && _reconnectTimer != null)
                _reconnectTimer.Change(_config.ReconnectDelayMs, Timeout.Infinite);
        }

        private async void ReconnectCallback(object state)
        {
            if (_disposed == 1) return;

            try
            {
                int currentAttempt = Interlocked.Increment(ref _reconnectAttempts);

                if (currentAttempt <= _config.MaxReconnectAttempts)
                {
                    _logger.Information($"正在尝试重连 ({currentAttempt}/{_config.MaxReconnectAttempts})...");
                    await Task.Delay(_config.ReconnectDelayMs).ConfigureAwait(false);

                    if (await ConnectAsync(_serverIP, _serverPort, _clientNode, CancellationToken.None).ConfigureAwait(false))
                    {
                        Interlocked.Exchange(ref _reconnectAttempts, 0);
                        _counters.IncrementReconnect();
                        _logger.Information("重连成功");
                    }
                    else
                    {
                        _reconnectTimer?.Change(_config.ReconnectDelayMs, Timeout.Infinite);
                    }
                }
                else
                {
                    _logger.Warning($"达到最大重连次数 {_config.MaxReconnectAttempts}，停止重连");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "重连异常");
            }
        }

        private async Task HandleConnectionLostAsync()
        {
            if (!_config.AutoReconnect || Interlocked.CompareExchange(ref _isConnected, 0, 1) != 1)
                return;

            ConnectionStateChanged?.Invoke(this, false);
            await CleanupAsync().ConfigureAwait(false);
            _reconnectTimer?.Change(_config.ReconnectDelayMs, Timeout.Infinite);
        }

        #endregion

        #region 底层数据收发

        private async Task<byte[]> ReceiveExactAsync(int count, CancellationToken cancellationToken)
        {
            byte[] buffer = _bufferPool.Rent(count);
            int received = 0;

            try
            {
                using var timeoutCts = new CancellationTokenSource(_config.ReceiveTimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutCts.Token, _disposeCts.Token);

                while (received < count)
                {
                    int read = await _stream.ReadAsync(buffer.AsMemory(received, count - received), linkedCts.Token).ConfigureAwait(false);
                    if (read == 0)
                        throw new EndOfStreamException("连接已关闭");

                    received += read;
                    LogSampled($"接收进度: {received}/{count} 字节");
                }

                byte[] result = new byte[count];
                Array.Copy(buffer, 0, result, 0, count);
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"接收超时，已接收: {received}/{count} 字节");
            }
            finally
            {
                _bufferPool.Return(buffer);
            }
        }

        private bool IsRetryableError(int errorCode) =>
            errorCode == ErrorCode.Timeout || errorCode == ErrorCode.NetworkError;

        #endregion

        #region 命令构建

        private byte[] BuildCommandPacket(OmronAddressType type, int address, int param,
            bool isBit, bool isRead, int bit = 0, byte[] data = null)
        {
            byte areaCode = GetAreaCode(type, isBit);
            int dataLength = data?.Length ?? 0;

            int finsCommandLength = FINS_HEADER_LEN + FINS_COMMAND_CODE_LEN + FINS_PARAM_LEN + dataLength;
            int tcpPayloadLength = FINS_TCP_COMMAND_LEN + FINS_TCP_ERROR_LEN + finsCommandLength;
            int totalLength = FINS_TCP_HEADER_LEN + tcpPayloadLength;

            byte[] packet = new byte[totalLength];
            int idx = 0;

            FINS_HEADER_BYTES.CopyTo(packet, idx);
            idx += 4;

            packet[idx++] = (byte)((tcpPayloadLength >> 24) & 0xFF);
            packet[idx++] = (byte)((tcpPayloadLength >> 16) & 0xFF);
            packet[idx++] = (byte)((tcpPayloadLength >> 8) & 0xFF);
            packet[idx++] = (byte)(tcpPayloadLength & 0xFF);

            packet[idx++] = 0x00; packet[idx++] = 0x00; packet[idx++] = 0x00; packet[idx++] = 0x02;
            packet[idx++] = 0x00; packet[idx++] = 0x00; packet[idx++] = 0x00; packet[idx++] = 0x00;

            packet[idx++] = ICF_COMMAND;
            packet[idx++] = RSV_DEFAULT;
            packet[idx++] = _config.GatewayCount;
            packet[idx++] = DNA_LOCAL;
            packet[idx++] = _serverNode;
            packet[idx++] = DA2_CPU_UNIT;
            packet[idx++] = SNA_LOCAL;
            packet[idx++] = _clientNode;
            packet[idx++] = SA2_HOST;
            packet[idx++] = _config.ServiceId;

            packet[idx++] = MRC_MEMORY_AREA;
            packet[idx++] = isRead ? SRC_READ : SRC_WRITE;

            packet[idx++] = areaCode;
            packet[idx++] = (byte)((address >> 8) & 0xFF);
            packet[idx++] = (byte)(address & 0xFF);
            packet[idx++] = (byte)(isBit ? bit : 0x00);
            packet[idx++] = (byte)((param >> 8) & 0xFF);
            packet[idx++] = (byte)(param & 0xFF);

            if (data != null && data.Length > 0)
                Array.Copy(data, 0, packet, idx, data.Length);

            LogDebug($"构建命令: {(isRead ? "读取" : "写入")}, 区域={type}, 地址={address}, 位={bit}, 数量={param}");

            return packet;
        }

        #endregion

        #region 命令执行

        private async Task<Result> ExecuteCommandAsync(byte[] request, CancellationToken cancellationToken)
        {
            if (!IsConnected)
            {
                if (_config.AutoReconnect) _ = HandleConnectionLostAsync();
                return Result.Fail(ErrorCode.NotConnected, "PLC未连接");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _counters.IncrementCommands();

            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _disposeCts.Token);
                linkedCts.CancelAfter(_config.ReceiveTimeoutMs);

                LogVerbose($"发送命令 ({request.Length}字节): {BitConverter.ToString(request)}");
                await _stream.WriteAsync(request, linkedCts.Token).ConfigureAwait(false);
                _counters.AddBytesSent(request.Length);

                byte[] finsFrame;
                byte[] tcpPayload = null;
                byte[] tcpHeader = await ReceiveExactAsync(FINS_TCP_HEADER_LEN, linkedCts.Token).ConfigureAwait(false);

                bool isFinsTcpHeader = true;
                for (int i = 0; i < 4; i++)
                {
                    if (tcpHeader[i] != FINS_HEADER_BYTES[i])
                    {
                        isFinsTcpHeader = false;
                        break;
                    }
                }

                if (isFinsTcpHeader)
                {
                    int tcpPayloadLength = (tcpHeader[4] << 24) | (tcpHeader[5] << 16) | (tcpHeader[6] << 8) | tcpHeader[7];
                    if (tcpPayloadLength < 8)
                        return Result.Fail(ErrorCode.ProtocolError, $"TCP载荷长度异常: {tcpPayloadLength}");

                    tcpPayload = await ReceiveExactAsync(tcpPayloadLength, linkedCts.Token).ConfigureAwait(false);
                    _counters.AddBytesReceived(FINS_TCP_HEADER_LEN + tcpPayloadLength);

                    uint command = (uint)((tcpPayload[0] << 24) | (tcpPayload[1] << 16) | (tcpPayload[2] << 8) | tcpPayload[3]);
                    if (command != FINS_TCP_COMMAND_RECV && command != FINS_TCP_COMMAND_SEND)
                        _logger.Warning($"响应 Command 异常: 期望 {FINS_TCP_COMMAND_RECV:X8} 或 {FINS_TCP_COMMAND_SEND:X8}, 实际 {command:X8}");

                    uint errorCode = (uint)((tcpPayload[4] << 24) | (tcpPayload[5] << 16) | (tcpPayload[6] << 8) | tcpPayload[7]);
                    if (errorCode != FINS_TCP_ERROR_OK)
                        return Result.Fail(ErrorCode.ProtocolError, $"PLC 返回 ErrorCode: {errorCode}");

                    finsFrame = new byte[tcpPayload.Length - 8];
                    Array.Copy(tcpPayload, 8, finsFrame, 0, finsFrame.Length);
                }
                else
                {
                    _logger.Debug("检测到原始 FINS 帧响应 (无 FINS/TCP 头)");

                    using var memoryStream = new MemoryStream();
                    memoryStream.Write(tcpHeader, 0, tcpHeader.Length);

                    byte[] remainingBuffer = new byte[2048];
                    int totalRead = tcpHeader.Length;

                    while (totalRead < 2048)
                    {
                        try
                        {
                            using var readCts = new CancellationTokenSource(100);
                            using var linkedReadCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, readCts.Token);

                            int read = await _stream.ReadAsync(remainingBuffer.AsMemory(0, Math.Min(remainingBuffer.Length, 2048 - totalRead)), linkedReadCts.Token).ConfigureAwait(false);
                            if (read == 0) break;

                            memoryStream.Write(remainingBuffer, 0, read);
                            totalRead += read;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }

                    finsFrame = memoryStream.ToArray();
                    _counters.AddBytesReceived(finsFrame.Length);
                }

                LogVerbose($"FINS 响应帧 ({finsFrame.Length}字节): {BitConverter.ToString(finsFrame)}");

                // ⭐⭐⭐ 添加调试输出 ⭐⭐⭐
                Debug.WriteLine($"[ExecuteCommandAsync] finsFrame总长度: {finsFrame.Length}");
                Debug.WriteLine($"[ExecuteCommandAsync] 完整响应帧: {BitConverter.ToString(finsFrame)}");

                if (finsFrame.Length < FINS_MIN_RESPONSE_LEN)
                    return Result.Fail(ErrorCode.ProtocolError, $"FINS帧长度不足: {finsFrame.Length} < {FINS_MIN_RESPONSE_LEN}");

                if (finsFrame.Length > 9)
                {
                    byte responseSid = finsFrame[9];
                    if (responseSid != _config.ServiceId)
                        _logger.Warning($"SID 不匹配: 期望 {_config.ServiceId:X2}, 实际 {responseSid:X2}");
                }

                if (finsFrame[0] != ICF_RESPONSE)
                    _logger.Warning($"ICF 异常: 期望 {ICF_RESPONSE:X2}, 实际 {finsFrame[0]:X2}");

                byte mres = finsFrame[12];
                byte sres = finsFrame[13];
                LogDebug($"响应码: MRES=0x{mres:X2}, SRES=0x{sres:X2}");

                // ⭐⭐⭐ 添加调试输出 ⭐⭐⭐
                Debug.WriteLine($"[ExecuteCommandAsync] MRES=0x{mres:X2}, SRES=0x{sres:X2}");

                bool isSuccess = (mres == 0 && sres == 0);

                if (!isSuccess)
                {
                    if ((mres & 0x80) != 0)
                    {
                        _logger.Warning($"网络中继错误 - MRES:0x{mres:X2}, SRES:0x{sres:X2}");
                        if (finsFrame.Length >= 16)
                        {
                            byte errorNetwork = finsFrame[14];
                            byte errorNode = finsFrame[15];
                            _logger.Warning($"错误位置 - 网络:{errorNetwork}, 节点:{errorNode}");
                        }
                        return Result.Fail(ErrorCode.NetworkError, GetErrorMessage(mres, sres));
                    }

                    _logger.Warning($"PLC返回错误: {GetErrorMessage(mres, sres)}");
                    return Result.Fail(ErrorCode.PlcError, GetErrorMessage(mres, sres));
                }

                stopwatch.Stop();
                _counters.RecordResponseTime(stopwatch.ElapsedMilliseconds);

                byte[] data = null;
                if (finsFrame.Length > 14)
                {
                    data = new byte[finsFrame.Length - 14];
                    Array.Copy(finsFrame, 14, data, 0, data.Length);
                }

                // ⭐⭐⭐ 添加调试输出 ⭐⭐⭐
                Debug.WriteLine($"[ExecuteCommandAsync] 提取数据长度: {data?.Length ?? 0}");
                if (data != null && data.Length > 0)
                {
                    Debug.WriteLine($"[ExecuteCommandAsync] 数据内容: {BitConverter.ToString(data)}");
                }

                DataTraced?.Invoke(this, new FinsTraceEventArgs(request, finsFrame, data, stopwatch.Elapsed));
                return Result.Ok(data);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                _counters.IncrementFailedCommands();
                _logger.Warning($"FINS命令超时 ({_config.ReceiveTimeoutMs}ms)");
                return Result.Fail(ErrorCode.Timeout, "操作超时");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _counters.IncrementFailedCommands();
                _logger.Error(ex, "FINS命令执行异常");

                if (ex is SocketException se && (se.SocketErrorCode == SocketError.ConnectionReset ||
                                                  se.SocketErrorCode == SocketError.ConnectionAborted))
                {
                    _ = HandleConnectionLostAsync();
                }

                return Result.Fail(ErrorCode.NetworkError, $"网络错误: {ex.Message}");
            }
        }

        private async Task<Result> ExecuteCommandWithRetryAsync(byte[] request, CancellationToken cancellationToken)
        {
            for (int i = 0; i < MAX_RETRY_COUNT; i++)
            {
                var result = await ExecuteCommandAsync(request, cancellationToken).ConfigureAwait(false);

                if (result.Success)
                    return result;

                if (IsRetryableError(result.ErrorCode) && i < MAX_RETRY_COUNT - 1)
                {
                    _logger.Warning($"命令失败，第 {i + 1} 次重试... 错误: {result.Message}");
                    await Task.Delay(RETRY_BASE_DELAY_MS * (i + 1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return result;
            }

            return Result.Fail(ErrorCode.NetworkError, $"执行失败，已重试 {MAX_RETRY_COUNT} 次");
        }

        #endregion

        #region 公开读写方法

        public async Task<Result<bool>> ReadBitAsync(OmronAddressType type, int address, int bit, CancellationToken cancellationToken = default)
        {
            if (!ValidateAddress(type, address, bit))
                return Result<bool>.Fail(ErrorCode.InvalidParameter, $"地址 {address} 或位 {bit} 超出范围");

            var packet = BuildCommandPacket(type, address, 1, isBit: true, isRead: true, bit: bit);
            var result = await ExecuteCommandWithRetryAsync(packet, cancellationToken).ConfigureAwait(false);

            if (result.Success && result.Data?.Length > 0)
                return Result<bool>.Ok(result.Data[0] != 0);

            return Result<bool>.Fail(result.ErrorCode, result.Message);
        }

        public async Task<Result> WriteBitAsync(OmronAddressType type, int address, int bit, bool value, CancellationToken cancellationToken = default)
        {
            if (!ValidateAddress(type, address, bit))
                return Result.Fail(ErrorCode.InvalidParameter, $"地址 {address} 或位 {bit} 超出范围");

            var packet = BuildCommandPacket(type, address, 1, isBit: true, isRead: false, bit: bit, data: new[] { (byte)(value ? 1 : 0) });
            return await ExecuteCommandWithRetryAsync(packet, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Result<short[]>> ReadWordsAsync(OmronAddressType type, int address, int count, CancellationToken cancellationToken = default)
        {
            if (!ValidateAddress(type, address))
                return Result<short[]>.Fail(ErrorCode.InvalidParameter, $"地址 {address} 超出 {type} 范围");

            if (count < 1 || count > _config.MaxWordReadLength)
                return Result<short[]>.Fail(ErrorCode.InvalidParameter, $"读取字数必须在 1~{_config.MaxWordReadLength} 之间");

            var packet = BuildCommandPacket(type, address, count, isBit: false, isRead: true);
            var result = await ExecuteCommandWithRetryAsync(packet, cancellationToken).ConfigureAwait(false);

            if (result.Success && result.Data != null)
            {
                if (result.Data.Length < count * 2)
                    return Result<short[]>.Fail(ErrorCode.ProtocolError, $"响应数据长度不足: 期望 {count * 2} 字节, 实际 {result.Data.Length} 字节");

                var words = new short[count];
                for (int i = 0; i < count; i++)
                    words[i] = (short)((result.Data[i * 2] << 8) | result.Data[i * 2 + 1]);

                return Result<short[]>.Ok(words);
            }

            return Result<short[]>.Fail(result.ErrorCode, result.Message);
        }

        public async Task<Result> WriteWordsAsync(OmronAddressType type, int address, short[] words, CancellationToken cancellationToken = default)
        {
            if (!ValidateAddress(type, address))
                return Result.Fail(ErrorCode.InvalidParameter, $"地址 {address} 超出 {type} 范围");

            if (words == null || words.Length == 0)
                return Result.Fail(ErrorCode.InvalidParameter, "数据不能为空");

            if (words.Length > _config.MaxWordWriteLength)
                return Result.Fail(ErrorCode.InvalidParameter, $"写入字数超过最大限制 {_config.MaxWordWriteLength}");

            byte[] data = new byte[words.Length * 2];
            for (int i = 0; i < words.Length; i++)
            {
                data[i * 2] = (byte)(words[i] >> 8);
                data[i * 2 + 1] = (byte)words[i];
            }

            var packet = BuildCommandPacket(type, address, words.Length, isBit: false, isRead: false, data: data);
            return await ExecuteCommandWithRetryAsync(packet, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Result<byte[]>> ReadRawBytesAsync(OmronAddressType type, int address, int byteCount, CancellationToken cancellationToken = default)
        {
            if (!ValidateAddress(type, address))
                return Result<byte[]>.Fail(ErrorCode.InvalidParameter, $"地址 {address} 超出 {type} 范围");

            int wordCount = (byteCount + 1) / 2;

            if (wordCount < 1 || wordCount > _config.MaxWordReadLength)
                return Result<byte[]>.Fail(ErrorCode.InvalidParameter, $"读取字数必须在 1~{_config.MaxWordReadLength} 之间");

            var packet = BuildCommandPacket(type, address, wordCount, isBit: false, isRead: true);
            var result = await ExecuteCommandWithRetryAsync(packet, cancellationToken).ConfigureAwait(false);

            if (result.Success && result.Data != null)
            {
                if (result.Data.Length < byteCount)
                    return Result<byte[]>.Fail(ErrorCode.ProtocolError, $"响应数据长度不足: 期望 {byteCount} 字节, 实际 {result.Data.Length} 字节");

                byte[] bytes = new byte[byteCount];
                Array.Copy(result.Data, bytes, byteCount);
                return Result<byte[]>.Ok(bytes);
            }

            return Result<byte[]>.Fail(result.ErrorCode, result.Message);
        }

        public async Task<Result<byte[]>> ReadWordsRawAsync(OmronAddressType type, int address, int wordCount, CancellationToken cancellationToken = default)
        {
            Debug.WriteLine($"[ReadWordsRawAsync] ========== 开始 ==========");
            Debug.WriteLine($"[ReadWordsRawAsync] 区域: {type}, 地址: {address}, 字数: {wordCount}");

            if (!ValidateAddress(type, address))
            {
                Debug.WriteLine($"[ReadWordsRawAsync] 地址验证失败");
                return Result<byte[]>.Fail(ErrorCode.InvalidParameter, $"地址 {address} 超出 {type} 范围");
            }

            if (wordCount < 1 || wordCount > _config.MaxWordReadLength)
            {
                Debug.WriteLine($"[ReadWordsRawAsync] 字数超出范围: {wordCount} (最大: {_config.MaxWordReadLength})");
                return Result<byte[]>.Fail(ErrorCode.InvalidParameter, $"读取字数必须在 1~{_config.MaxWordReadLength} 之间");
            }

            var packet = BuildCommandPacket(type, address, wordCount, isBit: false, isRead: true);
            Debug.WriteLine($"[ReadWordsRawAsync] 发送命令: {BitConverter.ToString(packet)}");

            var result = await ExecuteCommandWithRetryAsync(packet, cancellationToken).ConfigureAwait(false);

            Debug.WriteLine($"[ReadWordsRawAsync] 执行结果: Success={result.Success}, ErrorCode={result.ErrorCode}, Message={result.Message}");
            Debug.WriteLine($"[ReadWordsRawAsync] Data 是否为 null: {result.Data == null}");
            Debug.WriteLine($"[ReadWordsRawAsync] Data 长度: {result.Data?.Length ?? 0}");

            if (result.Data != null && result.Data.Length > 0)
            {
                Debug.WriteLine($"[ReadWordsRawAsync] Data 内容: {BitConverter.ToString(result.Data)}");
            }
            Debug.WriteLine($"[ReadWordsRawAsync] ========== 结束 ==========");

            if (result.Success && result.Data != null)
                return Result<byte[]>.Ok(result.Data);

            return Result<byte[]>.Fail(result.ErrorCode, result.Message);
        }

        public async Task<Result> WriteRawBytesAsync(OmronAddressType type, int address, byte[] data, CancellationToken cancellationToken = default)
        {
            if (!ValidateAddress(type, address))
                return Result.Fail(ErrorCode.InvalidParameter, $"地址 {address} 超出 {type} 范围");

            if (data == null || data.Length == 0)
                return Result.Fail(ErrorCode.InvalidParameter, "数据不能为空");

            byte[] writeData = data;
            if (data.Length % 2 != 0)
            {
                writeData = new byte[data.Length + 1];
                Array.Copy(data, writeData, data.Length);
            }

            int wordCount = writeData.Length / 2;

            if (wordCount > _config.MaxWordWriteLength)
                return Result.Fail(ErrorCode.InvalidParameter, $"写入字数超过最大限制 {_config.MaxWordWriteLength}");

            var packet = BuildCommandPacket(type, address, wordCount, isBit: false, isRead: false, data: writeData);
            return await ExecuteCommandWithRetryAsync(packet, cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region 泛型读写

        public async Task<Result<T>> ReadAsync<T>(OmronAddressType type, int address, CancellationToken cancellationToken = default) where T : struct
        {
            try
            {
                if (typeof(T) == typeof(bool))
                {
                    var result = await ReadBitAsync(type, address, 0, cancellationToken).ConfigureAwait(false);
                    return result.Success
                        ? Result<T>.Ok((T)(object)result.Value)
                        : Result<T>.Fail(result.ErrorCode, result.Message);
                }

                int byteCount = typeof(T) switch
                {
                    Type t when t == typeof(byte) || t == typeof(sbyte) => 1,
                    Type t when t == typeof(short) || t == typeof(ushort) => 2,
                    Type t when t == typeof(int) || t == typeof(uint) || t == typeof(float) => 4,
                    Type t when t == typeof(long) || t == typeof(ulong) || t == typeof(double) => 8,
                    _ => throw new NotSupportedException($"不支持的类型: {typeof(T).Name}")
                };

                var wordsResult = await ReadWordsAsync(type, address, (byteCount + 1) / 2, cancellationToken).ConfigureAwait(false);
                if (!wordsResult.Success)
                    return Result<T>.Fail(wordsResult.ErrorCode, wordsResult.Message);

                byte[] bytes = new byte[byteCount];
                for (int i = 0; i < wordsResult.Value.Length && i * 2 < byteCount; i++)
                {
                    bytes[i * 2] = (byte)(wordsResult.Value[i] >> 8);
                    if (i * 2 + 1 < byteCount)
                        bytes[i * 2 + 1] = (byte)wordsResult.Value[i];
                }

                T value = typeof(T) switch
                {
                    Type t when t == typeof(byte) => (T)(object)bytes[0],
                    Type t when t == typeof(sbyte) => (T)(object)(sbyte)bytes[0],
                    Type t when t == typeof(short) => (T)(object)(short)((bytes[0] << 8) | bytes[1]),
                    Type t when t == typeof(ushort) => (T)(object)(ushort)((bytes[0] << 8) | bytes[1]),
                    Type t when t == typeof(int) => (T)(object)(int)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]),
                    Type t when t == typeof(uint) => (T)(object)(uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]),
                    Type t when t == typeof(long) => (T)(object)(long)(((long)bytes[0] << 56) | ((long)bytes[1] << 48) | ((long)bytes[2] << 40) | ((long)bytes[3] << 32) | ((long)bytes[4] << 24) | ((long)bytes[5] << 16) | ((long)bytes[6] << 8) | bytes[7]),
                    Type t when t == typeof(ulong) => (T)(object)(((ulong)bytes[0] << 56) | ((ulong)bytes[1] << 48) | ((ulong)bytes[2] << 40) | ((ulong)bytes[3] << 32) | ((ulong)bytes[4] << 24) | ((ulong)bytes[5] << 16) | ((ulong)bytes[6] << 8) | bytes[7]),
                    Type t when t == typeof(float) => (T)(object)BitConverter.Int32BitsToSingle((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]),
                    Type t when t == typeof(double) => (T)(object)BitConverter.Int64BitsToDouble((((long)bytes[0] << 56) | ((long)bytes[1] << 48) | ((long)bytes[2] << 40) | ((long)bytes[3] << 32) | ((long)bytes[4] << 24) | ((long)bytes[5] << 16) | ((long)bytes[6] << 8) | bytes[7])),
                    _ => default
                };

                return Result<T>.Ok(value);
            }
            catch (Exception ex)
            {
                _counters.IncrementFailedCommands();
                return Result<T>.Fail(ErrorCode.InternalError, $"读取异常: {ex.Message}");
            }
        }

        public async Task<Result> WriteAsync<T>(OmronAddressType type, int address, T value, CancellationToken cancellationToken = default) where T : struct
        {
            try
            {
                if (typeof(T) == typeof(bool))
                    return await WriteBitAsync(type, address, 0, Convert.ToBoolean(value), cancellationToken).ConfigureAwait(false);

                byte[] bytes = typeof(T) switch
                {
                    Type t when t == typeof(byte) => new[] { Convert.ToByte(value) },
                    Type t when t == typeof(sbyte) => new[] { (byte)Convert.ToSByte(value) },
                    Type t when t == typeof(short) => BitConverter.GetBytes(Convert.ToInt16(value)),
                    Type t when t == typeof(ushort) => BitConverter.GetBytes(Convert.ToUInt16(value)),
                    Type t when t == typeof(int) => BitConverter.GetBytes(Convert.ToInt32(value)),
                    Type t when t == typeof(uint) => BitConverter.GetBytes(Convert.ToUInt32(value)),
                    Type t when t == typeof(long) => BitConverter.GetBytes(Convert.ToInt64(value)),
                    Type t when t == typeof(ulong) => BitConverter.GetBytes(Convert.ToUInt64(value)),
                    Type t when t == typeof(float) => BitConverter.GetBytes(Convert.ToSingle(value)),
                    Type t when t == typeof(double) => BitConverter.GetBytes(Convert.ToDouble(value)),
                    _ => throw new NotSupportedException($"不支持的类型: {typeof(T).Name}")
                };

                if (BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);

                short[] words = new short[(bytes.Length + 1) / 2];
                for (int i = 0; i < words.Length; i++)
                {
                    int high = i * 2 < bytes.Length ? bytes[i * 2] : 0;
                    int low = i * 2 + 1 < bytes.Length ? bytes[i * 2 + 1] : 0;
                    words[i] = (short)((high << 8) | low);
                }

                return await WriteWordsAsync(type, address, words, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _counters.IncrementFailedCommands();
                return Result.Fail(ErrorCode.InternalError, $"写入异常: {ex.Message}");
            }
        }

        #endregion

        #region 字符串读写

        public async Task<Result<string>> ReadStringAsync(OmronAddressType type, int address, int length, CancellationToken cancellationToken = default)
        {
            if (!ValidateAddress(type, address))
                return Result<string>.Fail(ErrorCode.InvalidParameter, $"地址 {address} 超出 {type} 范围");

            if (length < 1 || length > 240)
                return Result<string>.Fail(ErrorCode.InvalidParameter, "长度必须在 1~240 之间");

            int wordCount = (length + 1) / 2;
            var result = await ReadWordsAsync(type, address, wordCount, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
                return Result<string>.Fail(result.ErrorCode, result.Message);

            byte[] bytes = new byte[length];
            for (int i = 0; i < wordCount && i * 2 < length; i++)
            {
                bytes[i * 2] = (byte)(result.Value[i] >> 8);
                if (i * 2 + 1 < length)
                    bytes[i * 2 + 1] = (byte)result.Value[i];
            }

            string str = Encoding.ASCII.GetString(bytes).TrimEnd('\0');
            return Result<string>.Ok(str);
        }

        public async Task<Result> WriteStringAsync(OmronAddressType type, int address, string value, int maxLength, CancellationToken cancellationToken = default)
        {
            if (!ValidateAddress(type, address))
                return Result.Fail(ErrorCode.InvalidParameter, $"地址 {address} 超出 {type} 范围");

            if (maxLength < 1 || maxLength > 240)
                return Result.Fail(ErrorCode.InvalidParameter, "长度必须在 1~240 之间");

            string processed = (value ?? string.Empty).PadRight(maxLength, '\0');
            if (processed.Length > maxLength)
                processed = processed.Substring(0, maxLength);

            byte[] bytes = Encoding.ASCII.GetBytes(processed);
            short[] words = new short[(bytes.Length + 1) / 2];

            for (int i = 0; i < words.Length; i++)
            {
                int high = i * 2 < bytes.Length ? bytes[i * 2] : 0;
                int low = i * 2 + 1 < bytes.Length ? bytes[i * 2 + 1] : 0;
                words[i] = (short)((high << 8) | low);
            }

            return await WriteWordsAsync(type, address, words, cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region 错误处理

        private string GetErrorMessage(byte mainCode, byte subCode)
        {
            return (mainCode, subCode) switch
            {
                (0x00, 0x00) => "正常完成",
                (0x00, 0x01) => "服务已取消",
                (0x01, 0x01) => "本地节点不在网络中",
                (0x01, 0x02) => "令牌超时",
                (0x01, 0x03) => "重试失败",
                (0x01, 0x04) => "发送帧过多",
                (0x01, 0x05) => "节点地址范围错误",
                (0x01, 0x06) => "节点地址重复",
                (0x02, 0x01) => "目标节点不在网络中",
                (0x02, 0x02) => "目标单元不存在",
                (0x02, 0x03) => "第三节点不存在",
                (0x02, 0x04) => "目标节点忙",
                (0x02, 0x05) => "响应超时",
                (0x03, 0x01) => "通信控制器错误",
                (0x03, 0x02) => "目标CPU单元错误",
                (0x03, 0x03) => "控制器错误",
                (0x03, 0x04) => "单元号错误",
                (0x04, 0x01) => "未定义的命令",
                (0x04, 0x02) => "型号/版本不支持",
                (0x05, 0x01) => "目标地址设置错误",
                (0x05, 0x02) => "路由表不存在",
                (0x05, 0x03) => "路由表错误",
                (0x05, 0x04) => "中继过多",
                (0x10, 0x01) => "命令太长",
                (0x10, 0x02) => "命令太短",
                (0x10, 0x03) => "数据与长度不匹配",
                (0x10, 0x04) => "命令格式错误",
                (0x10, 0x05) => "头部错误",
                (0x11, 0x01) => "区域类型错误",
                (0x11, 0x02) => "访问大小错误",
                (0x11, 0x03) => "地址范围错误",
                (0x11, 0x04) => "地址超出范围",
                (0x11, 0x06) => "程序不存在",
                (0x11, 0x0B) => "响应太长",
                (0x11, 0x0C) => "参数错误",
                (0x20, 0x02) => "数据受保护",
                (0x20, 0x03) => "表格不存在",
                (0x20, 0x04) => "数据不存在",
                (0x20, 0x05) => "程序不存在",
                (0x20, 0x06) => "文件不存在",
                (0x20, 0x07) => "数据不匹配",
                (0x21, 0x01) => "只读区域",
                (0x21, 0x02) => "受保护区域",
                (0x21, 0x03) => "无法注册",
                (0x22, 0x03) => "PLC模式错误(编程模式)",
                (0x22, 0x04) => "PLC模式错误(监视模式)",
                (0x22, 0x05) => "PLC模式错误(运行模式)",
                (0x23, 0x01) => "文件设备不存在",
                (0x23, 0x02) => "内存不存在",
                (0x23, 0x03) => "无时钟",
                (0x24, 0x01) => "数据链接表错误",
                (0x25, 0x02) => "内存错误",
                (0x25, 0x03) => "I/O设置错误",
                (0x25, 0x04) => "I/O点过多",
                (0x25, 0x05) => "CPU总线错误",
                (0x25, 0x06) => "I/O重复",
                (0x25, 0x07) => "I/O总线错误",
                (0x26, 0x01) => "区域未保护",
                (0x26, 0x02) => "密码错误",
                (0x26, 0x04) => "区域受保护",
                (0x30, 0x01) => "访问权被其他设备持有",
                (0x40, 0x01) => "命令被中止",
                _ => $"未知错误 [{mainCode:X2} {subCode:X2}]"
            };
        }

        #endregion

        #region 资源释放

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _disposeCts.Cancel();
            _disposeCts.Dispose();
            _reconnectTimer?.Dispose();

            await DisconnectAsync().ConfigureAwait(false);
            _asyncLock.Dispose();
        }

        #endregion
    }

    #region 配置类

    public class FinsTcpConfig
    {
        public int HandshakeTimeoutMs { get; set; } = 5000;
        public int ReceiveTimeoutMs { get; set; } = 3000;
        public int SendTimeoutMs { get; set; } = 3000;
        public int MaxWordReadLength { get; set; } = 1000;
        public int MaxWordWriteLength { get; set; } = 1000;
        public bool AutoReconnect { get; set; } = true;
        public int ReconnectDelayMs { get; set; } = 5000;
        public int MaxReconnectAttempts { get; set; } = 10;
        public bool EnableKeepAlive { get; set; } = true;
        public byte GatewayCount { get; set; } = 0x02;
        public byte ServiceId { get; set; } = 0x01;
        public LogLevel LogLevel { get; set; } = LogLevel.Information;
    }

    #endregion

    #region 其他类型

    public enum LogLevel
    {
        Error = 0,
        Warning = 1,
        Information = 2,
        Debug = 3,
        Verbose = 4
    }

    public class FinsPerformanceCounters
    {
        private long _totalCommands;
        private long _failedCommands;
        private long _bytesSent;
        private long _bytesReceived;
        private long _minResponseTimeMs = long.MaxValue;
        private long _maxResponseTimeMs;
        private long _totalResponseTimeMs;
        private long _reconnectCount;

        public void IncrementCommands() => Interlocked.Increment(ref _totalCommands);
        public void IncrementFailedCommands() => Interlocked.Increment(ref _failedCommands);
        public void AddBytesSent(int bytes) => Interlocked.Add(ref _bytesSent, bytes);
        public void AddBytesReceived(int bytes) => Interlocked.Add(ref _bytesReceived, bytes);
        public void IncrementReconnect() => Interlocked.Increment(ref _reconnectCount);

        public void RecordResponseTime(long ms)
        {
            Interlocked.Add(ref _totalResponseTimeMs, ms);

            long currentMin;
            do
            {
                currentMin = _minResponseTimeMs;
                if (ms >= currentMin) break;
            }
            while (Interlocked.CompareExchange(ref _minResponseTimeMs, ms, currentMin) != currentMin);

            long currentMax;
            do
            {
                currentMax = _maxResponseTimeMs;
                if (ms <= currentMax) break;
            }
            while (Interlocked.CompareExchange(ref _maxResponseTimeMs, ms, currentMax) != currentMax);
        }

        public long TotalCommands => Interlocked.Read(ref _totalCommands);
        public long FailedCommands => Interlocked.Read(ref _failedCommands);
        public long BytesSent => Interlocked.Read(ref _bytesSent);
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);
        public long ReconnectCount => Interlocked.Read(ref _reconnectCount);
        public long MinResponseTimeMs => Interlocked.Read(ref _minResponseTimeMs) == long.MaxValue ? 0 : Interlocked.Read(ref _minResponseTimeMs);
        public long MaxResponseTimeMs => Interlocked.Read(ref _maxResponseTimeMs);
        public double AverageResponseTimeMs => TotalCommands == 0 ? 0 : (double)_totalResponseTimeMs / TotalCommands;
        public double SuccessRate => TotalCommands == 0 ? 100 : (TotalCommands - FailedCommands) * 100.0 / TotalCommands;
    }

    public class Result
    {
        public bool Success { get; }
        public int ErrorCode { get; }
        public string Message { get; }
        public byte[] Data { get; }

        protected Result(bool success, int errorCode = 0, string message = null, byte[] data = null)
        {
            Success = success;
            ErrorCode = errorCode;
            Message = message;
            Data = data;
        }

        public static Result Ok(byte[] data = null) => new Result(true, 0, null, data);
        public static Result Fail(int errorCode, string message) => new Result(false, errorCode, message);
    }

    public class Result<T> : Result
    {
        public T Value { get; }

        private Result(bool success, T value, int errorCode = 0, string message = null)
            : base(success, errorCode, message)
        {
            Value = value;
        }

        public static Result<T> Ok(T value) => new Result<T>(true, value);
        public static Result<T> Fail(int errorCode, string message) => new Result<T>(false, default, errorCode, message);
    }

    public static class ErrorCode
    {
        public const int Success = 0;
        public const int NotConnected = 1000;
        public const int InvalidParameter = 1001;
        public const int Timeout = 1002;
        public const int NetworkError = 1003;
        public const int ProtocolError = 1004;
        public const int PlcError = 1005;
        public const int InternalError = 1006;
    }

    public class FinsTraceEventArgs : EventArgs
    {
        public byte[] Request { get; }
        public byte[] Response { get; }
        public byte[] Data { get; }
        public TimeSpan Elapsed { get; }

        public FinsTraceEventArgs(byte[] request, byte[] response, byte[] data, TimeSpan elapsed)
        {
            Request = request;
            Response = response;
            Data = data;
            Elapsed = elapsed;
        }
    }

    public enum OmronAddressType
    {
        CIO = 0,
        WR = 1,
        DM = 2,
        HR = 3,
        TIM = 4,
        AR = 5,
        CNT = 6
    }

    #endregion
}