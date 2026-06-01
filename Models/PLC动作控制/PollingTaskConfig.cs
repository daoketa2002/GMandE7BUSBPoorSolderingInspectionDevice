using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制
{
    /// <summary>
    /// 轮询任务配置（支持寄存器参数 或 原始帧）
    /// </summary>
    public class PollingTaskConfig
    {
        // ====== 公共字段 ======
        public string Key { get; set; } = Guid.NewGuid().ToString(); // 唯一标识符（建议由调用方指定有意义的名字）
        public string DisplayName { get; set; } = "Unnamed Task";   // 命令名称（用于UI显示）
        public int IntervalMs { get; set; } = 1000;                 // 轮询间隔（毫秒）
        public bool IsEnabled { get; set; } = true;                 // 是否启用（可用于暂停/恢复）

        // ====== 模式1：基于寄存器参数 ======
        public byte? FunctionCode { get; set; }
        public byte UnitId { get; set; } = 1;
        public ushort StartAddress { get; set; }
        public ushort Quantity { get; set; }

        // ====== 模式2：基于原始Modbus帧（字节数组） ======
        public byte[]? RawRequestFrame { get; set; } // 完整的Modbus请求帧（含CRC等）

        // ====== 辅助方法 ======
        public bool UsesRegisterMode => FunctionCode.HasValue && RawRequestFrame == null;
        public bool UsesRawFrameMode => RawRequestFrame != null && !FunctionCode.HasValue;

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Key))
                throw new ArgumentException("Key cannot be null or empty.");

            if (IntervalMs <= 0)
                throw new ArgumentException("Interval must be greater than 0.");

            if (UsesRegisterMode)
            {
                if (FunctionCode < 1 || FunctionCode > 255)
                    throw new ArgumentException("Invalid FunctionCode.");
                if (Quantity == 0)
                    throw new ArgumentException("Quantity must be > 0.");
            }
            else if (UsesRawFrameMode)
            {
                if (RawRequestFrame == null || RawRequestFrame.Length == 0)
                    throw new ArgumentException("RawRequestFrame is required in raw mode.");
            }
            else
            {
                throw new InvalidOperationException("Must specify either register parameters or raw frame, not both or neither.");
            }
        }
    }
}
