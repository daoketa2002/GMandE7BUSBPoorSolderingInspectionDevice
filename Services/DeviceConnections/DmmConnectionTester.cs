// ============================================================
// 文件: Services/DeviceConnections/DmmConnectionTester.cs
// 描述: 独立无状态 DMM 临时测试器实现
// 职责: 使用全新局部 TcpClient 测试 SCPI *IDN? 通信，
//       不访问生产 GwInstekGDM9060Driver 内部状态。
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections
{
    /// <summary>
    /// 独立无状态 DMM 临时测试器。
    /// 每次调用 TestAsync 创建全新 TcpClient，用完即释放。
    /// 仅使用局部变量，不访问生产万用表驱动内部状态。
    /// </summary>
    public sealed class DmmConnectionTester : IDmmConnectionTester
    {
        private readonly ILogger<DmmConnectionTester> _logger;

        public DmmConnectionTester(ILogger<DmmConnectionTester> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 测试指定 DMM 配置是否可达且 SCPI *IDN? 响应正常。
        /// 流程：
        /// 1. TCP Connect
        /// 2. 获取 NetworkStream
        /// 3. 发送 "*IDN?\r\n"
        /// 4. 读取响应直到 \n
        /// 5. 校验非空
        /// 6. 超时或取消返回 null
        /// 7. finally 释放资源
        /// </summary>
        public async Task<string?> TestAsync(
            string host,
            int port,
            int timeoutMs,
            CancellationToken ct = default)
        {
            // 全部使用局部对象，不访问生产通信字段
            TcpClient? tcpClient = null;
            NetworkStream? stream = null;

            try
            {
                // 1. TCP Connect（最多用一半超时）
                tcpClient = new TcpClient();
                using var connectTimeoutCts = new CancellationTokenSource(timeoutMs / 2);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeoutCts.Token);

                await tcpClient.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);

                if (!tcpClient.Connected)
                {
                    _logger.LogWarning("[临时测试][DMM] TCP 连接失败: {Host}:{Port}", host, port);
                    return null;
                }

                // 2. 获取 NetworkStream
                stream = tcpClient.GetStream();
                stream.ReadTimeout = timeoutMs / 2;
                stream.WriteTimeout = timeoutMs / 2;

                // 3. 发送 "*IDN?\r\n"
                var idnCommand = "*IDN?\r\n";
                var cmdBytes = Encoding.ASCII.GetBytes(idnCommand);
                await stream.WriteAsync(cmdBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                // 4. 读取响应直到 \n 或超时
                using var responseStream = new MemoryStream();
                var buffer = new byte[4096];
                bool foundNewline = false;

                using var readTimeoutCts = new CancellationTokenSource(timeoutMs / 2);
                using var readLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readTimeoutCts.Token);

                while (!foundNewline)
                {
                    int bytesRead = await stream.ReadAsync(
                        buffer, 0, buffer.Length, readLinkedCts.Token).ConfigureAwait(false);

                    if (bytesRead == 0) break;

                    responseStream.Write(buffer, 0, bytesRead);

                    var responseSoFar = Encoding.ASCII.GetString(
                        responseStream.ToArray(), 0, (int)responseStream.Length);
                    if (responseSoFar.Contains('\n'))
                        foundNewline = true;
                }

                // 5. 解析和校验响应
                var rawResponse = Encoding.ASCII.GetString(
                    responseStream.ToArray(), 0, (int)responseStream.Length);
                var idnResponse = rawResponse.TrimEnd('\r', '\n', ' ');

                if (string.IsNullOrWhiteSpace(idnResponse))
                {
                    _logger.LogWarning("[临时测试][DMM] *IDN? 响应为空: {Host}:{Port}", host, port);
                    return null;
                }

                _logger.LogInformation(
                    "[临时测试][DMM] 测试成功: {Host}:{Port} IDN={Idn}",
                    host, port, idnResponse);

                return idnResponse;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[临时测试][DMM] 测试超时或取消: {Host}:{Port}", host, port);
                return null;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "[临时测试][DMM] Socket 异常: {Message}", ex.Message);
                return null;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "[临时测试][DMM] IO 异常: {Message}", ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[临时测试][DMM] 未预期异常: {Message}", ex.Message);
                return null;
            }
            finally
            {
                if (stream is not null)
                {
                    try { stream.Dispose(); } catch { }
                }
                if (tcpClient is not null)
                {
                    try { tcpClient.Dispose(); } catch { }
                }
            }
        }
    }
}
