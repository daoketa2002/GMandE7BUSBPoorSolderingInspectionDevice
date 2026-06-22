using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs
{
    /// <summary>
    /// 系统设备总配置模型
    /// 包含三个设备的配置以及PLC通信测试开关
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
        /// 是否开启PLC通信测试功能（控制主菜单按钮显隐）
        /// </summary>
        public bool IsPlcCommunicationTestEnabled { get; set; } = false;

        /// <summary>
        /// DAQ-9600
        /// </summary>
        public TcpClientGWInstekSettings TcpClientGWInstek { get; set; } = new TcpClientGWInstekSettings();

        /// <summary>
        /// 动作控制PLC
        /// </summary>
        public TcpClientPLCMotionControlSettings TcpClientPLCMotion { get; set; } = new TcpClientPLCMotionControlSettings();

        /// <summary>
        /// 动作控制PLC，电脑是tcp客户端，默认端口502，地址192.168.1.3，modbus协议
        /// </summary>
        public class TcpClientPLCMotionControlSettings//主机（客户端）连PLC（服务端）
        {
            public string? Host { get; set; } = "192.168.1.3"; //服务器地址
            public int Port { get; set; } = 502; //服务器端口

            public string? TriggerCommand { get; set; } = "Cstart";


            // 新增：心跳检测模式（具体去服务类通用组件看）
            public HealthCheckMode HealthCheckMode { get; set; } = HealthCheckMode.Disabled;

            // 新增：配合 DataActivity 使用的超时时间（单位：秒）
            public int? LastDataTimeoutSeconds { get; set; } = 30;


            // 可选：心跳检查间隔（秒）
            public int HealthCheckIntervalSeconds { get; set; } = 5;

            //最大重连次数
            public int? MaxReconnectAttempts { get; set; } = 12;
            public int? ReceiveTimeoutMs { get; set; } = 5000;
            public int? SendTimeoutMs { get; set; } = 5000;
            public int? ReconnectDelayMs { get; set; } = 2000;


        }


        /// <summary>
        /// DAQ-9600，电脑是tcp客户端
        /// </summary>
        public class TcpClientGWInstekSettings//主机（客户端）连读码器A（服务端）   DAQ-9600，实际的网络地址可以在web端改
        {
            public string? Host { get; set; } = "192.168.1.4"; //服务器地址
            public int Port { get; set; } = 5025; //服务器端口

            public string? TriggerCommand { get; set; } = "Cstart";


            // 新增：心跳检测模式（具体去服务类通用组件看）
            public HealthCheckMode HealthCheckMode { get; set; } = HealthCheckMode.Disabled;

            // 新增：配合 DataActivity 使用的超时时间（单位：秒）
            public int? LastDataTimeoutSeconds { get; set; } = 30;


            // 可选：心跳检查间隔（秒）
            public int HealthCheckIntervalSeconds { get; set; } = 5;

        }
    }
}