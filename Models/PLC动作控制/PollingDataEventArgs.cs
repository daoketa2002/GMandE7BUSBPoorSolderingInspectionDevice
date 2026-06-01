using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制
{
    public class PollingDataEventArgs : EventArgs
    {
        public string Key { get; }
        public string DisplayName { get; }
        public ModbusResponse Response { get; }

        public PollingDataEventArgs(string key, string displayName, ModbusResponse response)
        {
            Key = key;
            DisplayName = displayName;
            Response = response;
        }
    }
}
