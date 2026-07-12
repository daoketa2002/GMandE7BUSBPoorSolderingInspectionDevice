using Microsoft.Extensions.Logging;
using System.Diagnostics;
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
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;
namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus
{
    /// <summary>
    /// TCP Modbus PLC动作控制客户端通信服务 - 支持中断操作
    /// </summary>
    public class TcpClientPLCMotionService : IAsyncDisposable, IDisposable
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
        /// Modbus 发送阶段轻量锁。只保护 NetworkStream.WriteAsync/FlushAsync，不锁等待响应全周期。
        /// </summary>
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        /// <summary>
        /// 请求生命周期串行锁。保护从创建 TID、发送、等待响应到删除 Pending 的完整生命周期。
        /// 确保任意时刻最多只有一个普通 Modbus 请求处于 pending 状态。
        /// </summary>
        private readonly SemaphoreSlim _requestLock = new(1, 1);

        /// <summary>
        /// 存储待处理响应的字典，键为事务ID
        /// </summary>
        private readonly ConcurrentDictionary<ushort, PendingModbusRequest> _pendingRequests = new();

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

        // ========== PLC 连接配置属性（由 DeviceConnectionManager 在启动时注入） ==========

        /// <summary>PLC 服务器地址</summary>
        public string Host { get; set; } = "192.168.1.3";

        /// <summary>Modbus TCP 端口号</summary>
        public int Port { get; set; } = 502;

        /// <summary>TCP 接收超时（毫秒）</summary>
        public int ReceiveTimeoutMs { get; set; } = 5000;

        /// <summary>TCP 发送超时（毫秒）</summary>
        public int SendTimeoutMs { get; set; } = 5000;

        /// <summary>重连延迟（毫秒）</summary>
        public int ReconnectDelayMs { get; set; } = 2000;

        /// <summary>最大重连次数</summary>
        public int MaxReconnectAttempts { get; set; } = 12;

        /// <summary>心跳检测模式</summary>
        public HealthCheckMode HealthCheckMode { get; set; } = HealthCheckMode.Disabled;

        /// <summary>心跳检查间隔（秒）</summary>
        public int HealthCheckIntervalSeconds { get; set; } = 5;

        /// <summary>无数据超时时间（秒），配合 DataActivity 模式使用</summary>
        public int LastDataTimeoutSeconds { get; set; } = 30;

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
        public TcpClientPLCMotionService(
            ILogger<TcpClientPLCMotionService> logger)
        {
            _logger = logger;
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

                _logger.LogInformation("开始启动Modbus TCP客户端...");

                try
                {
                    // 尝试连接到服务器
                    await ConnectToServerAsync().ConfigureAwait(false);

                    // TCP 握手成功后仅作为内部临时通信状态，用于发送 Modbus 验证帧。
                    // 注意：此处不能对外触发已连接事件，否则无真实 PLC 响应时运行界面会短暂变绿。
                    _isConnected = true;

                    // ⭐ 先启动数据接收循环（Modbus验证需要读循环接收应答）
                    _readLoopCts = new CancellationTokenSource();
                    _ = Task.Run(() => ReadDataAsync(_readLoopCts.Token), _readLoopCts.Token);

                    // ⭐ Modbus验证：读保持寄存器确认对方是真正的PLC，2秒超时
                    await VerifyDeviceRespondsAsync().ConfigureAwait(false);

                    // 只有 PLC 通过 Modbus 应答验证后，才向 UI 和设备管理器发布已连接状态。
                    ConnectionStateChanged?.Invoke(true);
                    DetailedConnectionStateChanged?.Invoke(ConnectionState.Connected);

                    _logger.LogInformation($"Modbus TCP客户端已连接到 {Host}:{Port}");
                    Notify(NotificationType.Success, $"已连接到 {Host}:{Port}", "Start");
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
        /// 发送读保持寄存器指令验证 PLC 是否有 Modbus 应答。
        /// TCP 握手成功不代表对方是 PLC，必须在 _isConnected 宣告前验证。
        /// 验证失败时抛出 IOException，由 StartAsync 的 catch 块统一清理。
        /// </summary>
        private async Task VerifyDeviceRespondsAsync()
        {
            // 读 DT120（保持寄存器），1 字，2 秒超时
            // Phase E3: 验证必须同时满足 response != null 且 !response.IsError
            var response = await ExecuteReadOperationAsync(0x03, 1, 120, 1, 2000).ConfigureAwait(false);
            if (response == null)
            {
                _logger.LogWarning("PLC设备Modbus验证失败：TCP已建立，但2秒内未收到DT120响应");
                throw new IOException("PLC设备验证失败：2秒内无Modbus应答");
            }
            if (response.IsError)
            {
                _logger.LogWarning("PLC设备Modbus验证失败：Modbus ErrorCode={ErrorCode}，DT120返回异常响应", response.ErrorCode);
                throw new IOException($"PLC设备验证失败：Modbus异常响应 ErrorCode={response.ErrorCode}");
            }
            _logger.LogInformation("PLC设备Modbus验证通过（DT120应答正常）");
        }

        /// <summary>
        /// 启动心跳检测循环
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>表示异步心跳检测循环的任务</returns>
        private async Task StartHealthCheckLoop(CancellationToken ct)
        {
            var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(HealthCheckIntervalSeconds));

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
        /// 异步连接到服务器，使用当前属性中的连接参数
        /// </summary>
        /// <returns>表示异步连接操作的任务</returns>
        private async Task ConnectToServerAsync()
        {
            int connectTimeoutMs = ConnectTimeoutMsDefault ?? 3000;
            CleanupConnection();

            TcpClient? tempTcpClient = null;
            try
            {
                _logger.LogInformation("尝试连接到 {Host}:{Port}", Host, Port);
                tempTcpClient = new TcpClient { ReceiveTimeout = ReceiveTimeoutMs, SendTimeout = SendTimeoutMs };
                using var timeoutCts = new CancellationTokenSource(connectTimeoutMs);
                await tempTcpClient.ConnectAsync(Host, Port, timeoutCts.Token).ConfigureAwait(false);
                _tcpClient = tempTcpClient;
                _networkStream = _tcpClient.GetStream();
                _logger.LogInformation("TCP连接建立成功");
            }
            catch
            {
                tempTcpClient?.Dispose();
                CleanupConnection();
                throw;
            }
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
                            MarkDisconnected();
                            break;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.LogDebug("网络流已被释放");
                        if (!ct.IsCancellationRequested && _isRunning) MarkDisconnected();
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
                    Notify(NotificationType.Warning, "数据读取异常，已标记断线", "ReadData");
                    MarkDisconnected();
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
                    // DIAG-KEEP: length 是剩余字节数（模式A），长度判断不应加 offset
                    if (length >= 9) // 确保有足够的数据获取错误码
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
                    // DIAG-FIXED: 修复 offset/length 语义 — length 是剩余字节数，不是绝对位置
                    case 0x01: // 读线圈
                    case 0x02: // 读离散输入
                    case 0x03: // 读保持寄存器
                    case 0x04: // 读输入寄存器
                        // 响应格式: [Unit][FC][ByteCount][Data...]
                        if (length < 9)
                        {
                            _logger.LogDebug("读响应数据不完整，无法读取ByteCount");
                            return null;
                        }
                        response.ByteCount = data[offset + 8];
                        if (length < 9 + response.ByteCount)
                        {
                            _logger.LogDebug($"读响应数据不完整: 期望 {9 + response.ByteCount} 字节, 实际 {length} 字节");
                            return null;
                        }
                        response.Data = new byte[response.ByteCount];
                        Array.Copy(data, offset + 9, response.Data, 0, response.ByteCount);
                        break;

                    case 0x05: // 写单线圈
                    case 0x06: // 写单寄存器
                        // 响应格式: [Unit][FC][起始地址高][起始地址低][值高][值低] → 共 6 字节 PDU
                        if (length < 12) // 6 (MBAP) + 6 (PDU) = 12
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
                        if (length < 12) // 6 (MBAP) + 6 (PDU) = 12
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
            if (_pendingRequests.TryRemove(response.TransactionId, out var pendingReq))
            {
                // 设置结果
                pendingReq.Completion.TrySetResult(response);
                return;
            }

            // DIAG-KEEP: 未匹配响应可能代表迟到响应、超时后响应或 pending 生命周期异常，必须保留。
            _logger.LogWarning(
                "[Modbus诊断][未匹配响应] TID={TID}, FC={FC}, Matched=false, PendingCount={PendingCount}, PossibleLateResponse=true",
                response.TransactionId, response.FunctionCode, _pendingRequests.Count);

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
            foreach (var kvp in _pendingRequests)
            {
                if (!kvp.Value.Completion.Task.IsCompleted)
                {
                    kvp.Value.Completion.TrySetCanceled();
                }
            }
            _pendingRequests.Clear();

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

        /// <summary>真实通信失败后只发布断线；自动重连统一由 DeviceConnectionService 负责。</summary>
        private void MarkDisconnected()
        {
            var wasConnected = _isConnected;
            InterruptAllOperations();
            CleanupConnection();
            if (wasConnected)
            {
                ConnectionStateChanged?.Invoke(false);
                DetailedConnectionStateChanged?.Invoke(ConnectionState.Disconnected);
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
        public async Task<ModbusResponse?> ExecuteReadOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort quantity, int timeoutMs = 3000, CancellationToken ct = default)
        {
            if (!IsConnected || !_isRunning)
            {
                _logger.LogWarning("TCP未连接或服务已停止，无法执行读取操作");
                Notify(NotificationType.Error, "TCP未连接或服务已停止", "ExecuteReadOperation");
                return null;
            }

            bool lockTaken = false;
            ushort? transactionId = null;
            string operation = "Read";
            var totalSw = Stopwatch.StartNew();

            try
            {
                // Phase E1: 完整生命周期串行化 — 在锁内创建 TID/Pending、发送、等待响应
                await _requestLock.WaitAsync(ct).ConfigureAwait(false);
                lockTaken = true;

                // 获取锁后再次检查连接状态（等待期间可能变化）
                if (!IsConnected || !_isRunning)
                {
                    _logger.LogWarning("获取锁后TCP未连接或服务已停止，无法执行读取操作");
                    return null;
                }

                // 在锁内生成 TID 和创建 Pending
                transactionId = GetNextTransactionId();
                var tcs = new TaskCompletionSource<ModbusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pendingRequest = new PendingModbusRequest
                {
                    Completion = tcs,
                };
                _pendingRequests[transactionId.Value] = pendingRequest;

                ModbusResponse? response = null;

                byte[] request = CreateModbusTcpRequest(functionCode, unitId, startAddress, quantity, transactionId.Value);

                // 发送阶段（由 _sendLock 保护，与 _requestLock 独立）
                var sendSw = Stopwatch.StartNew();
                try
                {
                    await SendRawDataAsync(request).ConfigureAwait(false);
                    sendSw.Stop();
                }
                catch (Exception ex)
                {
                    sendSw.Stop();
                    _logger.LogError(
                        ex,
                        "[Modbus诊断][发送失败] TID={TID}, FC={FC}, StartAddress={StartAddress}, ElapsedMs={ElapsedMs}",
                        transactionId.Value, functionCode, startAddress, sendSw.ElapsedMilliseconds);
                    return null;
                }

                // 等待响应：使用 WaitAsync 明确区分 正常响应 / 超时 / 取消
                try
                {
                    response = await pendingRequest.Completion.Task
                        .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct)
                        .ConfigureAwait(false);
                    totalSw.Stop();

                    if (response != null)
                    {
                        if (response.IsError)
                        {
                            _logger.LogWarning($"Modbus错误: {response.ErrorCode}");
                            Notify(NotificationType.Warning, $"Modbus错误: {response.ErrorCode}", "ExecuteReadOperation");
                        }
                        else
                        {
                            long elapsedMs = totalSw.ElapsedMilliseconds;
                            if (elapsedMs >= timeoutMs * 0.8)
                            {
                                _logger.LogWarning(
                                    "[Modbus性能][慢请求] TID={TID}, Operation={Operation}, FC={FC}, UnitId={UnitId}, StartAddress={StartAddress}, Quantity={Quantity}, ElapsedMs={ElapsedMs}, PendingCount={PendingCount}, IsConnected={IsConnected}, IsRunning={IsRunning}",
                                    transactionId.Value, operation, functionCode, unitId, startAddress, quantity, elapsedMs, _pendingRequests.Count, _isConnected, _isRunning);
                            }
                            else if (elapsedMs >= 300)
                            {
                                _logger.LogInformation(
                                    "[Modbus性能][慢请求] TID={TID}, Operation={Operation}, FC={FC}, UnitId={UnitId}, StartAddress={StartAddress}, Quantity={Quantity}, ElapsedMs={ElapsedMs}, PendingCount={PendingCount}, IsConnected={IsConnected}, IsRunning={IsRunning}",
                                    transactionId.Value, operation, functionCode, unitId, startAddress, quantity, elapsedMs, _pendingRequests.Count, _isConnected, _isRunning);
                            }
                        }
                    }
                }
                catch (TimeoutException)
                {
                    totalSw.Stop();
                    _logger.LogError(
                        "[Modbus诊断][读取超时] TID={TID}, FC={FC}, UnitId={UnitId}, StartAddress={StartAddress}, ElapsedMs={ElapsedMs}, TimeoutMs={TimeoutMs}, IsConnected={IsConnected}, IsRunning={IsRunning}",
                        transactionId.Value, functionCode, unitId, startAddress,
                        totalSw.ElapsedMilliseconds, timeoutMs,
                        _isConnected, _isRunning);
                    Notify(NotificationType.Warning, "Modbus读取超时", "ExecuteReadOperation");
                }
                catch (OperationCanceledException)
                {
                    totalSw.Stop();
                    _logger.LogWarning("读取操作被取消，TID={TID}, ElapsedMs={ElapsedMs}", transactionId.Value, totalSw.ElapsedMilliseconds);
                    Notify(NotificationType.Warning, "读取操作被取消", "ExecuteReadOperation");
                }

                return response;
            }
            finally
            {
                // finally: 幂等清理 Pending 并释放 requestLock（任意出口均不漏）
                if (transactionId.HasValue)
                    _pendingRequests.TryRemove(transactionId.Value, out _);

                if (lockTaken)
                    _requestLock.Release();
            }
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
        public async Task<ModbusResponse?> ExecuteWriteOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort[] data, int timeoutMs = 3000, CancellationToken ct = default)
        {
            if (!IsConnected || !_isRunning)
            {
                _logger.LogWarning(
                    "[Modbus诊断][写入拒绝] FC={FC}, UnitId={UnitId}, StartAddress={StartAddress}, IsConnected={IsConnected}, IsRunning={IsRunning}",
                    functionCode, unitId, startAddress, IsConnected, _isRunning);
                Notify(NotificationType.Error, "TCP未连接或服务已停止", "ExecuteWriteOperation");
                return null;
            }

            bool lockTaken = false;
            ushort? transactionId = null;
            string operation = "Write";
            var totalSw = Stopwatch.StartNew();

            try
            {
                // Phase E1: 完整生命周期串行化 — 在锁内创建 TID/Pending、发送、等待响应
                await _requestLock.WaitAsync(ct).ConfigureAwait(false);
                lockTaken = true;

                // 获取锁后再次检查连接状态
                if (!IsConnected || !_isRunning)
                {
                    _logger.LogWarning(
                        "[Modbus诊断][写入拒绝] FC={FC}, UnitId={UnitId}, StartAddress={StartAddress}, IsConnected={IsConnected}, IsRunning={IsRunning}",
                        functionCode, unitId, startAddress, IsConnected, _isRunning);
                    Notify(NotificationType.Error, "TCP未连接或服务已停止", "ExecuteWriteOperation");
                    return null;
                }

                // 在锁内生成 TID 和创建 Pending
                transactionId = GetNextTransactionId();
                var tcs = new TaskCompletionSource<ModbusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pendingRequest = new PendingModbusRequest
                {
                    Completion = tcs,
                };
                _pendingRequests[transactionId.Value] = pendingRequest;

                ModbusResponse? response = null;

                // 根据功能码创建相应的写入请求
                byte[] request;
                if (functionCode == 0x06) // 写单个寄存器
                {
                    if (data.Length != 1)
                        throw new ArgumentException("写单个寄存器时数据长度必须为1", nameof(data));
                    request = CreateModbusTcpWriteSingleRequest(unitId, startAddress, data[0], transactionId.Value);
                }
                else if (functionCode == 0x10) // 写多个寄存器
                {
                    request = CreateModbusTcpWriteRequest(functionCode, unitId, startAddress, data, transactionId.Value);
                }
                else if (functionCode == 0x05) // 写单个线圈
                {
                    if (data.Length != 1)
                        throw new ArgumentException("写单个线圈时数据长度必须为1");
                    bool value = data[0] == 0xFF00;
                    request = ModbusTcpMessageHelper.CreateWriteSingleCoilRequest(
                        transactionId.Value, unitId, startAddress, value);
                }
                else
                {
                    throw new ArgumentException($"不支持的功能码: {functionCode}", nameof(functionCode));
                }

                // 发送阶段
                var sendSw = Stopwatch.StartNew();
                try
                {
                    await SendRawDataAsync(request).ConfigureAwait(false);
                    sendSw.Stop();
                }
                catch (Exception ex)
                {
                    sendSw.Stop();
                    _logger.LogError(
                        ex,
                        "[Modbus诊断][发送失败] TID={TID}, FC={FC}, StartAddress={StartAddress}, ElapsedMs={ElapsedMs}",
                        transactionId.Value, functionCode, startAddress, sendSw.ElapsedMilliseconds);
                    return null;
                }

                // 等待响应：使用 WaitAsync 明确区分 正常响应 / 超时 / 取消
                try
                {
                    response = await pendingRequest.Completion.Task
                        .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct)
                        .ConfigureAwait(false);
                    totalSw.Stop();

                    if (response != null)
                    {
                        // 验证事务ID是否匹配
                        if (response.TransactionId == transactionId.Value)
                        {
                            if (response.IsError)
                            {
                                _logger.LogWarning($"Modbus写入错误: 事务ID={response.TransactionId}, 错误码={response.ErrorCode}");
                                Notify(NotificationType.Warning, $"Modbus写入错误: {response.ErrorCode}", "ExecuteWriteOperation");
                            }
                            else
                            {
                                long elapsedMs = totalSw.ElapsedMilliseconds;
                                if (elapsedMs >= timeoutMs * 0.8)
                                {
                                    _logger.LogWarning(
                                        "[Modbus性能][慢请求] TID={TID}, Operation={Operation}, FC={FC}, UnitId={UnitId}, StartAddress={StartAddress}, Quantity={Quantity}, ElapsedMs={ElapsedMs}, PendingCount={PendingCount}, IsConnected={IsConnected}, IsRunning={IsRunning}",
                                        transactionId.Value, operation, functionCode, unitId, startAddress, data.Length, elapsedMs, _pendingRequests.Count, _isConnected, _isRunning);
                                }
                                else if (elapsedMs >= 300)
                                {
                                    _logger.LogInformation(
                                        "[Modbus性能][慢请求] TID={TID}, Operation={Operation}, FC={FC}, UnitId={UnitId}, StartAddress={StartAddress}, Quantity={Quantity}, ElapsedMs={ElapsedMs}, PendingCount={PendingCount}, IsConnected={IsConnected}, IsRunning={IsRunning}",
                                        transactionId.Value, operation, functionCode, unitId, startAddress, data.Length, elapsedMs, _pendingRequests.Count, _isConnected, _isRunning);
                                }
                                // 正常写成功不触发通知（避免噪声），异常由上层业务日志记录
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"事务ID不匹配: 期望={transactionId.Value}, 实际={response.TransactionId}");
                            Notify(NotificationType.Warning, "响应事务ID不匹配", "ExecuteWriteOperation");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("接收到空响应");
                        Notify(NotificationType.Warning, "接收到空响应", "ExecuteWriteOperation");
                    }
                }
                catch (TimeoutException)
                {
                    totalSw.Stop();
                    _logger.LogError(
                        "[Modbus诊断][写入超时] TID={TID}, FC={FC}, UnitId={UnitId}, StartAddress={StartAddress}, ElapsedMs={ElapsedMs}, TimeoutMs={TimeoutMs}, IsConnected={IsConnected}, IsRunning={IsRunning}",
                        transactionId.Value, functionCode, unitId, startAddress,
                        totalSw.ElapsedMilliseconds, timeoutMs,
                        _isConnected, _isRunning);
                    Notify(NotificationType.Warning, "Modbus写入超时", "ExecuteWriteOperation");
                }
                catch (OperationCanceledException)
                {
                    totalSw.Stop();
                    _logger.LogWarning("写入操作被取消，TID={TID}, ElapsedMs={ElapsedMs}", transactionId.Value, totalSw.ElapsedMilliseconds);
                    Notify(NotificationType.Warning, "写入操作被取消", "ExecuteWriteOperation");
                }

                return response;
            }
            finally
            {
                // finally: 幂等清理 Pending 并释放 requestLock（任意出口均不漏）
                if (transactionId.HasValue)
                    _pendingRequests.TryRemove(transactionId.Value, out _);

                if (lockTaken)
                    _requestLock.Release();
            }
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
                throw new InvalidOperationException("TCP未连接或服务已停止");
            }

            _logger.LogDebug($"发送 {data.Length} 字节的Modbus请求: {BitConverter.ToString(data)}");
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _networkStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                await _networkStream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "PLC 写入发生连接级异常，已标记断线");
                MarkDisconnected();
                throw;
            }
            finally
            {
                _sendLock.Release();
            }

            _logger.LogDebug($"已发送 {data.Length} 字节的Modbus请求");
        }

        /// <summary>
        /// 执行心跳检测
        /// </summary>
        /// <returns>表示异步心跳检测操作的任务</returns>
        private async Task HealthCheckAsync()
        {
            if (!_isRunning || _heartbeatCts?.IsCancellationRequested == true) return; // 如果服务已停止，不再执行心跳检测

            try
            {
                _logger.LogDebug("执行心跳检测");

                switch (HealthCheckMode)
                {
                    case HealthCheckMode.CommandResponse:
                        await CheckByCommandResponse();
                        break;

                    case HealthCheckMode.DataActivity:
                        CheckByDataActivity(LastDataTimeoutSeconds);
                        break;

                    case HealthCheckMode.StatusQuery:
                        await CheckByStatusQuery();
                        break;

                    case HealthCheckMode.Disabled:
                        break;

                    default:
                        _logger.LogWarning("未知的心跳模式: {HealthCheckMode}", HealthCheckMode);
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
                Notify(NotificationType.Warning, "心跳失败，已标记断线", "HealthCheck");
                MarkDisconnected();
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
                Notify(NotificationType.Warning, $"无数据超时({elapsedSeconds}s)，已标记断线", "HealthCheck");
                MarkDisconnected();
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
                Notify(NotificationType.Warning, "状态异常，已标记断线", "HealthCheck");
                MarkDisconnected();
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

            bool success = false;

            try
            {
                var timeoutTask = Task.Delay(timeoutMs);

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
                else
                {
                    _logger.LogWarning("[Modbus][命令超时] Command={Command}, TimeoutMs={TimeoutMs}", command, timeoutMs);
                    Notify(NotificationType.Warning, "Modbus命令响应超时", "ExecuteOperation");
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
            status.AppendLine($"待处理Modbus响应数量: {_pendingRequests.Count}");
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
                _sendLock?.Dispose();
                _requestLock.Dispose();
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
        /// <param name="ct">取消令牌</param>
        /// <returns>Modbus响应对象</returns>
        public async Task<ModbusResponse?> SendCustomModbusRequestAsync(byte[] customRequestFrame, int timeoutMs = 3000, CancellationToken ct = default)
        {
            if (customRequestFrame == null || customRequestFrame.Length < 8)
                throw new ArgumentException("Modbus TCP 请求帧至少需要8字节（MBAP头+PDU）", nameof(customRequestFrame));

            byte functionCode = customRequestFrame[7];
            var totalSw = Stopwatch.StartNew();

            if (!IsConnected || !_isRunning)
            {
                Notify(NotificationType.Error, "TCP未连接或服务已停止", "SendCustomModbusRequest");
                return null;
            }

            bool lockTaken = false;
            ushort? transactionId = null;

            try
            {
                // Phase E1: 完整生命周期串行化 — Custom 也必须入 Gate
                await _requestLock.WaitAsync(ct).ConfigureAwait(false);
                lockTaken = true;

                // 获取锁后再次检查连接状态
                if (!IsConnected || !_isRunning)
                {
                    Notify(NotificationType.Error, "TCP未连接或服务已停止", "SendCustomModbusRequest");
                    return null;
                }

                // 在锁内生成 TID 并覆盖传入帧的 TID
                var newTid = GetNextTransactionId();
                BinaryPrimitives.WriteUInt16BigEndian(customRequestFrame.AsSpan(0), newTid);
                transactionId = newTid;

                var tcs = new TaskCompletionSource<ModbusResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pendingRequest = new PendingModbusRequest
                {
                    Completion = tcs,
                };
                _pendingRequests[transactionId.Value] = pendingRequest;

                await SendRawDataAsync(customRequestFrame).ConfigureAwait(false);

                // 等待响应：使用 WaitAsync 明确区分 正常响应 / 超时 / 取消
                try
                {
                    var response = await pendingRequest.Completion.Task
                        .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct)
                        .ConfigureAwait(false);
                    return response;
                }
                catch (TimeoutException)
                {
                    totalSw.Stop();
                    _logger.LogWarning(
                        "[Modbus诊断][自定义请求超时] TID={TID}, FC={FC}, ElapsedMs={ElapsedMs}, TimeoutMs={TimeoutMs}, IsConnected={IsConnected}, IsRunning={IsRunning}",
                        transactionId.Value, functionCode, totalSw.ElapsedMilliseconds, timeoutMs, _isConnected, _isRunning);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    totalSw.Stop();
                    _logger.LogWarning("自定义请求被取消，TID={TID}", transactionId.Value);
                    return null;
                }
            }
            finally
            {
                // finally: 幂等清理 Pending 并释放 requestLock
                if (transactionId.HasValue)
                    _pendingRequests.TryRemove(transactionId.Value, out _);

                if (lockTaken)
                    _requestLock.Release();
            }
        }

        #endregion
    }
}



