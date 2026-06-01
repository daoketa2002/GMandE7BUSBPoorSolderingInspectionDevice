using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 心跳检测模式
    /// </summary>
    public enum HealthCheckMode
    {
        /// <summary>
        /// 发送 PING 命令并等待 ACK/OK 响应
        /// </summary>
        CommandResponse,

        /// <summary>
        /// 只要最近一段时间内收到过任意数据，就认为设备在线
        /// </summary>
        DataActivity,

        /// <summary>
        /// 定期读取一个状态命令（如 STATUS），验证返回值
        /// </summary>
        StatusQuery,

        /// <summary>
        /// 禁用心跳，仅靠通信异常触发重连
        /// </summary>
        Disabled
    }
}
