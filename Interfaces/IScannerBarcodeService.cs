using System;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 扫描枪条码服务接口
    /// 解耦扫描枪硬件与各ViewModel，提供统一的条码接收和解析能力
    /// 
    /// 解析格式示例："T998248391,250919,00004Z"
    ///   第1段 → 机种名称（Model）
    ///   第2段 → 日期（忽略）
    ///   第3段 → 序列号（忽略）
    /// </summary>
    public interface IScannerBarcodeService
    {
        /// <summary>
        /// 条码扫描完成事件（已解析）
        /// </summary>
        event EventHandler<BarcodeParsedEventArgs>? BarcodeParsed;

        /// <summary>
        /// 扫描枪连接状态变更事件
        /// </summary>
        event EventHandler<bool>? ConnectionStateChanged;

        /// <summary>
        /// 扫描枪是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 扫描枪端口名
        /// </summary>
        string PortName { get; }

        /// <summary>
        /// 连接扫描枪
        /// </summary>
        bool Connect(string portName);

        /// <summary>
        /// 断开扫描枪
        /// </summary>
        void Disconnect();                   // 保留同步版本（兼容）
        Task DisconnectAsync();                 // ⭐ 新增异步版本
    }

    /// <summary>
    /// 条码解析完成事件参数
    /// </summary>
    public class BarcodeParsedEventArgs : EventArgs
    {
        /// <summary>
        /// 原始条码
        /// </summary>
        public string RawBarcode { get; }

        /// <summary>
        /// 机种名称（型号）
        /// </summary>
        public string ModelName { get; }

        /// <summary>
        /// 日期段（可能为空）
        /// </summary>
        public string? DatePart { get; }

        /// <summary>
        /// 序列号段（可能为空）
        /// </summary>
        public string? SerialPart { get; }

        /// <summary>
        /// 接收时间戳
        /// </summary>
        public DateTime Timestamp { get; }

        public BarcodeParsedEventArgs(string rawBarcode, string modelName, string? datePart, string? serialPart)
        {
            RawBarcode = rawBarcode;
            ModelName = modelName;
            DatePart = datePart;
            SerialPart = serialPart;
            Timestamp = DateTime.Now;
        }
    }
}