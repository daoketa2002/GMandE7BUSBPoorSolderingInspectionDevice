// ============================================================
// 文件: Interfaces/IDmmConnectionTester.cs
// 描述: 独立无状态 DMM 临时测试器接口
// 职责: 使用局部 TcpClient 测试指定 DMM 配置是否可达，
//       发送 *IDN? 验证设备身份，不访问生产通信对象内部状态。
// ============================================================

using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 独立无状态 DMM 临时测试器。
    /// 每次调用创建全新 TcpClient，用完即释放。
    /// 不访问生产 GwInstekGDM9060Driver 内部状态。
    /// </summary>
    public interface IDmmConnectionTester
    {
        /// <summary>
        /// 测试指定 DMM 配置是否可达且 SCPI *IDN? 响应正常。
        /// </summary>
        /// <param name="host">DMM IP 地址</param>
        /// <param name="port">SCPI TCP 端口号</param>
        /// <param name="timeoutMs">连接和通信总超时（毫秒）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>成功返回 *IDN? 响应文本，失败返回 null</returns>
        Task<string?> TestAsync(
            string host,
            int port,
            int timeoutMs,
            CancellationToken ct = default);
    }
}
