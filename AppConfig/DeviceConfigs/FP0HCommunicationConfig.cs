using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs
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

        /// <summary>
        /// 发送超时时间（毫秒）
        /// </summary>

        /// <summary>
        /// 重连延迟（毫秒）
        /// </summary>

        /// <summary>
        /// 最大重连次数
        /// </summary>

        /// <summary>
        /// 心跳检测模式（默认：禁用）
        /// </summary>

        /// <summary>
        /// 心跳检查间隔（秒）
        /// </summary>

        /// <summary>
        /// 配合 DataActivity 模式使用的无数据超时时间（秒）
        /// 超过此时间未收到数据则判定连接断开，触发重连
        /// </summary>

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
