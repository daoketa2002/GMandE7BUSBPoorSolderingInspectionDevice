// ============================================================
// 文件: Interfaces/IPlcConnectionTester.cs
// 描述: 独立无状态 PLC 临时测试器接口
// 职责: 使用局部 TcpClient/NetworkStream 测试指定 PLC 配置是否可达，
//       不访问生产通信对象内部状态。
// ============================================================

using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 独立无状态 PLC 临时测试器。
    /// 每次调用创建全新 TcpClient，用完即释放。
    /// 不访问 _tcpClient / _networkStream / _pendingRequests 等生产通信状态。
    /// </summary>
    public interface IPlcConnectionTester
    {
        /// <summary>
        /// 测试指定 PLC 配置是否可达且 Modbus 通信正常。
        /// </summary>
        /// <param name="host">PLC IP 地址</param>
        /// <param name="port">Modbus TCP 端口号</param>
        /// <param name="unitId">Modbus 从站 ID</param>
        /// <param name="timeoutMs">连接和通信总超时（毫秒）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>通信正常返回 true，否则 false</returns>
        Task<bool> TestAsync(
            string host,
            int port,
            byte unitId,
            int timeoutMs,
            CancellationToken ct = default);
    }
}
