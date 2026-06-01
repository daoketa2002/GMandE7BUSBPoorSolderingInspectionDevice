using System;
using System.Collections.Generic;
using System.Text;

namespace GMandE7BUSBPoorSolderingInspectionDevice.PlcCommunicationModule
{
    /// <summary>
    /// PLC连接状态
    /// </summary>
    public class PlcConnectionState
    {
        public string PlcId { get; set; }
        public bool IsConnected { get; set; }
        public DateTime LastConnectionTime { get; set; }
        public DateTime LastErrorTime { get; set; }
        public string LastError { get; set; }
        public int ReconnectCount { get; set; }
    }
}
