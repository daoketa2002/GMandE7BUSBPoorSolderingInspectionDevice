using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关
{
    /// <summary>
    /// Modbus协议报文生成与解析工具
    /// </summary>
    public static class ModbusMessageHelper
    {
        /// <summary>
        /// 创建读取线圈状态请求报文 (功能码 0x01)
        /// </summary>
        public static byte[] CreateReadCoilsRequest(byte slaveId, ushort startAddress, ushort quantity)
        {
            if (quantity < 1 || quantity > 2000)
                throw new ArgumentException("Quantity must be between 1 and 2000", nameof(quantity));

            byte[] request = new byte[6];
            request[0] = slaveId;
            request[1] = 0x01; // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), quantity);
            return request;
        }

        /// <summary>
        /// 创建读取离散输入状态请求报文 (功能码 0x02)
        /// </summary>
        public static byte[] CreateReadDiscreteInputsRequest(byte slaveId, ushort startAddress, ushort quantity)
        {
            if (quantity < 1 || quantity > 2000)
                throw new ArgumentException("Quantity must be between 1 and 2000", nameof(quantity));

            byte[] request = new byte[6];
            request[0] = slaveId;
            request[1] = 0x02; // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), quantity);
            return request;
        }

        /// <summary>
        /// 创建读取保持寄存器请求报文 (功能码 0x03)
        /// </summary>
        public static byte[] CreateReadHoldingRegistersRequest(byte slaveId, ushort startAddress, ushort quantity)
        {
            if (quantity < 1 || quantity > 125)
                throw new ArgumentException("Quantity must be between 1 and 125", nameof(quantity));

            byte[] request = new byte[6];
            request[0] = slaveId;
            request[1] = 0x03; // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), quantity);
            return request;
        }

        /// <summary>
        /// 创建读取输入寄存器请求报文 (功能码 0x04)
        /// </summary>
        public static byte[] CreateReadInputRegistersRequest(byte slaveId, ushort startAddress, ushort quantity)
        {
            if (quantity < 1 || quantity > 125)
                throw new ArgumentException("Quantity must be between 1 and 125", nameof(quantity));

            byte[] request = new byte[6];
            request[0] = slaveId;
            request[1] = 0x04; // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), quantity);
            return request;
        }

        /// <summary>
        /// 创建写单个线圈请求报文 (功能码 0x05)
        /// </summary>
        public static byte[] CreateWriteSingleCoilRequest(byte slaveId, ushort address, bool value)
        {
            byte[] request = new byte[6];
            request[0] = slaveId;
            request[1] = 0x05; // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), address);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), value ? (ushort)0xFF00 : (ushort)0x0000);
            return request;
        }

        /// <summary>
        /// 创建写单个保持寄存器请求报文 (功能码 0x06)
        /// </summary>
        public static byte[] CreateWriteSingleRegisterRequest(byte slaveId, ushort address, ushort value)
        {
            byte[] request = new byte[6];
            request[0] = slaveId;
            request[1] = 0x06; // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), address);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), value);
            return request;
        }

        /// <summary>
        /// 创建写多个线圈请求报文 (功能码 0x0F)
        /// </summary>
        public static byte[] CreateWriteMultipleCoilsRequest(byte slaveId, ushort startAddress, bool[] values)
        {
            if (values.Length < 1 || values.Length > 1968)
                throw new ArgumentException("Values count must be between 1 and 1968", nameof(values));

            int byteCount = (values.Length + 7) / 8;
            byte[] request = new byte[7 + byteCount];
            request[0] = slaveId;
            request[1] = 0x0F; // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)values.Length);
            request[6] = (byte)byteCount;

            for (int i = 0; i < values.Length; i++)
            {
                int byteIndex = 7 + (i / 8);
                int bitIndex = i % 8;
                if (values[i])
                    request[byteIndex] |= (byte)(1 << bitIndex);
                else
                    request[byteIndex] &= (byte)~(1 << bitIndex);
            }

            return request;
        }

        /// <summary>
        /// 创建写多个保持寄存器请求报文 (功能码 0x10)
        /// </summary>
        public static byte[] CreateWriteMultipleRegistersRequest(byte slaveId, ushort startAddress, ushort[] values)
        {
            if (values.Length < 1 || values.Length > 123)
                throw new ArgumentException("Values count must be between 1 and 123", nameof(values));

            byte[] request = new byte[7 + values.Length * 2];
            request[0] = slaveId;
            request[1] = 0x10; // 功能码
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)values.Length);
            request[6] = (byte)(values.Length * 2);

            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(7 + i * 2), values[i]);
            }

            return request;
        }

        /// <summary>
        /// 解析读取线圈状态响应报文
        /// </summary>
        public static bool[]? ParseReadCoilsResponse(byte[] response)
        {
            if (response.Length < 3)
                return null;

            byte byteCount = response[2];
            if (response.Length < 3 + byteCount)
                return null;

            int coilCount = (byteCount * 8) <= 2008 ? byteCount * 8 : 2008; // Modbus最大返回2008个线圈
            bool[] coils = new bool[byteCount * 8];

            for (int i = 0; i < byteCount; i++)
            {
                byte dataByte = response[3 + i];
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
        /// 解析读取保持寄存器响应报文
        /// </summary>
        public static ushort[]? ParseReadHoldingRegistersResponse(byte[] response)
        {
            if (response.Length < 3)
                return null;

            byte byteCount = response[2];
            if (response.Length < 3 + byteCount || byteCount % 2 != 0)
                return null;

            int registerCount = byteCount / 2;
            ushort[] registers = new ushort[registerCount];

            for (int i = 0; i < registerCount; i++)
            {
                registers[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(3 + i * 2));
            }

            return registers;
        }

        /// <summary>
        /// 解析错误响应报文
        /// </summary>
        public static bool IsErrorResponse(byte[] response, out byte exceptionCode)
        {
            exceptionCode = 0;
            if (response.Length < 2 || (response[1] & 0x80) == 0)
                return false;

            exceptionCode = response[2];
            return true;
        }

        /// <summary>
        /// 验证Modbus CRC16校验
        /// </summary>
        public static bool VerifyCrc16(byte[] data)
        {
            if (data.Length < 2)
                return false;

            ushort receivedCrc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(data.Length - 2));
            ushort calculatedCrc = CalculateCrc16(data.AsSpan(0, data.Length - 2));
            return receivedCrc == calculatedCrc;
        }

        /// <summary>
        /// 计算Modbus CRC16校验
        /// </summary>
        public static ushort CalculateCrc16(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }

        /// <summary>
        /// 添加CRC16校验到Modbus报文
        /// </summary>
        public static byte[] AddCrc16(byte[] message)
        {
            ushort crc = CalculateCrc16(message);
            byte[] result = new byte[message.Length + 2];
            Array.Copy(message, 0, result, 0, message.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(message.Length), crc);
            return result;
        }
    }
}
