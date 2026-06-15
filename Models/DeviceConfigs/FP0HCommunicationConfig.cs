using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceConfigs
{
    /// <summary>
    /// FP0H AFP0HC32ET PLC 设备配置
    /// 通信方式：LAN网线连接，ModbusTCP协议
    /// </summary>
    public class FP0HCommunicationConfig : INotifyPropertyChanged
    {
        private string _ipAddress = "192.168.1.3";
        private int _port = 502;
        private int _slaveId = 1;
        private int _receiveTimeoutMs = 5000;
        private int _sendTimeoutMs = 5000;
        private int _reconnectDelayMs = 2000;
        private int _maxReconnectAttempts = 12;
        private int _healthCheckIntervalSeconds = 5;
        private HealthCheckMode _healthCheckMode = HealthCheckMode.Disabled;

        /// <summary>
        /// PLC IP地址
        /// </summary>
        public string IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// ModbusTCP端口号（默认502）
        /// </summary>
        public int Port
        {
            get => _port;
            set { _port = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Modbus从站ID
        /// </summary>
        public int SlaveId
        {
            get => _slaveId;
            set { _slaveId = value; OnPropertyChanged(); }
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
        /// 重连延迟（毫秒）
        /// </summary>
        public int ReconnectDelayMs
        {
            get => _reconnectDelayMs;
            set { _reconnectDelayMs = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 最大重连次数
        /// </summary>
        public int MaxReconnectAttempts
        {
            get => _maxReconnectAttempts;
            set { _maxReconnectAttempts = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 心跳检测模式（默认：禁用）
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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}