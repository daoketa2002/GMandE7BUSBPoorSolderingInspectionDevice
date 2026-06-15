namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceConfigs
{
    /// <summary>
    /// 系统设备总配置模型
    /// 包含三个设备的配置以及PLC通信测试开关
    /// </summary>
    public class SystemDeviceSettings
    {
        /// <summary>
        /// FP0H PLC配置
        /// </summary>
        public FP0HConfig FP0HConfig { get; set; } = new FP0HConfig();

        /// <summary>
        /// 扫描仪H1900配置
        /// </summary>
        public ScannerConfig ScannerConfig { get; set; } = new ScannerConfig();

        /// <summary>
        /// GDM-9060万用表配置
        /// </summary>
        public GDM9060Config GDM9060Config { get; set; } = new GDM9060Config();

        /// <summary>
        /// 是否开启PLC通信测试功能（控制主菜单按钮显隐）
        /// </summary>
        public bool IsPlcCommunicationTestEnabled { get; set; } = false;
    }
}