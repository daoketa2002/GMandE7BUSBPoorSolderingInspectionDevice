using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// TCP客户端PLC动作控制服务接口
    /// </summary>
    public interface ITcpClientPLCMotionService
    {
        /// <summary>
        /// 获取当前TCP连接是否已成功建立
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 获取当前服务是否正在运行
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// 当发生通信相关事件时触发的通知事件
        /// </summary>
        event EventHandler<CommunicationNotification>? OnNotification;

        /// <summary>
        /// 当接收到Modbus响应时触发
        /// </summary>
        event EventHandler<Models.TCP报文相关.ModbusResponse>? ModbusResponseReceived;

        /// <summary>
        /// 当接收到原始数据时触发
        /// </summary>
        event EventHandler<byte[]>? RawDataReceived;

        /// <summary>
        /// 异步启动TCP客户端：加载配置、连接到服务器、启动数据接收循环和心跳检测机制
        /// </summary>
        /// <returns>表示异步启动操作的任务</returns>
        Task StartAsync();

        /// <summary>
        /// 异步停止TCP客户端：关闭连接，取消后台读取任务，释放所有资源
        /// </summary>
        /// <returns>表示异步停止操作的任务</returns>
        Task StopAsync();

        /// <summary>
        /// 强制中断当前连接
        /// </summary>
        void InterruptConnection();

        /// <summary>
        /// 强制中断所有正在进行的操作
        /// </summary>
        void InterruptAllOperations();

        /// <summary>
        /// 获取当前服务状态信息
        /// </summary>
        /// <returns>包含服务状态信息的字符串</returns>
        string GetStatusInfo();

        /// <summary>
        /// 执行Modbus读取操作
        /// </summary>
        /// <param name="functionCode">功能码</param>
        /// <param name="unitId">单元ID</param>
        /// <param name="startAddress">起始地址</param>
        /// <param name="quantity">数量</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>Modbus响应对象</returns>
        Task<Models.TCP报文相关.ModbusResponse?> ExecuteReadOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort quantity, int timeoutMs = 3000);

        /// <summary>
        /// 执行Modbus写入操作
        /// </summary>
        /// <param name="functionCode">功能码</param>
        /// <param name="unitId">单元ID</param>
        /// <param name="startAddress">起始地址</param>
        /// <param name="data">要写入的数据</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>Modbus响应对象</returns>
        Task<Models.TCP报文相关.ModbusResponse?> ExecuteWriteOperationAsync(byte functionCode, byte unitId, ushort startAddress, ushort[] data, int timeoutMs = 3000);

        /// <summary>
        /// 异步发送一条原始命令到PLC，不等待响应
        /// </summary>
        /// <param name="command">要发送的命令字符串</param>
        /// <returns>表示异步发送操作的任务</returns>
        Task SendCommandAsync(string command);

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

        // ========== 扩展 ==========

        /// <summary>
        /// 发送自定义 Modbus TCP 请求帧（必须包含有效 MBAP 头），并等待匹配事务ID的响应
        /// </summary>
        /// <param name="customRequestFrame">完整的 Modbus TCP 请求帧（至少8字节）</param>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <returns>Modbus响应对象</returns>
        Task<Models.TCP报文相关.ModbusResponse?> SendCustomModbusRequestAsync(byte[] customRequestFrame, int timeoutMs = 3000);
    }
}