// ============================================================
// 文件: Interfaces/Devices/IScannerDevice.cs
// 描述: 扫描枪设备特有接口
// 职责:
//   1. 继承 ICommunicationDevice 通用生命周期
//   2. 暴露条码接收事件供 ScannerBarcodeService 订阅
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices
{
    /// <summary>
    /// 扫描枪设备抽象接口
    /// 继承通用通信设备接口，额外暴露扫码事件
    /// 实现类：HoneywellH1900Scanner
    /// </summary>
    public interface IScannerDevice : ICommunicationDevice
    {
        /// <summary>
        /// 条码接收事件
        /// 扫描枪接收到完整条码时触发
        /// ScannerBarcodeService 订阅此事件进行解析和转发
        /// </summary>
        event EventHandler<BarcodeReceivedEventArgs>? BarcodeReceived;

        /// <summary>扫描枪异常帧被拒绝事件。</summary>
        event EventHandler<ScannerFrameRejectedEventArgs>? BarcodeFrameRejected;

        /// <summary>在关闭串口前进入恢复排空状态。</summary>
        void BeginRecoveryDrain();

        /// <summary>等待串口重新打开后完成历史数据排空。</summary>
        Task<ScannerRecoveryDrainResult> WaitForRecoveryDrainAsync(
            CancellationToken cancellationToken = default);

        /// <summary>取消当前恢复排空状态，确保失败路径不会遗留门禁。</summary>
        void CancelRecoveryDrain();
    }
}
