using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 表示一条串口通信过程中的通知消息，用于记录系统事件、状态变化或异常信息。
    /// 包含消息类型、内容、来源模块和时间戳，适用于日志记录、UI 提示或事件监控。
    /// 该类是不可变的（只读属性），确保通知在发布后不会被意外修改。
    /// </summary>
    public class CommunicationNotification
    {
        /// <summary>
        /// 获取通知的类型（如信息、警告、错误、成功等）。
        /// 用于区分消息的严重级别和处理方式。
        /// </summary>
        public NotificationType Type { get; }

        /// <summary>
        /// 获取通知的具体消息内容，描述事件详情。
        /// 例如："串口已连接"、"命令超时"、"设备返回错误码" 等。
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// 获取通知的来源模块或组件名称（可选）。
        /// 用于标识是哪个功能模块或测试步骤触发了该通知。
        /// 例如："PowerControlStep"、"SerialPortService"、"TemperatureSensor" 等。
        /// 若未指定，则为空字符串。
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// 获取通知生成的时间戳（本地时间）。
        /// 精确到毫秒，可用于分析事件发生顺序和响应延迟。
        /// </summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// 初始化一个新的通知实例。
        /// </summary>
        /// <param name="type">通知的类型，如 Info、Warning、Error 等。</param>
        /// <param name="message">通知的消息内容，不能为空或 null。</param>
        /// <param name="source">通知的来源标识（可选），可用于过滤和追踪。</param>
        public CommunicationNotification(NotificationType type, string message, string source = "")
        {
            Type = type;
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Source = source ?? "";
            Timestamp = DateTime.Now;
        }

        /// <summary>
        /// 返回该通知的格式化字符串表示，便于日志输出或界面显示。
        /// 格式示例：[Error] 14:30:25 [PowerCheck] 命令超时 (3000ms)
        /// </summary>
        /// <returns>格式化的字符串，包含类型、时间、来源和消息。</returns>
        public override string ToString() =>
            $"[{Type}] {Timestamp:HH:mm:ss} [{Source}] {Message}";
    }
}

