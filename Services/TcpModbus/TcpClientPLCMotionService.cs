using Microsoft.Extensions.Logging;
using System.IO;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using static GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs.DeviceSettings;


namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus
{
    /// <summary>
    /// TCP Modbus PLC动作控制客户端通信服务 - 支持中断操作
    /// </summary>
    public class TcpClientPLCMotionService : ITcpClientPLCMotionService, IAsyncDisposable, IDisposable
    {
        /// <summary>
        /// 连接状态枚举
        /// </summary>
        public enum ConnectionState
        {
            Disconnected = 0,    // 未连接
            Connected = 1,       // 已连接
            Reconnecting = 2     // 重连中
        }

        /// <summary>
        /// 日志记录器
        /// </summary>
        private readonly ILogger<TcpClientPLCMotionService> _logger;

        /// <summary>
        /// 设备设置服务
        /// </summary>
        private readonly IDeviceSettingsService _settingsService;

        /// <summary>
        /// 当前TCP客户端PLC动作控制设置
        /// </summary>
        private TcpClientPLCMotionControlSettings? _currentSettings;

        /// <summary>
        /// TCP客户端
        /// </summary>
        private TcpClient? _tcpClient;

        /// <summary>
        /// 网络流
        /// </summary>
        private NetworkStream? _networkStream;

        /// <summary>
        /// 心跳检测定时器任务
        /// </summary>
        private Task? _healthCheckTask;

        /// <summary>
        /// 同步锁信号量
        /// </summary>
        private readonly SemaphoreSlim _syncLock = new(1, 1);

        /// <summary>
        /// 存储待处理响应的字典，键为事务ID
        /// </summary>
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<ModbusResponse>> _pendingResponses = new();

        /// <summary>
        /// 待处理字符串响应任务完成源
        /// </summary>
        private readonly object _stringResponseLock = new object();
        private TaskCompletionSource<string>? _pendingStringResponseTcs;

        /// <summary>
        /// 读取循环取消令牌源
        /// </summary>
        private CancellationTokenSource? _readLoopCts;

        /// <summary>
        /// 心跳检测取消令牌源
        /// </summary>
        private CancellationTokenSource? _heartbeatCts;

        /// <summary>
        /// 最后数据接收时间戳
        /// </summary>
        private long _lastDataReceivedTicks = DateTime.UtcNow.Ticks;

        /// <summary>
        /// 当前事务ID计数器 - 使用原子操作
        /// </summary>
        private volatile int _transactionIdCounter = 0;

        /// <summary>
        /// 服务运行状态
        /// </summary>
        private volatile bool _isRunning = false;

        /// <summary>
        /// 服务实际连接状态 - 由内部逻辑精确控制，不依赖TcpClient.Connected
        /// </summary>
        private volatile bool _isConnected = false;

        /// <summary>
        /// 是否正在重连中 - 使用int代替bool以支持Interlocked操作
        /// </summary>
        private volatile int _isReconnecting = 0; // 0 = false, 1 = true

        /// <summary>
        /// 接收缓冲区，用于处理粘包/分包
        /// </summary>
        private readonly MemoryStream _receiveBuffer = new();

        /// <summary>
        /// 连接超时时间（毫秒），默认3000ms
        /// </summary>
        public int? ConnectTimeoutMsDefault { get; set; } = 3000;

        /// <summary>
        /// 获取当前TCP连接是否已成功建立 - 由内部状态控制，更可靠
        /// </summary>
        public bool IsConnected => _isConnected;

        /// <summary>
        /// 获取当前服务是否正在运行
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// 当发生通信相关事件时触发的通知事件
        /// </summary>
        public event EventHandler<CommunicationNotification>? OnNotification;

        /// <summary>
        /// 当接收到Modbus响应时触发
        /// </summary>
        public event EventHandler<ModbusResponse>? ModbusResponseReceived;

        /// <summary>
        /// 当接收到原始数据时触发（仅用于非Modbus数据）
        /// </summary>
        public event EventHandler<byte[]>? RawDataReceived;

        /// <summary>
        /// 连接状态变更事件 - true 表示连接成功，false 表示断开
        /// </summary>
        public event Action<bool>? ConnectionStateChanged;

        /// <summary>
        /// 详细连接状态变更事件 - 提供更详细的连接状态信息
        /// </summary>
        public event Action<ConnectionState>? DetailedConnectionStateChanged;

        /// <summary>
        /// 初始化一个新的TCP Modbus客户端服务实例
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="settingsService">设置服务，用于加载TCP客户端PLC动作控制设置</param>
        public TcpClientPLCMotionService(
            ILogger<TcpClientPLCMotionService> logger,
            IDeviceSettingsService settingsService)
        {
            _logger = logger;
            _settingsService = settingsService;
        }

        /// <summary>
        /// 获取下一个事务ID - 线程安全的原子操作
        /// </summary>
        /// <returns>下一个可用的事务ID</returns>
        private ushort GetNextTransactionId()
        {
            var next = Interlocked.Increment(ref _transactionIdCounter);
            return (ushort)(next == 0 ? 1 : next); // 跳过 0
        }

        /// <summary>
        /// 获取当前有效的TCP客户端PLC动作控制通信配置。若未加载，则从设置服务中读取。
        /// </summary>
        /// <returns>TCP客户端PLC动作控制通信设置对象</returns>
        private TcpClientPLCMotionControlSettings GetCurrentSettings()
        {
            if (_currentSettings == null)
            {
                var appSettings = _settingsService.LoadSettings();
                _currentSettings = appSettings.TcpClientPLCMotion;
            }
            return _currentSettings;
        }

        /// <summary>
        /// 🔥 新增：清理连接资源
        /// </summary>
        private void CleanupConnection()
        {
            try
            {
                _networkStream?.Dispose();
                _tcpClient?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "清理连接资源时出现异常（可忽略）");
            }
            finally
            {
                _networkStream = null;
                _tcpClient = null;
                _receiveBuffer.SetLength(0);
                _isConnected = false;
            }
        }

        /// <summary>
        /// 异步启动TCP客户端：加载配置、连接到服务器、启动数据接收循环和心跳检测机制
        /// </summary>
        /// <returns>表示异步启动操作的任务</returns>
        public async Task StartAsync()
        {
            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isRunning)
                {
                    _logger.LogWarning("服务已在运行中，无法重复启动");
                    return;
                }

                _isRunning = true;
                var settings = GetCurrentSettings();

                _logger.LogInformation("开始启动Modbus TCP客户端...");

                try
                {
                    // 尝试连接到服务器
                    await ConnectToServerAsync(settings).ConfigureAwait(false);

                    // 关键修复：明确设置内部连接状态为 true，并触发事件
                    _isConnected = true;
                    ConnectionStateChanged?.Invoke(true);
                    DetailedConnectionStateChanged?.Invoke(ConnectionState.Connected);

                    // 启动心跳检测
                    if (settings.HealthCheckMode != HealthCheckMode.Disabled)
                    {
                        _heartbeatCts = new CancellationTokenSource();
                        _healthCheckTask = Task.Run(() => StartHealthCheckLoop(_heartbeatCts.Token), _heartbeatCts.Token);
                    }

                    // 启动数据接收循环
                    _readLoopCts = new CancellationTokenSource();
                    _ = Task.Run(() => ReadDataAsync(_readLoopCts.Token), _readLoopCts.Token);

                    _logger.LogInformation($"Modbus TCP客户端已连接到 {settings.Host}:{settings.Port}");
                    Notify(NotificationType.Success, $"已连接到 {settings.Host}:{settings.Port}", "Start");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "启动Modbus TCP客户端失败");
                    Notify(NotificationType.Critical, $"启动失败: {ex.Message}", "Start");

                    // 🔥 关键：确保完全清理
                    _isRunning = false;
                    CleanupConnection();

                    // 触发连接失败事件
                    ConnectionStateChanged?.Invoke(false);
                    DetailedConnectionStateChanged?.Invoke(ConnectionState.Disconnected);

                    throw; // 重新抛出，让上层知道失败
                }
            }
            finally
            {
                _syncLock.Release();
            }
        }

        /// <summary>
        /// 启动心跳检测循环
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>表示异步心跳检测循环的任务</returns>
        private async Task StartHealthCheckLoop(CancellationToken ct)
        {
            var settings = GetCurrentSettings();
            var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(settings.HealthCheckIntervalSeconds));

            try
            {
                while (!ct.IsCancellationRequested && await periodicTimer.WaitForNextTickAsync(ct))
                {
                    await HealthCheckAsync();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("心跳检测循环被取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "心跳检测循环异常");
            }
        }

        /// <summary>
        /// 异步连接到服务器
        /// </summary>
        /// <param name="settings">当前设置</param>
        /// <returns>表示异步连接操作的任务</returns>
        private async Task ConnectToServerAsync(TcpClientPLCMotionControlSettings settings)
        {
            int retryDelayMs = settings.ReconnectDelayMs ?? 2000;
            int connectTimeoutMs = ConnectTimeoutMsDefault ?? 3000;
            int attempts = 0;
            int maxRetries = settings.MaxReconnectAttempts ?? 12;

            // 🔥 关键：确保开始新连接前清理旧资源
            CleanupConnection();

            while (_isRunning && attempts < maxRetries)
            {
                TcpClient? tempTcpClient = null;
                try
                {
                    _logger.LogInformation($"尝试连接到 {settings.Host}:{settings.Port} (尝试 {attempts + 1}/{maxRetries})");

                    tempTcpClient = new TcpClient();
                    tempTcpClient.ReceiveTimeout = settings.ReceiveTimeoutMs ?? 5000;
                    tempTcpClient.SendTimeout = settings.SendTimeoutMs ?? 5000;

                    // 🔥 关键：添加超时控制
                    var connectTask = tempTcpClient.ConnectAsync(settings.Host, settings.Port);
                    var timeoutTask = Task.Delay(connectTimeoutMs);

                    var completedTask = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                    if (completedTask == connectTask)
                    {
                        await connectTask.ConfigureAwait(false);
                        _tcpClient = tempTcpClient;
                        _networkStream = _tcpClient.GetStream();

                        _logger.LogInformation("TCP连接建立成功（实际耗时 < {0}ms）", connectTimeoutMs);
                        return;
                    }

                    // 连接超时
                    tempTcpClient?.Close();
                    tempTcpClient?.Dispose();
                    tempTcpClient = null;

                    throw new TimeoutException($"连接超时（{connectTimeoutMs}ms）");
                }
                catch (Exception ex)
                {
                    tempTcpClient?.Close();
                    tempTcpClient?.Dispose();

                    attempts++;

                    var errorMsg = ex is TimeoutException ? "连接超时" : "连接失败";
                    _logger.LogWarning(ex, $"{errorMsg} (尝试 {attempts}/{maxRetries})");

                    if (!_isRunning)
                    {
                        _logger.LogInformation("服务已停止，取消重连");
                        CleanupConnection();
                        throw; // 服务停止，抛出异常
                    }

                    if (attempts < maxRetries)
                    {
                        _logger.LogInformation($"等待 {retryDelayMs}ms 后重试...");
                        await Task.Delay(retryDelayMs).ConfigureAwait(false);
                    }
                    else
                    {
                        // 🔥 关键：所有重试都失败时，清理资源
                        CleanupConnection();
                        throw new InvalidOperationException($"连接失败，已达到最大重试次数 ({maxRetries})");
                    }
                }
            }

            CleanupConnection();
            throw new InvalidOperationException($"连接失败，已达到最大重试次数 ({maxRetries})");
        }

        /// <summary>
        /// 异步读取来自PLC的数据
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>表示异步读取操作的任务</returns>
        private async Task ReadDataAsync(CancellationToken ct)
        {
            var buffer = new byte[4096]; // 增大缓冲区以处理大数据包
            try
            {
                _logger.LogInformation("开始Modbus数据接收循环");

                while (!ct.IsCancellationRequested && _isConnected && _isRunning)
                {
                    if (_networkStream == null)
                    {
                        _logger.LogWarning("网络流为null，无法读取数据");
                        break;
                    }

                    try
                    {
                        int bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        if (bytesRead > 0)
                        {
                            _logger.LogDebug($"接收到 {bytesRead} 字节数据");

                            // 将新数据添加到接收缓冲区
                            _receiveBuffer.Write(buffer, 0, bytesRead);

                            // 处理缓冲区中的完整报文
                            ProcessBufferedData();
                        }
                        else
                        {
                            // 连接已关闭
                            _logger.LogInformation("TCP连接已关闭，停止读取循环");
                            break;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.LogDebug("网络流已被释放");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Modbus TCP读取任务被取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取Modbus数据失败");
                if (!ct.IsCancellationRequested && _isRunning)
                {
                    Notify(NotificationType.Error, "数据读取异常，尝试重连", "ReadData");
                    await HandleConnectionLossAsync().ConfigureAwait(false);
                }
            }

            _logger.LogInformation("Modbus数据接收循环结束");
        }

        /// <summary>
        /// 处理接收缓冲区中的数据，尝试解析完整的Modbus报文
        /// </summary>
        private void ProcessBufferedData()
        {
            var bufferArray = _receiveBuffer.ToArray();
            int offset = 0;

            while (offset < bufferArray.Length)
            {
                // 尝试解析Modbus响应
                var response = TryParseModbusResponse(bufferArray, offset, bufferArray.Length - offset);

                if (response != null)
                {
                    _logger.LogDebug($"成功解析Modbus响应: 事务ID={response.TransactionId}, 功能码={response.FunctionCode}");
                    HandleModbusResponse(response);

                    // 移动偏移量到下一个可能的报文
                    var responseLength = CalculateModbusResponseLength(bufferArray, offset, bufferArray.Length - offset);
                    if (responseLength > 0)
                    {
                        offset += responseLength;
                    }
                    else
                    {
                        // 无法计算响应长度，可能是解析错误，跳过一个字节
                        offset++;
                    }
                }
                else
                {
                    // 无法解析为Modbus响应，检查是否是字符串数据
                    // 如果缓冲区中有换行符或其他分隔符，可以尝试解析为字符串
                    var rawData = TryParseRawData(bufferArray, offset, bufferArray.Length - offset);
                    if (rawData != null)
                    {
                        RawDataReceived?.Invoke(this, rawData);
                        offset += rawData.Length;
                    }
                    else
                    {
                        // 没有找到完整数据包，保留剩余数据
                        break;
                    }
                }
            }

            // 清理已处理的数据
            if (offset > 0)
            {
                var remainingData = new byte[bufferArray.Length - offset];
                Array.Copy(bufferArray, offset, remainingData, 0, remainingData.Length);
                _receiveBuffer.SetLength(0);
                _receiveBuffer.Write(remainingData, 0, remainingData.Length);
            }
        }

        /// <summary>
        /// 计算Modbus响应的完整长度
        /// </summary>
        /// <param name="data">数据数组</param>
        /// <param name="offset">偏移量</param>
        /// <param name="length">可用长度</param>
        /// <returns>Modbus响应的完整长度</returns>
        private int CalculateModbusResponseLength(byte[] data, int offset, int length)
        {
            if (length < 6) return 0; // MBAP头至少6字节

            var pduLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4));
            var totalLength = 6 + pduLength; // MBAP头(6字节) + PDU

            return totalLength;
        }

        /// <summary>
        /// 尝试解析Modbus响应报文
        /// </summary>
        /// <param name="data">接收到的数据</param>
        /// <param name="offset">数据偏移量</param>
        /// <param name="length">数据长度</param>
        /// <returns>解析后的Modbus响应对象</returns>
        private ModbusResponse? TryParseModbusResponse(byte[] data, int offset, int length)
        {
            try
            {
                if (length < 9) // 最小Modbus TCP响应长度
                {
                    _logger.LogDebug($"响应数据长度不足: {length}, 需要至少9字节");
                    return null;
                }

                // 解析MBAP头
                var transactionId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 0));
                var protocolId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
                var pduLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4));

                if (pduLength < 2) // PDU至少包含Unit ID和Function Code
                {
                    _logger.LogDebug($"PDU长度无效: {pduLength}");
                    return null;
                }

                var totalExpectedLength = 6 + pduLength; // MBAP头(6字节) + PDU
                if (length < totalExpectedLength)
                {
                    _logger.LogDebug($"响应数据不完整: 期望 {totalExpectedLength} 字节, 实际 {length} 字节");
                    return null;
                }

                var response = new ModbusResponse
                {
                    TransactionId = transactionId,
                    ProtocolId = protocolId,
                    Length = pduLength,
                    UnitId = data[offset + 6],
                    FunctionCode = data[offset + 7]
                };

                // 检查是否为错误响应
                if ((response.FunctionCode & 0x80) != 0)
                {
                    if (length >= offset + 9) // 确保有足够的数据获取错误码
                    {
                        response.IsError = true;
                        response.ErrorCode = data[offset + 8];
                    }
                    else
                    {
                        _logger.LogDebug("错误响应数据不完整");
                        return null;
                    }
                    return response;
                }

                // 根据功能码决定如何解析 PDU
                switch (response.FunctionCode)
                {
                    case 0x01: // 读线圈
                    case 0x02: // 读离散输入
                    case 0x03: // 读保持寄存器
                    case 0x04: // 读输入寄存器
                        // 响应格式: [Unit][FC][ByteCount][Data...]
                        if (length < offset + 9)
                        {
                            _logger.LogDebug("读响应数据不完整，无法读取ByteCount");
                            return null;
                        }
                        response.ByteCount = data[offset + 8];
                        if (length < offset + 9 + response.ByteCount)
                        {
                            _logger.LogDebug($"读响应数据不完整: 期望 {9 + response.ByteCount} 字节, 实际 {length - offset} 字节");
                            return null;
                        }
                        response.Data = new byte[response.ByteCount];
                        Array.Copy(data, offset + 9, response.Data, 0, response.ByteCount);
                        break;

                    case 0x05: // 写单线圈
                    case 0x06: // 写单寄存器
                        // 响应格式: [Unit][FC][起始地址高][起始地址低][值高][值低] → 共 6 字节 PDU
                        if (length < offset + 12) // 6 (MBAP) + 6 (PDU) = 12
                        {
                            _logger.LogDebug("写单操作响应数据不完整，期望12字节");
                            return null;
                        }
                        // 提取地址和值用于验证
                        var addr = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8));
                        var val = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 10));
                        response.Data = new byte[4];
                        BinaryPrimitives.WriteUInt16BigEndian(response.Data.AsSpan(0), addr);
                        BinaryPrimitives.WriteUInt16BigEndian(response.Data.AsSpan(2), val);
                        break;

                    case 0x0F: // 写多线圈
                    case 0x10: // 写多寄存器
                        // 响应格式: [Unit][FC][起始地址高][起始地址低][写入数量高][写入数量低]
                        if (length < offset + 12) // 6 (MBAP) + 6 (PDU) = 12
                        {
                            _logger.LogDebug("写多操作响应数据不完整");
                            return null;
                        }
                        // 提取地址和数量用于验证
                        var startAddr = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8));
                        var quantity = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 10));
                        response.Data = new byte[4];
                        BinaryPrimitives.WriteUInt16BigEndian(response.Data.AsSpan(0), startAddr);
                        BinaryPrimitives.WriteUInt16BigEndian(response.Data.AsSpan(2), quantity);
                        break;

                    default:
                        _logger.LogWarning("不支持的功能码响应: {FunctionCode}", response.FunctionCode);
                        // 对于不支持的功能码，尝试按通用格式解析
                        if (pduLength > 2)
                        {
                            response.Data = new byte[pduLength - 2]; // 除了Unit ID和FC外的所有数据
                            Array.Copy(data, offset + 8, response.Data, 0, response.Data.Length);
                        }
                        break;
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析Modbus响应时发生异常");
                return null;
            }
        }

        /// <summary>
        /// 尝试解析原始数据（用于非Modbus数据）
        /// </summary>
        /// <param name="data">数据数组</param>
        /// <param name="offset">偏移量</param>
        /// <param name="length">长度</param>
        /// <returns>原始数据字节数组</returns>
        private byte[]? TryParseRawData(byte[] data, int offset, int length)
        {
            // 在Modbus场景下，通常不处理非Modbus数据
            // 如果确实需要处理特定格式的数据（如以\r\n结尾的文本），请在此处添加逻辑
            // 例如，检查是否以\r\n结尾

            // 临时禁用原始数据处理以避免干扰Modbus协议
            return null;
        }

        /// <summary>
        /// 解析Modbus响应报文
        /// </summary>
        /// <param name="data">接收到的数据</param>
        /// <param name="offset">数据偏移量</param>
        /// <param name="length">数据长度</param>
        /// <returns>解析后的Modbus响应对象</returns>
        private ModbusResponse? ParseModbusResponse(byte[] data, int offset, int length)
        {
            return TryParseModbusResponse(data, offset, length);
        }

        /// <summary>
        /// 处理Modbus响应 - 使用事务ID匹配
        /// </summary>
        /// <param name="response">Modbus响应对象</param>
        private void HandleModbusResponse(ModbusResponse response)
        {
            Interlocked.Exchange(ref _lastDataReceivedTicks, DateTime.UtcNow.Ticks);

            // 首先尝试通过事务ID找到对应的等待任务
            if (_pendingResponses.TryRemove(response.TransactionId, out var tcs))
            {
                // 设置结果
                tcs.TrySetResult(response);

                _logger.LogDebug($"成功匹配事务ID {response.TransactionId} 的响应");
                return;
            }

            // 如果没有找到匹配的等待任务，触发通用事件
            ModbusResponseReceived?.Invoke(this, response);
        }

        /// <summary>
        /// 处理字符串响应
        /// </summary>
        /// <param name="data">接收到的原始数据</param>
        private void HandleStringResponse(byte[] data)
        {
            Interlocked.Exchange(ref _lastDataReceivedTicks, DateTime.UtcNow.Ticks);

            var responseString = Encoding.UTF8.GetString(data);

            lock (_stringResponseLock)
            {
                var tcs = _pendingStringResponseTcs;
                if (tcs != null && !tcs.Task.IsCompleted)
                {
                    tcs.TrySetResult(responseString);
                    _pendingStringResponseTcs = null; // 清除引用
                }
            }
        }

        /// <summary>
        /// 强制中断当前连接
        /// </summary>
        public void InterruptConnection()
        {
            _logger.LogInformation("强制中断当前连接");
            _networkStream?.Close();
            _tcpClient?.Close();
            Notify(NotificationType.Warning, "连接被强制中断", "InterruptConnection");
        }

        /// <summary>
        /// 强制中断所有正在进行的操作
        /// </summary>
        public void InterruptAllOperations()
        {
            _logger.LogInformation("强制中断所有正在进行的操作");

            // 中断所有待处理的Modbus响应任务
            foreach (var kvp in _pendingResponses)
            {
                if (!kvp.Value.Task.IsCompleted)
                {
                    kvp.Value.TrySetCanceled();
                }
            }
            _pendingResponses.Clear();

            // 中断待处理的字符串响应任务
            lock (_stringResponseLock)
            {
                if (_pendingStringResponseTcs != null && !_pendingStringResponseTcs.Task.IsCompleted)
                {
                    _pendingStringResponseTcs.TrySetCanceled();
                    _pendingStringResponseTcs = null;
                }
            }

            Notify(NotificationType.Warning, "所有操作被强制中断", "InterruptAllOperations");
        }

        /// <summary>
        /// 处理连接丢失，尝试重连
        /// </summary>
        /// <returns>表示异步重连操作的任务</returns>
        private async Task HandleConnectionLossAsync()
        {
            if (!_isRunning) return; // 如果服务已停止，不再重连

            // 防止重复重连 - 使用int 0/1模拟bool
            if (Interlocked.CompareExchange(ref _isReconnecting, 1, 0) == 1)
            {
                _logger.LogDebug("重连已在进行中，跳过");
                return;
            }

            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _logger.LogInformation("开始处理连接丢失");

                // 关键修复：明确设置内部连接状态为 false，并触发事件
                if (_isConnected)
                {
                    _isConnected = false;
                    ConnectionStateChanged?.Invoke(false);
                    DetailedConnectionStateChanged?.Invoke(ConnectionState.Disconnected);
                }

                // 中断所有待处理的响应
                InterruptAllOperations();

                // 清理连接资源
                CleanupConnection();

                var settings = GetCurrentSettings();
                int retryDelayMs = settings.ReconnectDelayMs ?? 2000;
                int attempts = 0;
                int maxRetries = settings.MaxReconnectAttempts ?? 10; // 使用设置中的最大重试次数

                // 重连循环
                while (_isRunning && attempts < maxRetries)
                {
                    try
                    {
                        _logger.LogInformation($"尝试重连 ({attempts + 1}/{maxRetries})...");
                        DetailedConnectionStateChanged?.Invoke(ConnectionState.Reconnecting);

                        await ConnectToServerAsync(settings).ConfigureAwait(false);

                        // 重连成功，启动数据接收循环
                        _readLoopCts?.Cancel();
                        _readLoopCts?.Dispose();
                        _readLoopCts = new CancellationTokenSource();
                        _ = Task.Run(() => ReadDataAsync(_readLoopCts.Token), _readLoopCts.Token);

                        // 关键修复：重连成功后，明确设置内部连接状态为 true，并触发事件
                        _isConnected = true;
                        ConnectionStateChanged?.Invoke(true);
                        DetailedConnectionStateChanged?.Invoke(ConnectionState.Connected);

                        _logger.LogInformation("重连成功");
                        Notify(NotificationType.Success, "重连成功", "Reconnect");
                        return; // 成功连接后退出重连循环
                    }
                    catch (Exception ex)
                    {
                        attempts++;
                        _logger.LogWarning(ex, "重连失败 ({Attempts}/{MaxRetries})，等待 {RetryDelayMs}ms", attempts, maxRetries, retryDelayMs);

                        if (!_isRunning)
                        {
                            _logger.LogError("服务已停止，连接失败");
                            Notify(NotificationType.Critical, "服务已停止，连接失败", "Reconnect");
                            return;
                        }

                        if (attempts < maxRetries)
                        {
                            await Task.Delay(retryDelayMs).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogError("重连失败，已达到最大重试次数");
                            Notify(NotificationType.Critical, "重连失败，已达到最大重试次数", "Reconnect");
                            CleanupConnection();
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isReconnecting, 0); // 重置为 false (0)
                _syncLock.Release();
            }
        }

        /// <summary>
        /// 异步停止TCP客户端：关闭连接，取消后台读取任务，释放所有资源
        /// </summary>
        /// <returns>表示异步停止操作的任务</returns>
        public async Task StopAsync()
        {
            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _isRunning = false;

                // 安全地取消和等待心跳任务
                if (_heartbeatCts != null)
                {
                    _heartbeatCts.Cancel();
                    if (_healthCheckTask != null)
                    {
                        try
                        {
                            await Task.WhenAny(_healthCheckTask, Task.Delay(1000));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "等待心跳任务结束时出错");
                        }
                    }
                }

                _heartbeatCts?.Dispose();
                _heartbeatCts = null;
                _healthCheckTask = null;

                _readLoopCts?.Cancel();
                _readLoopCts?.Dispose();
                _readLoopCts = null;

                // 中断所有待处理的响应
                InterruptAllOperations();

                CleanupConnection();

                // 关键修复：停止时明确设置连接状态为 false，并触发事件
                _isConnected = false;
                ConnectionStateChanged?.Invoke(false);
                DetailedConnectionStateChanged?.Invoke(ConnectionState.Disconnected);

                _logger.LogInformation("Modbus TCP客户端已停止");
                Notify(NotificationType.Info, "Modbus TCP客户端已停止", "Stop");
            }
            finally
            {
                _syncLock.Release();
            }
        }

        /// <summary>
        /// 创建Modbus TCP请求报文
        /// </summary>
        /// <param name="functionCode">功能码</param>
        /// <param name="unitId">单元ID</param>
        /// <param name="startAddress">起始地址</param>
        /// <param name="quantity">数量</param>
        /// <param name="transactionId">事务ID（由调用方提供）</param>
        /// <returns>Modbus TCP请求报文字节</returns>
        private byte[] CreateModbusTcpRequest(byte functionCode, byte unitId, ushort startAddress, ushort quantity, ushort transactionId)
        {
            // 根据功能码创建不同的PDU
            byte[] request;
            if (functionCode == 0x01) // 读取线圈
            {
                request = ModbusTcpMessageHelper.CreateReadCoilsRequest(transactionId, unitId, startAddress, quantity);
            }
            else if (functionCode == 0x03) // 读取保持寄存器
            {
                request = ModbusTcpMessageHelper.CreateReadHoldingRegistersRequest(transactionId, unitId, startAddress, quantity);
            }
            else
            {
                throw new ArgumentException($"不支持的功能码: {functionCode}", nameof(functionCode));
            }
            return request;
        }

        /// <summary>
        /// 创建Modbus TCP写入请求报文
        /// </summary>
        /// <param name="functionCode">功能码</param>
        /// <param name="unitId">单元ID</param>
        /// <param name="startAddress">起始地址</param>
        /// <param name="data">要写入的数据</param>
        /// <param name="transactionId">事务ID（由调用方提供）</param>
        /// <returns>Modbus TCP写入请求报文字节</returns>
        private byte[] CreateModbusTcpWriteRequest(byte functionCode, byte unitId, ushort startAddress, ushort[] data, ushort transactionId)
        {
            return ModbusTcpMessageHelper.CreateWriteMultipleRegistersRequest(transactionId, unitId, startAddress, data);
        }

        /// <summary>
        /// 创建写单个寄存器的Modbus TCP请求报文
        /// </summary>
        /// <param name="unitId">单元ID</param>
        /// <param name="startAddress">起始地址</param>
        /// <param name="value">要写入的值</param>
        /// <param name="transactionId">事务ID（由调用方提供）</param>
        /// <returns>Modbus TCP写单个寄存器请求报文字节</returns>
        private byte[] CreateModbusTcpWriteSingleRequest(byte unitId, ushort startAddress, ushort value, ushort transactionId)
        {
            return ModbusTcpMessageHelper.CreateWriteSingleRegisterRequest(transactionId, unitId, startAddress, value);
        }

        /// <summary>
        /// 执行Modbus读取操作 - 使用事务ID匹配响应
        /// </summary>
        /// <param name="functionCode">功能码</param>
        /// <param name="unitId">单元ID</param>
        /// <param name="startAddress">起始地址</param>
        /// <param name="quantity">数量</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>Modbus响应对象</returns>
        public async Task<ModbusResponse?> ExecuteReadOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort quantity, int timeoutMs = 3000)
        {
            if (!IsConnected || !_isRunning)
            {
                _logger.LogWarning("TCP未连接或服务已停止，无法执行读取操作");
                Notify(NotificationType.Error, "TCP未连接或服务已停止", "ExecuteReadOperation");
                return null;
            }

            // ✅ 关键修复：只生成一次事务ID
            var transactionId = GetNextTransactionId();
            var tcs = new TaskCompletionSource<ModbusResponse>();
            _pendingResponses[transactionId] = tcs;

            ModbusResponse? response = null;
            try
            {
                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);

                // ✅ 传入已生成的 transactionId
                byte[] request = CreateModbusTcpRequest(functionCode, unitId, startAddress, quantity, transactionId);
                _logger.LogDebug($"发送Modbus请求: 事务ID={transactionId}, 功能码={functionCode}, 从站地址={unitId}, 起始地址={startAddress}, 数量={quantity}");
                await SendRawDataAsync(request).ConfigureAwait(false);

                var resultTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
                if (resultTask == tcs.Task)
                {
                    response = await tcs.Task.ConfigureAwait(false);
                    if (response != null)
                    {
                        if (response.IsError)
                        {
                            _logger.LogWarning($"Modbus错误: {response.ErrorCode}");
                            Notify(NotificationType.Warning, $"Modbus错误: {response.ErrorCode}", "ExecuteReadOperation");
                        }
                        else
                        {
                            _logger.LogDebug($"读取操作成功: 返回数据长度={response.Data?.Length ?? 0}");
                            Notify(NotificationType.Success, "读取操作成功", "ExecuteReadOperation");
                        }
                    }
                }
                else if (timeoutCts.Token.IsCancellationRequested)
                {
                    _logger.LogWarning("操作被中断");
                    Notify(NotificationType.Warning, "操作被中断", "ExecuteReadOperation");
                    _pendingResponses.TryRemove(transactionId, out _);
                }
                else
                {
                    _logger.LogWarning("等待响应超时");
                    Notify(NotificationType.Warning, "等待响应超时", "ExecuteReadOperation");
                    _pendingResponses.TryRemove(transactionId, out _);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("读取操作被取消");
                Notify(NotificationType.Warning, "读取操作被取消", "ExecuteReadOperation");
                _pendingResponses.TryRemove(transactionId, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行读取操作时出错");
                Notify(NotificationType.Error, $"执行失败: {ex.Message}", "ExecuteReadOperation");
                _pendingResponses.TryRemove(transactionId, out _);
            }
            return response;
        }

        /// <summary>
        /// 执行Modbus写入操作 - 使用事务ID匹配响应
        /// </summary>
        /// <param name="functionCode">功能码</param>
        /// <param name="unitId">单元ID</param>
        /// <param name="startAddress">起始地址</param>
        /// <param name="data">要写入的数据</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>Modbus响应对象</returns>
        public async Task<ModbusResponse?> ExecuteWriteOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort[] data, int timeoutMs = 3000)
        {
            if (!IsConnected || !_isRunning)
            {
                Notify(NotificationType.Error, "TCP未连接或服务已停止", "ExecuteWriteOperation");
                return null;
            }

            // ✅ 关键修复：只生成一次事务ID
            var transactionId = GetNextTransactionId();
            var tcs = new TaskCompletionSource<ModbusResponse>();
            _pendingResponses[transactionId] = tcs;

            ModbusResponse? response = null;
            try
            {
                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);

                // 根据功能码创建相应的写入请求
                byte[] request;
                if (functionCode == 0x06) // 写单个寄存器
                {
                    if (data.Length != 1)
                        throw new ArgumentException("写单个寄存器时数据长度必须为1", nameof(data));
                    request = CreateModbusTcpWriteSingleRequest(unitId, startAddress, data[0], transactionId);
                    _logger.LogDebug($"发送写单个寄存器请求: 事务ID={transactionId}, 功能码={functionCode}, 从站地址={unitId}, 地址={startAddress}, 值={data[0]}");
                }
                else if (functionCode == 0x10) // 写多个寄存器
                {
                    request = CreateModbusTcpWriteRequest(functionCode, unitId, startAddress, data, transactionId);
                    _logger.LogDebug($"发送写多个寄存器请求: 事务ID={transactionId}, 功能码={functionCode}, 从站地址={unitId}, 起始地址={startAddress}, 数据={string.Join(",", data)}");
                }
                else if (functionCode == 0x05) // 写单个线圈
                {
                    if (data.Length != 1)
                        throw new ArgumentException("写单个线圈时数据长度必须为1");
                    bool value = data[0] == 0xFF00;
                    request = ModbusTcpMessageHelper.CreateWriteSingleCoilRequest(
                        transactionId, unitId, startAddress, value);
                }
                else
                {
                    throw new ArgumentException($"不支持的功能码: {functionCode}", nameof(functionCode));
                }

                await SendRawDataAsync(request).ConfigureAwait(false);

                var resultTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
                if (resultTask == tcs.Task)
                {
                    response = await tcs.Task.ConfigureAwait(false);
                    if (response != null)
                    {
                        // 验证事务ID是否匹配
                        if (response.TransactionId == transactionId)
                        {
                            if (response.IsError)
                            {
                                _logger.LogWarning($"Modbus写入错误: 事务ID={response.TransactionId}, 错误码={response.ErrorCode}");
                                Notify(NotificationType.Warning, $"Modbus写入错误: {response.ErrorCode}", "ExecuteWriteOperation");
                            }
                            else
                            {
                                _logger.LogDebug($"写入操作成功: 事务ID={response.TransactionId}, 功能码={response.FunctionCode}, 从站地址={response.UnitId}");
                                Notify(NotificationType.Success, "写入操作成功", "ExecuteWriteOperation");
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"事务ID不匹配: 期望={transactionId}, 实际={response.TransactionId}");
                            Notify(NotificationType.Warning, "响应事务ID不匹配", "ExecuteWriteOperation");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("接收到空响应");
                        Notify(NotificationType.Warning, "接收到空响应", "ExecuteWriteOperation");
                    }
                }
                else if (timeoutCts.Token.IsCancellationRequested)
                {
                    _logger.LogWarning("写入操作被中断");
                    Notify(NotificationType.Warning, "操作被中断", "ExecuteWriteOperation");
                    _pendingResponses.TryRemove(transactionId, out _);
                }
                else
                {
                    _logger.LogWarning("等待写入响应超时");
                    Notify(NotificationType.Warning, "等待响应超时", "ExecuteWriteOperation");
                    _pendingResponses.TryRemove(transactionId, out _);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("写入操作被取消");
                Notify(NotificationType.Warning, "写入操作被取消", "ExecuteWriteOperation");
                _pendingResponses.TryRemove(transactionId, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行写入操作时出错");
                Notify(NotificationType.Error, $"执行失败: {ex.Message}", "ExecuteWriteOperation");
                _pendingResponses.TryRemove(transactionId, out _);
            }
            return response;
        }

        /// <summary>
        /// 异步发送原始数据到PLC
        /// </summary>
        /// <param name="data">要发送的数据</param>
        /// <returns>表示异步发送操作的任务</returns>
        private async Task SendRawDataAsync(byte[] data)
        {
            if (!IsConnected || _networkStream == null || !_isRunning)
            {
                _logger.LogWarning("无法发送数据: TCP未连接或服务已停止");
                return;
            }

            try
            {
                _logger.LogDebug($"发送 {data.Length} 字节的Modbus请求: {BitConverter.ToString(data)}");
                await _networkStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                await _networkStream.FlushAsync().ConfigureAwait(false);

                _logger.LogDebug($"已发送 {data.Length} 字节的Modbus请求");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送Modbus请求失败");
                Notify(NotificationType.Error, $"发送失败: {ex.Message}", "SendRawData");
            }
        }

        /// <summary>
        /// 执行心跳检测
        /// </summary>
        /// <returns>表示异步心跳检测操作的任务</returns>
        private async Task HealthCheckAsync()
        {
            if (!_isRunning || _heartbeatCts?.IsCancellationRequested == true) return; // 如果服务已停止，不再执行心跳检测

            var settings = GetCurrentSettings();
            try
            {
                _logger.LogDebug("执行心跳检测");

                // 避免重复执行重连
                if (_isReconnecting == 1) // 检查重连状态
                {
                    _logger.LogDebug("重连正在进行中，跳过心跳检测");
                    return;
                }

                switch (settings.HealthCheckMode)
                {
                    case HealthCheckMode.CommandResponse:
                        await CheckByCommandResponse();
                        break;

                    case HealthCheckMode.DataActivity:
                        CheckByDataActivity(settings.LastDataTimeoutSeconds ?? 30);
                        break;

                    case HealthCheckMode.StatusQuery:
                        await CheckByStatusQuery();
                        break;

                    case HealthCheckMode.Disabled:
                        break;

                    default:
                        _logger.LogWarning("未知的心跳模式: {HealthCheckMode}", settings.HealthCheckMode);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "健康检查过程中发生异常");
            }
        }

        /// <summary>
        /// 通过命令响应方式检查PLC连接状态
        /// </summary>
        /// <returns>表示异步检查操作的任务</returns>
        private async Task CheckByCommandResponse()
        {
            var response = await ExecuteReadOperationAsync(0x03, 1, 0, 1, 2000); // 读取保持寄存器
            if (response == null || response.IsError)
            {
                _logger.LogWarning("Modbus心跳检测失败");
                Notify(NotificationType.Warning, "心跳失败，尝试重连", "HealthCheck");
                await HandleConnectionLossAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 通过数据活动方式检查PLC连接状态
        /// </summary>
        /// <param name="timeoutSeconds">超时秒数</param>
        private void CheckByDataActivity(int timeoutSeconds)
        {
            if (!_isRunning) return; // 如果服务已停止，不再执行数据活动检查

            var lastReceivedTime = Interlocked.Read(ref _lastDataReceivedTicks);
            var now = DateTime.UtcNow.Ticks;
            var elapsedSeconds = (now - lastReceivedTime) / TimeSpan.TicksPerSecond;

            if (elapsedSeconds > timeoutSeconds)
            {
                _logger.LogWarning("超过 {TimeoutSeconds}s 未收到Modbus数据", timeoutSeconds);
                Notify(NotificationType.Warning, $"无数据超时({elapsedSeconds}s)，尝试重连", "HealthCheck");
                // 使用ContinueWith确保异常被记录
                _ = HandleConnectionLossAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogError(t.Exception, "重连操作异常");
                    }
                }, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// 通过状态查询方式检查PLC连接状态
        /// </summary>
        /// <returns>表示异步检查操作的任务</returns>
        private async Task CheckByStatusQuery()
        {
            var response = await ExecuteReadOperationAsync(0x01, 1, 0, 1, 3000); // 读取线圈状态
            if (response == null || response.IsError)
            {
                _logger.LogWarning("Modbus状态查询失败");
                Notify(NotificationType.Warning, "状态异常，尝试重连", "HealthCheck");
                await HandleConnectionLossAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 发送通知
        /// </summary>
        /// <param name="type">通知类型</param>
        /// <param name="message">通知消息</param>
        /// <param name="source">通知来源</param>
        private void Notify(NotificationType type, string message, string source)
        {
            OnNotification?.Invoke(this, new CommunicationNotification(type, message, source));
            _logger.LogInformation($"[{source}] {message}");
        }

        /// <summary>
        /// 异步发送一条原始命令到PLC，不等待响应
        /// </summary>
        /// <param name="command">要发送的命令字符串</param>
        /// <returns>表示异步发送操作的任务</returns>
        public async Task SendCommandAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                Notify(NotificationType.Warning, "发送命令为空", "SendCommand");
                return;
            }

            if (!IsConnected || !_isRunning)
            {
                Notify(NotificationType.Error, "TCP未连接或服务已停止，无法发送命令", "SendCommand");
                return;
            }

            try
            {
                var data = Encoding.UTF8.GetBytes(command);
                await SendRawDataAsync(data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送命令失败");
                Notify(NotificationType.Error, $"发送失败: {ex.Message}", "SendCommand");
            }
        }

        /// <summary>
        /// 执行一个需要响应的TCP操作，发送指定命令并等待响应
        /// 验证响应是否以指定前缀开头
        /// </summary>
        /// <param name="command">要发送到PLC的命令字符串</param>
        /// <param name="expectedPrefix">期望响应消息的前缀</param>
        /// <param name="timeoutMs">等待响应的超时时间（毫秒）</param>
        /// <returns>如果收到匹配的响应返回true，否则返回false</returns>
        public async Task<bool> ExecuteOperationAsync(string command, string expectedPrefix, int timeoutMs = 3000)
        {
            return await ExecuteOperationAsync(
                response => Task.FromResult(response.StartsWith(expectedPrefix)),
                command,
                timeoutMs);
        }

        /// <summary>
        /// 执行一个需要响应的TCP操作，使用自定义异步验证函数判断响应是否有效
        /// </summary>
        /// <param name="responseValidator">一个异步函数，接收响应字符串并返回布尔值</param>
        /// <param name="command">要发送到PLC的命令字符串</param>
        /// <param name="timeoutMs">等待响应的超时时间（毫秒）</param>
        /// <returns>如果验证通过返回true，否则返回false</returns>
        public async Task<bool> ExecuteOperationAsync(Func<string, Task<bool>> responseValidator, string command, int timeoutMs = 3000)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                Notify(NotificationType.Warning, "命令为空", "ExecuteOperation");
                return false;
            }

            if (!IsConnected || !_isRunning)
            {
                Notify(NotificationType.Error, "TCP未连接或服务已停止", "ExecuteOperation");
                return false;
            }

            var tcs = new TaskCompletionSource<string>();

            lock (_stringResponseLock)
            {
                if (_pendingStringResponseTcs != null && !_pendingStringResponseTcs.Task.IsCompleted)
                {
                    _logger.LogWarning("已有字符串响应等待处理，当前操作将被取消");
                    Notify(NotificationType.Warning, "已有操作在等待响应", "ExecuteOperation");
                    return false;
                }

                _pendingStringResponseTcs = tcs;
            }

            // 订阅原始数据接收事件来处理字符串响应
            void OnRawDataReceived(object sender, byte[] data)
            {
                lock (_stringResponseLock)
                {
                    // 确保只处理未完成的任务
                    if (_pendingStringResponseTcs?.Task.IsCompleted == false)
                    {
                        HandleStringResponse(data);
                    }
                }
            }

            RawDataReceived += OnRawDataReceived;

            using var cts = new CancellationTokenSource();
            bool success = false;

            try
            {
                using var timeoutCts = new CancellationTokenSource();
                var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);

                await SendCommandAsync(command).ConfigureAwait(false);

                var resultTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
                if (resultTask == tcs.Task)
                {
                    string response = await tcs.Task.ConfigureAwait(false);
                    success = await responseValidator(response).ConfigureAwait(false);
                    Notify(success ? NotificationType.Success : NotificationType.Warning,
                           success ? $"响应匹配: {response}" : $"响应不匹配: {response}",
                           "ExecuteOperation");
                }
                else if (timeoutCts.Token.IsCancellationRequested)
                {
                    Notify(NotificationType.Warning, "操作被中断", "ExecuteOperation");
                }
                else
                {
                    Notify(NotificationType.Warning, "等待响应超时", "ExecuteOperation");
                }
            }
            catch (OperationCanceledException)
            {
                Notify(NotificationType.Warning, "操作取消", "ExecuteOperation");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行操作时出错");
                Notify(NotificationType.Error, $"执行失败: {ex.Message}", "ExecuteOperation");
            }
            finally
            {
                RawDataReceived -= OnRawDataReceived;

                lock (_stringResponseLock)
                {
                    _pendingStringResponseTcs = null;
                }
            }

            return success;
        }

        /// <summary>
        /// 获取当前服务状态信息
        /// </summary>
        /// <returns>包含服务状态信息的字符串</returns>
        public string GetStatusInfo()
        {
            var status = new StringBuilder();
            status.AppendLine($"服务运行状态: {_isRunning}");
            status.AppendLine($"TCP连接状态: {IsConnected}");
            status.AppendLine($"待处理Modbus响应数量: {_pendingResponses.Count}");
            status.AppendLine($"待处理字符串响应: {_pendingStringResponseTcs != null && !_pendingStringResponseTcs.Task.IsCompleted}");
            status.AppendLine($"心跳检测任务: {_healthCheckTask != null}");
            status.AppendLine($"读取循环取消令牌: {_readLoopCts != null}");
            status.AppendLine($"心跳检测取消令牌: {_heartbeatCts != null}");
            status.AppendLine($"是否正在重连: {_isReconnecting == 1}");
            status.AppendLine($"接收缓冲区大小: {_receiveBuffer.Length} 字节");

            return status.ToString();
        }

        #region IDisposable and IAsyncDisposable Implementation

        /// <summary>
        /// 标记该实例是否已被释放，防止重复释放资源。
        /// </summary>
        private bool _disposed = false;

        /// <summary>
        /// 释放当前对象占用的所有托管和非托管资源。
        /// 此方法由 <see cref="IDisposable.Dispose"/> 调用，执行同步清理。
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 释放资源的核心方法。当 <paramref name="disposing"/> 为 true 时，
        /// 表示正在同步释放托管资源；为 false 时表示由终结器调用（仅释放非托管资源）。
        /// 本实现中主要处理托管资源的释放。
        /// </summary>
        /// <param name="disposing">
        /// 若为 <see langword="true"/>，表示方法由 <see cref="Dispose()"/> 显式调用；
        /// 若为 <see langword="false"/>，表示由对象终结器（finalizer）调用。
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // 同步停止服务并释放托管资源
                if (_isRunning)
                {
                    _isRunning = false;
                    InterruptAllOperations();
                }

                CleanupConnection();

                _readLoopCts?.Cancel();
                _readLoopCts?.Dispose();

                _heartbeatCts?.Cancel();
                _heartbeatCts?.Dispose();

                _syncLock?.Dispose();
                _receiveBuffer?.Dispose();
            }

            _disposed = true;
        }

        /// <summary>
        /// 异步释放当前对象占用的资源。首先尝试通过 <see cref="StopAsync"/> 
        /// 优雅地停止服务，然后执行同步清理逻辑以确保所有资源被正确释放。
        /// 此方法由 <see cref="IAsyncDisposable.DisposeAsync"/> 调用。
        /// </summary>
        /// <returns>一个表示异步释放操作的 <see cref="ValueTask"/>。</returns>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            // 先尝试优雅停止（异步）
            if (_isRunning)
            {
                try
                {
                    await StopAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 忽略异常：Dispose/DisposeAsync 不应抛出异常
                }
            }

            // 复用同步 Dispose 逻辑完成最终清理
            Dispose();
        }

        #endregion

        #region 扩展
        /// <summary>
        /// 发送自定义 Modbus TCP 请求帧（必须包含有效 MBAP 头），并等待匹配事务ID的响应
        /// </summary>
        /// <param name="customRequestFrame">完整的 Modbus TCP 请求帧（至少8字节）</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>Modbus响应对象</returns>
        public async Task<ModbusResponse?> SendCustomModbusRequestAsync(byte[] customRequestFrame, int timeoutMs = 3000)
        {
            if (customRequestFrame == null || customRequestFrame.Length < 8)
                throw new ArgumentException("Modbus TCP 请求帧至少需要8字节（MBAP头+PDU）", nameof(customRequestFrame));

            if (!IsConnected || !_isRunning)
            {
                Notify(NotificationType.Error, "TCP未连接或服务已停止", "SendCustomModbusRequest");
                return null;
            }

            // 解析传入帧中的事务ID（必须由调用方保证唯一性，或我们覆盖它）
            ushort transactionId = BinaryPrimitives.ReadUInt16BigEndian(customRequestFrame.AsSpan(0));

            // 👇 更安全的做法：强制使用我们生成的事务ID（避免冲突）
            var newTid = GetNextTransactionId();
            BinaryPrimitives.WriteUInt16BigEndian(customRequestFrame.AsSpan(0), newTid);
            transactionId = newTid;

            var tcs = new TaskCompletionSource<ModbusResponse>();
            _pendingResponses[transactionId] = tcs;

            try
            {
                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                var timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);

                await SendRawDataAsync(customRequestFrame).ConfigureAwait(false);

                var resultTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
                if (resultTask == tcs.Task)
                {
                    var response = await tcs.Task.ConfigureAwait(false);
                    return response;
                }
                else
                {
                    _logger.LogWarning("自定义请求等待响应超时");
                    _pendingResponses.TryRemove(transactionId, out _);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送自定义Modbus请求失败");
                _pendingResponses.TryRemove(transactionId, out _);
                throw;
            }
        }

        #endregion
    }
}



