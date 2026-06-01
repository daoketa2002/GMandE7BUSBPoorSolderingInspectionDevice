using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制
{
    public class AlarmState
    {
        public string Key { get; set; }
        public int ErrorCode { get; set; }
        public string Name { get; set; }
        public bool IsActive { get; set; }
        public DateTime? LastTriggerTime { get; set; }
        public DateTime? LastClearTime { get; set; }
    }
}
