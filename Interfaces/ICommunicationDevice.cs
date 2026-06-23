// ============================================================
// 文件: Interfaces/Devices/ICommunicationDevice.cs
// 描述: 所有硬件通信设备的统一抽象接口
// 职责:
//   1. 统一设备生命周期管理（连接/断开）
//   2. 统一连接状态变更事件
//   3. 让 DeviceConnectionManager 只依赖接口，不依赖具体驱动
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices
{
    /// <summary>
    /// 通用通信设备抽象接口
    /// 所有硬件设备（PLC、万用表、扫描枪）必须实现此接口
    /// 使 DeviceConnectionManager 与具体硬件型号解耦
    /// </summary>
    public interface ICommunicationDevice
    {
        /// <summary>
        /// 设备是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 异步连接设备
        /// 由 DeviceConnectionManager 调用，内部从属性读取配置参数
        /// </summary>
        /// <param name="ct">取消令牌，用于超时或应用退出时中断连接</param>
        /// <returns>连接成功返回 true，失败返回 false</returns>
        Task<bool> ConnectAsync(CancellationToken ct = default);

        /// <summary>
        /// 异步断开设备连接
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// 连接状态变更事件
        /// true = 已连接，false = 已断开
        /// DeviceConnectionManager 订阅此事件统一转发给所有 ViewModel
        /// </summary>
        event EventHandler<bool>? ConnectionStateChanged;
    }
}