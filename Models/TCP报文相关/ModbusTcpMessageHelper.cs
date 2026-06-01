using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关
{
    /// <summary>
    /// Modbus TCP协议报文生成与解析工具
    /// </summary>
    public static class ModbusTcpMessageHelper
    {
        /// <summary>
        /// 创建Modbus TCP读取线圈状态请求报文 (功能码 0x01)
        /// </summary>
        public static byte[] CreateReadCoilsRequest(ushort transactionId, byte unitId, ushort startAddress, ushort quantity)
        {
            if (quantity < 1 || quantity > 2000)
                throw new ArgumentException("Quantity must be between 1 and 2000", nameof(quantity));

            // PDU部分 (不包含MBAP头)
            byte[] pdu = new byte[6];
            pdu[0] = unitId;  // Unit ID
            pdu[1] = 0x01;    // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), quantity);

            // 创建完整TCP报文 (MBAP头 + PDU)
            var totalLength = 6 + pdu.Length; // MBAP头(6字节) + PDU
            var request = new byte[totalLength];

            // MBAP头
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Protocol ID
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)pdu.Length); // PDU长度（包括Unit ID）

            // PDU (包含Unit ID)
            Array.Copy(pdu, 0, request, 6, pdu.Length);

            return request;
        }

        /// <summary>
        /// 创建Modbus TCP读取离散输入状态请求报文 (功能码 0x02)
        /// </summary>
        public static byte[] CreateReadDiscreteInputsRequest(ushort transactionId, byte unitId, ushort startAddress, ushort quantity)
        {
            if (quantity < 1 || quantity > 2000)
                throw new ArgumentException("Quantity must be between 1 and 2000", nameof(quantity));

            // PDU部分 (不包含MBAP头)
            byte[] pdu = new byte[6];
            pdu[0] = unitId;  // Unit ID
            pdu[1] = 0x02;    // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), quantity);

            // 创建完整TCP报文 (MBAP头 + PDU)
            var totalLength = 6 + pdu.Length; // MBAP头(6字节) + PDU
            var request = new byte[totalLength];

            // MBAP头
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Protocol ID
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)pdu.Length); // PDU长度（包括Unit ID）

            // PDU (包含Unit ID)
            Array.Copy(pdu, 0, request, 6, pdu.Length);

            return request;
        }

        /// <summary>
        /// 创建Modbus TCP读取保持寄存器请求报文 (功能码 0x03)
        /// </summary>
        public static byte[] CreateReadHoldingRegistersRequest(ushort transactionId, byte unitId, ushort startAddress, ushort quantity)
        {
            if (quantity < 1 || quantity > 125)
                throw new ArgumentException("Quantity must be between 1 and 125", nameof(quantity));

            // PDU部分 (不包含MBAP头)
            byte[] pdu = new byte[6];
            pdu[0] = unitId;  // Unit ID
            pdu[1] = 0x03;    // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), quantity);

            // 创建完整TCP报文 (MBAP头 + PDU)
            var totalLength = 6 + pdu.Length; // MBAP头(6字节) + PDU
            var request = new byte[totalLength];

            // MBAP头
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Protocol ID
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)pdu.Length); // PDU长度（包括Unit ID）

            // PDU (包含Unit ID)
            Array.Copy(pdu, 0, request, 6, pdu.Length);

            return request;
        }

        /// <summary>
        /// 创建Modbus TCP读取输入寄存器请求报文 (功能码 0x04)
        /// </summary>
        public static byte[] CreateReadInputRegistersRequest(ushort transactionId, byte unitId, ushort startAddress, ushort quantity)
        {
            if (quantity < 1 || quantity > 125)
                throw new ArgumentException("Quantity must be between 1 and 125", nameof(quantity));

            // PDU部分 (不包含MBAP头)
            byte[] pdu = new byte[6];
            pdu[0] = unitId;  // Unit ID
            pdu[1] = 0x04;    // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), quantity);

            // 创建完整TCP报文 (MBAP头 + PDU)
            var totalLength = 6 + pdu.Length; // MBAP头(6字节) + PDU
            var request = new byte[totalLength];

            // MBAP头
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Protocol ID
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)pdu.Length); // PDU长度（包括Unit ID）

            // PDU (包含Unit ID)
            Array.Copy(pdu, 0, request, 6, pdu.Length);

            return request;
        }

        /// <summary>
        /// 创建Modbus TCP写单个线圈请求报文 (功能码 0x05)
        /// </summary>
        public static byte[] CreateWriteSingleCoilRequest(ushort transactionId, byte unitId, ushort address, bool value)
        {
            // PDU部分 (不包含MBAP头)
            byte[] pdu = new byte[6];
            pdu[0] = unitId;  // Unit ID
            pdu[1] = 0x05;    // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), address);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), value ? (ushort)0xFF00 : (ushort)0x0000);

            // 创建完整TCP报文 (MBAP头 + PDU)
            var totalLength = 6 + pdu.Length; // MBAP头(6字节) + PDU
            var request = new byte[totalLength];

            // MBAP头
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Protocol ID
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)pdu.Length); // PDU长度（包括Unit ID）

            // PDU (包含Unit ID)
            Array.Copy(pdu, 0, request, 6, pdu.Length);

            return request;
        }

        /// <summary>
        /// 创建Modbus TCP写单个保持寄存器请求报文 (功能码 0x06)
        /// </summary>
        public static byte[] CreateWriteSingleRegisterRequest(ushort transactionId, byte unitId, ushort address, ushort value)
        {
            // PDU部分 (不包含MBAP头)
            byte[] pdu = new byte[6];
            pdu[0] = unitId;  // Unit ID
            pdu[1] = 0x06;    // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), address);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), value);

            // 创建完整TCP报文 (MBAP头 + PDU)
            var totalLength = 6 + pdu.Length; // MBAP头(6字节) + PDU
            var request = new byte[totalLength];

            // MBAP头
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Protocol ID
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)pdu.Length); // PDU长度（包括Unit ID）

            // PDU (包含Unit ID)
            Array.Copy(pdu, 0, request, 6, pdu.Length);

            return request;
        }

        /// <summary>
        /// 创建Modbus TCP写多个线圈请求报文 (功能码 0x0F)
        /// </summary>
        public static byte[] CreateWriteMultipleCoilsRequest(ushort transactionId, byte unitId, ushort startAddress, bool[] values)
        {
            if (values.Length < 1 || values.Length > 1968)
                throw new ArgumentException("Values count must be between 1 and 1968", nameof(values));

            int byteCount = (values.Length + 7) / 8;
            // PDU部分 (不包含MBAP头)
            byte[] pdu = new byte[7 + byteCount];
            pdu[0] = unitId;  // Unit ID
            pdu[1] = 0x0F;    // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), (ushort)values.Length);
            pdu[6] = (byte)byteCount;

            for (int i = 0; i < values.Length; i++)
            {
                int byteIndex = 7 + (i / 8);
                int bitIndex = i % 8;
                if (values[i])
                    pdu[byteIndex] |= (byte)(1 << bitIndex);
                else
                    pdu[byteIndex] &= (byte)~(1 << bitIndex);
            }

            // 创建完整TCP报文 (MBAP头 + PDU)
            var totalLength = 6 + pdu.Length; // MBAP头(6字节) + PDU
            var request = new byte[totalLength];

            // MBAP头
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Protocol ID
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)pdu.Length); // PDU长度（包括Unit ID）

            // PDU (包含Unit ID)
            Array.Copy(pdu, 0, request, 6, pdu.Length);

            return request;
        }

        /// <summary>
        /// 创建Modbus TCP写多个保持寄存器请求报文 (功能码 0x10)
        /// </summary>
        public static byte[] CreateWriteMultipleRegistersRequest(ushort transactionId, byte unitId, ushort startAddress, ushort[] values)
        {
            if (values.Length < 1 || values.Length > 123)
                throw new ArgumentException("Values count must be between 1 and 123", nameof(values));

            // PDU部分 (不包含MBAP头)
            byte[] pdu = new byte[7 + values.Length * 2];
            pdu[0] = unitId;  // Unit ID
            pdu[1] = 0x10;    // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(4), (ushort)values.Length);
            pdu[6] = (byte)(values.Length * 2);

            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(7 + i * 2), values[i]);
            }

            // 创建完整TCP报文 (MBAP头 + PDU)
            var totalLength = 6 + pdu.Length; // MBAP头(6字节) + PDU
            var request = new byte[totalLength];

            // MBAP头
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Protocol ID
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)pdu.Length); // PDU长度（包括Unit ID）

            // PDU (包含Unit ID)
            Array.Copy(pdu, 0, request, 6, pdu.Length);

            return request;
        }

        /// <summary>
        /// 解析Modbus TCP读取线圈状态响应报文
        /// </summary>
        public static bool[]? ParseReadCoilsResponse(byte[] response)
        {
            // 检查响应是否为错误响应
            if (response.Length >= 9 && (response[7] & 0x80) != 0)
            {
                return null; // 错误响应
            }

            if (response.Length < 10) // MBAP头(6) + Unit ID(1) + Function Code(1) + Byte Count(1) + 至少1个数据字节
                return null;

            byte byteCount = response[8];
            if (response.Length < 9 + byteCount) // MBAP头(6) + PDU(3+byteCount)
                return null;

            int coilCount = (byteCount * 8) <= 2008 ? byteCount * 8 : 2008; // Modbus最大返回2008个线圈
            bool[] coils = new bool[byteCount * 8];

            for (int i = 0; i < byteCount; i++)
            {
                byte dataByte = response[9 + i];
                for (int j = 0; j < 8; j++)
                {
                    if ((i * 8 + j) < coils.Length)
                    {
                        coils[i * 8 + j] = (dataByte & (1 << j)) != 0;
                    }
                }
            }

            return coils;
        }

        /// <summary>
        /// 解析Modbus TCP读取保持寄存器响应报文
        /// </summary>
        public static ushort[]? ParseReadHoldingRegistersResponse(byte[] response)
        {
            // 检查响应是否为错误响应
            if (response.Length >= 9 && (response[7] & 0x80) != 0)
            {
                return null; // 错误响应
            }

            if (response.Length < 10) // MBAP头(6) + Unit ID(1) + Function Code(1) + Byte Count(1) + 至少1个数据字节
                return null;

            byte byteCount = response[8];
            if (response.Length < 9 + byteCount || byteCount % 2 != 0) // MBAP头(6) + PDU(3+byteCount)
                return null;

            int registerCount = byteCount / 2;
            ushort[] registers = new ushort[registerCount];

            for (int i = 0; i < registerCount; i++)
            {
                registers[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(9 + i * 2));
            }

            return registers;
        }

        /// <summary>
        /// 解析错误响应报文
        /// </summary>
        public static bool IsErrorResponse(byte[] response, out byte exceptionCode)
        {
            exceptionCode = 0;
            if (response.Length < 9) // MBAP头(6) + PDU至少3字节(Unit ID, Function Code, Exception Code)
                return false;

            byte functionCode = response[7];
            if ((functionCode & 0x80) == 0) // 检查功能码最高位是否为1（错误标识）
                return false;

            if (response.Length < 10) // MBAP头(6) + PDU至少4字节(Unit ID, Function Code, Exception Code)
                return false;

            exceptionCode = response[8];
            return true;
        }

        /// <summary>
        /// 从TCP响应中提取PDU部分
        /// </summary>
        public static byte[] ExtractPdu(byte[] response)
        {
            if (response.Length < 6) // MBAP头至少6字节
                throw new ArgumentException("Response too short to contain MBAP header", nameof(response));

            var pduLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4));
            var pdu = new byte[pduLength];
            Array.Copy(response, 6, pdu, 0, pduLength);
            return pdu;
        }
    }
}
