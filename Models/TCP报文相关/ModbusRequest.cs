using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关
{
    /// <summary>
    /// Modbus TCP请求报文结构
    /// </summary>
    public class ModbusRequest
    {
        public ushort TransactionId { get; set; }
        public ushort ProtocolId { get; set; } = 0; // Modbus协议ID固定为0
        public ushort Length { get; set; }
        public byte UnitId { get; set; }
        public byte FunctionCode { get; set; }
        public ushort StartAddress { get; set; }
        public ushort Quantity { get; set; }
    }
}
