using System;

using GMandE7BUSBPoorSolderingInspectionDevice.Models;

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

        /// <summary>原始扫码事件，保留完整字符串供作业员号等非产品条码场景使用。</summary>
        event EventHandler<BarcodeReceivedEventArgs>? BarcodeReceived;

        /// <summary>扫描枪超长帧被驱动层完整丢弃事件。</summary>
        event EventHandler<ScannerFrameRejectedEventArgs>? BarcodeFrameRejected;

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
        /// 连接扫描枪（保留兼容，实际连接由 DeviceConnectionManager 管理）
        /// </summary>
        bool Connect(string portName);

        /// <summary>
        /// 断开扫描枪（同步版本，兼容旧代码）
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 断开扫描枪（异步版本）
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// ⭐ 异步初始化服务
        /// 在 DeviceConnectionManager 完成硬件连接后调用
        /// 同步硬件连接状态并激活条码解析功能
        /// </summary>
        Task InitializeAsync();
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

        /// <summary>
        /// 是否符合运行页使用的产品条码格式和基础字段校验。
        /// </summary>
        public bool IsProductBarcodeValid { get; }

        /// <summary>
        /// 产品条码无效时面向操作员的固定失败原因；有效时为空。
        /// </summary>
        public string? ParseFailureReason { get; }

        public BarcodeParsedEventArgs(
            string rawBarcode,
            string modelName,
            string? datePart,
            string? serialPart,
            bool isProductBarcodeValid,
            string? parseFailureReason)
        {
            RawBarcode = rawBarcode ?? string.Empty;
            ModelName = modelName ?? string.Empty;
            DatePart = datePart;
            SerialPart = serialPart;
            Timestamp = DateTime.Now;
            IsProductBarcodeValid = isProductBarcodeValid;
            ParseFailureReason = parseFailureReason;
        }
    }
}
