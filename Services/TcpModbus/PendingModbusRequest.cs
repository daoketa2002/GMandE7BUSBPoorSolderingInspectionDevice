using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

/// <summary>
/// Modbus 请求的最小等待状态，仅保存响应完成源。
/// </summary>
sealed class PendingModbusRequest
{
    /// <summary>创建请求时所属的 TCP 连接代次，防止旧连接迟到响应串入新连接。</summary>
    public long ConnectionGeneration { get; init; }

    public required TaskCompletionSource<ModbusResponse> Completion { get; init; }
}
