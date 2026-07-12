// ============================================================
// 文件: Services/TcpModbus/PlcConnectionTester.cs
// 描述: 独立无状态 PLC 临时测试器实现
// 职责: 使用全新局部 TcpClient/NetworkStream 测试 Modbus TCP 通信，
//       不访问生产通信对象内部状态。
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus
{
    /// <summary>
    /// 独立无状态 PLC 临时测试器。
    /// 每次调用 TestAsync 创建全新 TcpClient，用完即释放。
    /// 仅使用局部变量，不访问任何生产通信对象内部状态。
    /// </summary>
    public sealed class PlcConnectionTester : IPlcConnectionTester
    {
        private readonly ILogger<PlcConnectionTester> _logger;

        public PlcConnectionTester(ILogger<PlcConnectionTester> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 测试指定 PLC 配置是否可达且 Modbus 通信正常。
        /// 完成内容：
        /// 1. TCP Connect（纳入总超时）
        /// 2. 发送 FC03 读保持寄存器请求（DT120，1 字）
        /// 3. 按 MBAP Length 精确读取完整响应
        /// 4. 校验 MBAP、TransactionId、ProtocolId、UnitId、FunctionCode、ByteCount
        /// 5. Modbus 异常响应返回 false
        /// 6. 超时或取消返回 false
        /// 7. finally 释放资源
        /// </summary>
        public async Task<bool> TestAsync(
            string host,
            int port,
            byte unitId,
            int timeoutMs,
            CancellationToken ct = default)
        {
            // 全部使用局部对象，不访问生产通信字段
            TcpClient? tcpClient = null;
            NetworkStream? stream = null;
            var sw = Stopwatch.StartNew();

            try
            {
                // 统一一个总超时令牌
                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                var token = linkedCts.Token;

                // 1. TCP Connect
                tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(host, port, token).ConfigureAwait(false);

                if (!tcpClient.Connected)
                {
                    _logger.LogWarning("[临时测试][PLC] TCP 连接失败: {Host}:{Port}", host, port);
                    return false;
                }

                // 2. 获取 NetworkStream
                stream = tcpClient.GetStream();

                // 3. 使用局部 TransactionId 构建 Modbus TCP 请求帧（FC03 读 DT120）
                ushort transactionId = 1;
                var request = ModbusTcpMessageHelper.CreateReadHoldingRegistersRequest(
                    transactionId: transactionId,
                    unitId: unitId,
                    startAddress: 120, // DT120
                    quantity: 1);

                // 4. 发送请求
                await stream.WriteAsync(request, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);

                // 5. 读取 6 字节 MBAP 头
                var mbapHeader = new byte[6];
                await ReadExactAsync(stream, mbapHeader, token).ConfigureAwait(false);

                // 6. 解析 MBAP
                ushort responseTid = BinaryPrimitives.ReadUInt16BigEndian(mbapHeader.AsSpan(0, 2));
                ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(mbapHeader.AsSpan(2, 2));
                ushort length = BinaryPrimitives.ReadUInt16BigEndian(mbapHeader.AsSpan(4, 2));

                // 7. 校验 MBAP
                if (responseTid != transactionId)
                {
                    _logger.LogWarning(
                        "[临时测试][PLC] TransactionId 不匹配: 期望={Expected}, 实际={Actual}",
                        transactionId, responseTid);
                    return false;
                }

                if (protocolId != 0)
                {
                    _logger.LogWarning(
                        "[临时测试][PLC] ProtocolId 不为 0: {ProtocolId}", protocolId);
                    return false;
                }

                if (length < 2 || length > 254)
                {
                    _logger.LogWarning(
                        "[临时测试][PLC] Length 超出范围: {Length}", length);
                    return false;
                }

                // 8. 按 Length 读取完整 body
                var body = new byte[length];
                await ReadExactAsync(stream, body, token).ConfigureAwait(false);

                // 9. 严格校验 FC03 响应
                if (body.Length < 2)
                {
                    _logger.LogWarning("[临时测试][PLC] Body 长度不足: {Bytes} 字节", body.Length);
                    return false;
                }

                if (body[0] != unitId)
                {
                    _logger.LogWarning(
                        "[临时测试][PLC] UnitId 不匹配: 期望={Expected}, 实际={Actual}",
                        unitId, body[0]);
                    return false;
                }

                byte functionCode = body[1];

                // Modbus 异常响应 → false
                if ((functionCode & 0x80) != 0)
                {
                    byte errorCode = body.Length >= 3 ? body[2] : (byte)0;
                    _logger.LogWarning(
                        "[临时测试][PLC] Modbus 异常响应: FC={FunctionCode}, ErrorCode={ErrorCode}",
                        functionCode, errorCode);
                    return false;
                }

                if (functionCode != 0x03)
                {
                    _logger.LogWarning(
                        "[临时测试][PLC] FunctionCode 不匹配: 期望=0x03, 实际=0x{FunctionCode:X2}",
                        functionCode);
                    return false;
                }

                if (body.Length != 5)
                {
                    _logger.LogWarning(
                        "[临时测试][PLC] Body 长度异常: 期望=5, 实际={Length}", body.Length);
                    return false;
                }

                if (body[2] != 2)
                {
                    _logger.LogWarning(
                        "[临时测试][PLC] ByteCount 异常: 期望=2, 实际={ByteCount}", body[2]);
                    return false;
                }

                sw.Stop();
                _logger.LogInformation(
                    "[临时测试][PLC] 测试成功: {Host}:{Port} UnitId={UnitId} Length={Length} ElapsedMs={ElapsedMs}",
                    host, port, unitId, length, sw.ElapsedMilliseconds);
                return true;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _logger.LogWarning("[临时测试][PLC] 测试超时或取消: {Host}:{Port} ElapsedMs={ElapsedMs}",
                    host, port, sw.ElapsedMilliseconds);
                return false;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "[临时测试][PLC] Socket 异常: {Message}", ex.Message);
                return false;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "[临时测试][PLC] IO 异常: {Message}", ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[临时测试][PLC] 未预期异常: {Message}", ex.Message);
                return false;
            }
            finally
            {
                // finally 释放所有局部资源
                if (stream is not null)
                {
                    try { stream.Dispose(); } catch { /* 忽略释放异常 */ }
                }
                if (tcpClient is not null)
                {
                    try { tcpClient.Dispose(); } catch { /* 忽略释放异常 */ }
                }
            }
        }

        /// <summary>
        /// 精确读取指定长度的数据到缓冲区。
        /// 支持 TCP 分包。连接关闭时抛出 IOException。
        /// </summary>
        private static async Task ReadExactAsync(
            NetworkStream stream,
            Memory<byte> buffer,
            CancellationToken ct)
        {
            int offset = 0;

            while (offset < buffer.Length)
            {
                int bytesRead = await stream
                    .ReadAsync(buffer[offset..], ct)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    throw new IOException(
                        $"连接已关闭，期望读取 {buffer.Length} 字节，实际读取 {offset} 字节");
                }

                offset += bytesRead;
            }
        }
    }
}
