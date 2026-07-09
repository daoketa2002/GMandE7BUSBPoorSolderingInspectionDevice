using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

/// <summary>
/// Modbus 请求诊断状态，用于超时根因排查。
/// DIAG-TEMP: 阶段 C 新增，确认根因后评估是否简化或删除。
/// </summary>
sealed class PendingModbusRequest
{
    public required TaskCompletionSource<ModbusResponse> Completion { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public int PendingAtCreate { get; set; }
    public bool SendCompleted { get; set; }
    public long SendElapsedMs { get; set; }
    public required int PendingOnEntry { get; init; }
}
