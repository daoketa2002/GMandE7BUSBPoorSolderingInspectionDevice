using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

/// <summary>
/// Modbus 请求的最小等待状态，仅保存响应完成源。
/// </summary>
sealed class PendingModbusRequest
{
    public required TaskCompletionSource<ModbusResponse> Completion { get; init; }
}
