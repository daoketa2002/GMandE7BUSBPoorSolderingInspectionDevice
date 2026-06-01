using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.InterFaces;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制
{
    /// <summary>
    /// TCP测试步骤类
    /// </summary>
    public class TcpTestStep
    {
        public string Name { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public ResponseMatchType MatchType { get; set; } = ResponseMatchType.StartsWith;
        public string ExpectedValue { get; set; } = string.Empty;
        public bool IsCritical { get; set; } = true;
        public int TimeoutMs { get; set; } = 3000;
        public Action<string, ITcpServerPLCMotionService> OnSuccess { get; set; } = null!;
        public Action<string> OnFailure { get; set; } = null!;
    }
}
