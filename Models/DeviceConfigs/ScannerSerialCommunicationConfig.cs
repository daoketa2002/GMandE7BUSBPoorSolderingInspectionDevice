using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceConfigs
{
    /// <summary>
    /// 霍尼韦尔 H1900 扫描枪配置
    /// 通信方式：USB连接，串口通信（虚拟串口）
    /// 心跳检测：DataActivity模式，通过检测数据活动判断设备在线状态
    /// </summary>
    public class ScannerSerialCommunicationConfig : INotifyPropertyChanged
    {
        private string _serialNumber = "COM9";
        private int _baudRate = 115200;
        private string _parity = "None";
        private int _dataBits = 8;
        private string _stopBits = "1";
        private string _flowControl = "None";
        private HealthCheckMode _healthCheckMode = HealthCheckMode.DataActivity;
        private int _healthCheckIntervalSeconds = 5;
        private int _lastDataTimeoutSeconds = 30;

        /// <summary>
        /// 串口号（如COM9）
        /// </summary>
        public string SerialNumber
        {
            get => _serialNumber;
            set { _serialNumber = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 波特率（默认115200）
        /// </summary>
        public int BaudRate
        {
            get => _baudRate;
            set { _baudRate = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 校验位：None / Odd / Even
        /// </summary>
        public string Parity
        {
            get => _parity;
            set { _parity = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 数据位（5-8）
        /// </summary>
        public int DataBits
        {
            get => _dataBits;
            set { _dataBits = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 停止位：1 / 1.5 / 2
        /// </summary>
        public string StopBits
        {
            get => _stopBits;
            set { _stopBits = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 流控制：None / XOnXOff / RequestToSend
        /// </summary>
        public string FlowControl
        {
            get => _flowControl;
            set { _flowControl = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 心跳检测模式（默认：DataActivity）
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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}