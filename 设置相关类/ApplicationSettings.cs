using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
//using GMandE7BUSBPoorSolderingInspectionDevice.Services.通用;

namespace GMandE7BUSBPoorSolderingInspectionDevice.设置相关类
{
    public class ApplicationSettings
    {
        public string xyz { get; set; } = "1234567";

        /// <summary>
        /// 扫码器
        /// </summary>
        public ScannerSerialCommunicationSettings ScannerSerialCommunication { get; set; } = new ScannerSerialCommunicationSettings();
      
        /// <summary>
        /// CA550
        /// </summary>
        public CA550SerialCommunicationSettings CA550SerialCommunication { get; set; } = new CA550SerialCommunicationSettings();


        /// <summary>
        /// 已弃用，动作控制PLC
        /// </summary>
        public TcpServerPLCMotionControlSettings TcpServerPLCMotion { get; set; } = new TcpServerPLCMotionControlSettings();
        /// <summary>
        /// 动作控制PLC
        /// </summary>
        public TcpClientPLCMotionControlSettings TcpClientPLCMotion { get; set; } = new TcpClientPLCMotionControlSettings();


        /// <summary>
        /// 已弃用，TC模块PLC
        /// </summary>
        public TcpServerPLCTemperatureModuleSettings TcpServerPLCTC { get; set; } = new TcpServerPLCTemperatureModuleSettings();
        /// <summary>
        /// TC模块PLC
        /// </summary>
        public TcpClientPLCTemperatureModuleSettings TcpClientPLCTC { get; set; } = new TcpClientPLCTemperatureModuleSettings();


        /// <summary>
        /// DAQ-9600
        /// </summary>
        public TcpClientGWInstekSettings TcpClientGWInstek { get; set; } = new TcpClientGWInstekSettings();


        public TATRuntimeSettings runtimeSettings { get; set; } =   new TATRuntimeSettings();
        


        public class ScannerSerialCommunicationSettings//扫码器RS232C通信设定实际  com9
        {

            public string? SerialNumber { get; set; }//串口号
            public int BaudRate { get; set; } = 115200;//波特率
            public string? Parity { get; set; } = "None";//"None","Odd","Even"  校验位
            public int DataBits { get; set; } = 8;//数据长度，数据位 5到8
            public string? StopBits { get; set; } = "1";//"1","1.5","2" 停止位
            public string? FlowControl { get; set; } = "None";//"None","XOnXOff","RequestToSend",etc


            // 新增：心跳检测模式（具体去服务类通用组件看）
            public HealthCheckMode HealthCheckMode { get; set; } = HealthCheckMode.Disabled;

            // 新增：配合 DataActivity 使用的超时时间（单位：秒）
            public int? LastDataTimeoutSeconds { get; set; } = 30;


            // 可选：心跳检查间隔（秒）
            public int HealthCheckIntervalSeconds { get; set; } = 5;



        }



        public class CA550SerialCommunicationSettings//CA550的RS232C设定，实际地址是com7 none
        {
            public string? SerialNumber { get; set; } //端口号

            public int BaudRate { get; set; } = 9600; //波特率

            public string? Parity { get; set; } = "None"; //"None","Odd","Even" 校验位

            public int DataBits { get; set; } = 8; //数据长度，数据位 5到8

            public string? StopBits { get; set; } = "1"; //"1","1.5","2" 停止位

            public string? FlowControl { get; set; } = "None"; //"None","XOnXOff","RequestToSend",etc

            // 新增：心跳检测模式（具体去服务类通用组件看）
            public HealthCheckMode HealthCheckMode { get; set; } = HealthCheckMode.Disabled;

            // 新增：配合 DataActivity 使用的超时时间（单位：秒）
            public int? LastDataTimeoutSeconds { get; set; } = 30;


            // 可选：心跳检查间隔（秒）
            public int HealthCheckIntervalSeconds { get; set; } = 5;


        }





        /// <summary>
        /// 已弃用！！！动作控制PLC，电脑是tcp服务端，默认监听端口7070
        /// </summary>
        public class TcpServerPLCMotionControlSettings//主机（服务端）连PLC（客户端）
        {
            public int ListeningPort { get; set; } = 7070;
            public int MaxConnections { get; set; } = 10;
            public string? TriggerCommand { get; set; } = "Cstart";



            public string? SchemePath { get; set; } = "未设定";

            public string? SchemePassword { get; set; } = "";

            public string? SchemePath2 { get; set; } = "未设定";

            public string? SchemePassword2 { get; set; } = "";


            // 新增：心跳检测模式（具体去服务类通用组件看）
            public HealthCheckMode HealthCheckMode { get; set; } = HealthCheckMode.Disabled;

            // 新增：配合 DataActivity 使用的超时时间（单位：秒）
            public int? LastDataTimeoutSeconds { get; set; } = 30;


            // 可选：心跳检查间隔（秒）
            public int HealthCheckIntervalSeconds { get; set; } = 5;
        }


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
        /// 已弃用！！！TC模块PLC，电脑是tcp服务端，默认监听端口6060
        /// </summary>
        public class TcpServerPLCTemperatureModuleSettings//主机（服务端）连PLC（客户端）
        {
            public int ListeningPort { get; set; } = 6060;
            public int MaxConnections { get; set; } = 10;
            public string? TriggerCommand { get; set; } = "Cstart";



            public string? SchemePath { get; set; } = "未设定";

            public string? SchemePassword { get; set; } = "";

            public string? SchemePath2 { get; set; } = "未设定";

            public string? SchemePassword2 { get; set; } = "";


            // 新增：心跳检测模式（具体去服务类通用组件看）
            public HealthCheckMode HealthCheckMode { get; set; } = HealthCheckMode.Disabled;

            // 新增：配合 DataActivity 使用的超时时间（单位：秒）
            public int? LastDataTimeoutSeconds { get; set; } = 30;


            // 可选：心跳检查间隔（秒）
            public int HealthCheckIntervalSeconds { get; set; } = 5;
        }


        /// <summary>
        /// TC模块，电脑是tcp客户端，默认端口602，地址192.168.1.2，modbus协议
        /// </summary>
        public class TcpClientPLCTemperatureModuleSettings//主机（客户端）连PLC（服务端）
        {
            public string? Host { get; set; } = "192.168.1.2"; //服务器地址
            public int Port { get; set; } = 602; //服务器端口

            public string? TriggerCommand { get; set; } = "Cstart";


            // 新增：心跳检测模式（具体去服务类通用组件看）
            public HealthCheckMode HealthCheckMode { get; set; } = HealthCheckMode.Disabled;

            // 新增：配合 DataActivity 使用的超时时间（单位：秒）
            public int? LastDataTimeoutSeconds { get; set; } = 30;


            // 可选：心跳检查间隔（秒）
            public int HealthCheckIntervalSeconds { get; set; } = 5;

            //最大重连次数
            public int? MaxReconnectAttempts { get; set; } = 5;
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

     

        public class TATRuntimeSettings
        {
            public string? SaveAddressForTestResults { get; set; } = "Default";
        }
    }
}
