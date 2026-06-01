using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关
{
    /// <summary>
    /// Modbus TCP响应报文结构
    /// </summary>
    public class ModbusResponse
    {
        public ushort TransactionId { get; set; }
        public ushort ProtocolId { get; set; }
        public ushort Length { get; set; }
        public byte UnitId { get; set; }
        public byte FunctionCode { get; set; }
        public byte ByteCount { get; set; }
        public byte[] Data { get; set; }
        public bool IsError { get; set; }
        public byte ErrorCode { get; set; }
    }
}
