using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.InterFaces
{
    /// <summary>
    /// PLC动作控制TCP服务接口
    /// </summary>
    public interface ITcpServerPLCMotionService
    {
        /// <summary>
        /// 获取当前TCP连接是否已成功建立
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 当发生通信相关事件时触发的通知事件
        /// </summary>
        event EventHandler<CommunicationNotification> OnNotification;

        /// <summary>
        /// 当接收到原始数据时触发
        /// </summary>
        event EventHandler<string> DataReceived;

        /// <summary>
        /// 异步启动TCP服务：加载配置、监听连接、启动数据接收循环和心跳检测机制
        /// </summary>
        /// <returns>表示异步启动操作的任务</returns>
        Task StartAsync();

        /// <summary>
        /// 异步停止TCP服务：关闭连接，取消后台读取任务，释放所有资源
        /// </summary>
        /// <returns>表示异步停止操作的任务</returns>
        Task StopAsync();

        /// <summary>
        /// 执行一个需要响应的TCP操作，发送指定命令并等待响应
        /// 验证响应是否以指定前缀开头
        /// </summary>
        /// <param name="command">要发送到PLC的命令字符串</param>
        /// <param name="expectedPrefix">期望响应消息的前缀</param>
        /// <param name="timeoutMs">等待响应的超时时间（毫秒）</param>
        /// <returns>如果收到匹配的响应返回true，否则返回false</returns>
        Task<bool> ExecuteOperationAsync(string command, string expectedPrefix, int timeoutMs = 3000);

        /// <summary>
        /// 执行一个需要响应的TCP操作，使用自定义异步验证函数判断响应是否有效
        /// </summary>
        /// <param name="responseValidator">一个异步函数，接收响应字符串并返回布尔值</param>
        /// <param name="command">要发送到PLC的命令字符串</param>
        /// <param name="timeoutMs">等待响应的超时时间（毫秒）</param>
        /// <returns>如果验证通过返回true，否则返回false</returns>
        Task<bool> ExecuteOperationAsync(Func<string, Task<bool>> responseValidator, string command, int timeoutMs = 3000);

        /// <summary>
        /// 异步发送一条原始命令到PLC，不等待响应
        /// </summary>
        /// <param name="command">要发送的命令字符串</param>
        /// <returns>表示异步发送操作的任务</returns>
        Task SendCommandAsync(string command);
    }
}