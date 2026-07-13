using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs
{
    /// <summary>
    /// 系统设备总配置模型
    /// 包含三个设备的通信配置以及检测行为设置
    /// </summary>
    public class DeviceSettings
    {
        /// <summary>
        /// FP0H PLC配置
        /// </summary>
        public FP0HCommunicationConfig FP0HCommunication { get; set; } = new FP0HCommunicationConfig();

        /// <summary>
        /// 扫描仪H1900配置
        /// </summary>
        public ScannerSerialCommunicationConfig ScannerSerialCommunication { get; set; } = new ScannerSerialCommunicationConfig();

        /// <summary>
        /// GDM-9060万用表配置
        /// </summary>
        public GDM9060CommunicationConfig GDM9060Communication { get; set; } = new GDM9060CommunicationConfig();

        /// <summary>
        /// 单项 NG 后是否继续测试后续项目。
        /// true（默认）：保持当前行为，继续测完整个方案；
        /// false：首个 NG 后停止本轮，等待操作员复位或终了。
        /// </summary>
        public bool ContinueTestingAfterNg { get; set; } = true;

        /// <summary>
        /// 单项 NG 后继续测试时，最终 NG 结果是否保存到 CSV。
        /// 默认 true，用于兼容旧配置文件，避免升级后 NG 记录突然不落盘。
        /// </summary>
        public bool SaveNgInspectionResult { get; set; } = true;

    }
}
