using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs
{
    /// <summary>
    /// 固纬 GDM-9060 万用表配置
    /// 通信方式：LAN网线连接，SCPI协议 over TCP
    /// 心跳检测：CommandResponse模式，通过*IDN?命令检测设备在线状态
    /// </summary>
    public class GDM9060CommunicationConfig : INotifyPropertyChanged
    {
        private double _continuityThresholdOhm = 10.0;
        private string _ipAddress = "192.168.1.4";
        private int _port = 5025;
        private int _receiveTimeoutMs = 5000;
        private int _sendTimeoutMs = 5000;
        private HealthCheckMode _healthCheckMode = HealthCheckMode.CommandResponse;
        private int _healthCheckIntervalSeconds = 5;
        private int _lastDataTimeoutSeconds = 30;

        /// <summary>
        /// 万用表IP地址
        /// </summary>
        public string IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// SCPI端口号（默认5025）
        /// </summary>
        public int Port
        {
            get => _port;
            set { _port = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 接收超时时间（毫秒）
        /// </summary>
        public int ReceiveTimeoutMs
        {
            get => _receiveTimeoutMs;
            set { _receiveTimeoutMs = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 发送超时时间（毫秒）
        /// </summary>
        public int SendTimeoutMs
        {
            get => _sendTimeoutMs;
            set { _sendTimeoutMs = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 心跳检测模式（默认：CommandResponse）
        /// </summary>
        public HealthCheckMode HealthCheckMode
        {
            get => _healthCheckMode;
            set { _healthCheckMode = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 心跳检查间隔（秒）
        /// </summary>
        public int HealthCheckIntervalSeconds
        {
            get => _healthCheckIntervalSeconds;
            set { _healthCheckIntervalSeconds = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 数据活动超时时间（秒）
        /// </summary>
        public int LastDataTimeoutSeconds
        {
            get => _lastDataTimeoutSeconds;
            set { _lastDataTimeoutSeconds = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 导通阈值(Ω)，用于导通模式判定 OPEN/SHORT。
        /// 范围 1~1000Ω，由系统设置页保存前校验。
        /// </summary>
        public double ContinuityThresholdOhm
        {
            get => _continuityThresholdOhm;
            set { _continuityThresholdOhm = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
