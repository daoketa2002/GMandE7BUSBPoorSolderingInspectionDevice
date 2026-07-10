using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

/// <summary>
/// Modbus 请求诊断状态，用于超时根因排查和控制响应延迟分析（Phase E2）。
/// </summary>
sealed class PendingModbusRequest
{
    public required TaskCompletionSource<ModbusResponse> Completion { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public int PendingAtCreate { get; set; }
    public bool SendCompleted { get; set; }
    public long SendElapsedMs { get; set; }

    /// <summary>Phase E2: 等待 requestLock 门禁的耗时（毫秒），用于评估锁竞争导致的控制信号响应延迟。</summary>
    public long GateWaitMs { get; set; }
}
