using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

/// <summary>
/// PLC 操作统一结果模型。
/// 所有 IPlcDevice 业务方法返回此类型，调用方根据 IsSuccess 决定后续动作。
/// </summary>
public class PlcOperationResult
{
    /// <summary>操作是否成功</summary>
    public bool IsSuccess { get; init; }

    /// <summary>操作结果描述（成功/失败原因）</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>原始 Modbus 响应（仅诊断用途，业务代码不应依赖此字段做判定）</summary>
    public ModbusResponse? RawResponse { get; init; }

    /// <summary>创建成功结果</summary>
    public static PlcOperationResult Success(string message = "操作成功", ModbusResponse? rawResponse = null)
        => new() { IsSuccess = true, Message = message, RawResponse = rawResponse };

    /// <summary>创建失败结果</summary>
    public static PlcOperationResult Failure(string message, ModbusResponse? rawResponse = null)
        => new() { IsSuccess = false, Message = message, RawResponse = rawResponse };
}
