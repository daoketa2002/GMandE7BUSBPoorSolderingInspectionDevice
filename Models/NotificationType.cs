using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 定义通信模块中通知消息的类型或严重级别。
    /// 用于区分不同场景下的事件性质，便于日志记录、UI 显示或告警处理。
    /// </summary>
    public enum NotificationType
    {
        /// <summary>
        /// 信息性通知：表示正常的操作提示或状态更新。
        /// 例如：串口已打开、开始执行测试流程等。
        /// 通常以蓝色或灰色图标显示。
        /// </summary>
        Info,

        /// <summary>
        /// 警告通知：表示出现非致命问题，但流程可继续执行。
        /// 例如：非关键步骤失败、响应时间偏长、电压轻微偏移等。
        /// 通常以黄色图标显示，需关注但不中断流程。
        /// </summary>
        Warning,

        /// <summary>
        /// 错误通知：表示发生错误，可能导致当前操作失败。
        /// 例如：命令超时、响应格式错误、校验失败等。
        /// 通常以红色图标显示，关键步骤错误将终止流程。
        /// </summary>
        Error,

        /// <summary>
        /// 严重错误通知：表示系统级或致命故障，需立即处理。
        /// 例如：串口设备丢失、硬件异常、通信完全中断等。
        /// 通常伴随自动停止测试流程，需人工干预恢复。
        /// 以深红色或闪烁图标突出显示。
        /// </summary>
        Critical,

        /// <summary>
        /// 成功通知：表示某个操作或整个流程成功完成。
        /// 例如：设备响应正确、测试通过、连接建立成功等。
        /// 通常以绿色图标显示，用于正向反馈。
        /// </summary>
        Success,

        /// <summary>
        /// 连接恢复通知：表示通信中断后已自动恢复连接。
        /// 用于通知用户系统已从异常状态恢复，可继续正常通信。
        /// 常用于具备重连机制的串口服务中。
        /// 可配合 Success 类型使用，但语义更具体。
        /// </summary>
        ConnectionRestored
    }
}
