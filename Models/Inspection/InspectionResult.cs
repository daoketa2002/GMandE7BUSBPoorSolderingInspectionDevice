namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>检测结果</summary>
public class InspectionResult
{
    public string InspectionId { get; set; } = string.Empty;
    public string Barcode { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string OperatorName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int TotalCount { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public bool IsAllPassed { get; set; }
    public bool IsAborted { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    /// <summary>停止原因（正常完成、单项 NG 停止、PLC 停止/急停/复位等）</summary>
    public InspectionStopReason StopReason { get; set; } = InspectionStopReason.None;
    /// <summary>停止时的测试项索引（如有）</summary>
    public int? StopPointIndex { get; set; }
    /// <summary>停止时的测试项名称（如有）</summary>
    public string StopPointName { get; set; } = string.Empty;
    public TimeSpan Duration => EndTime - StartTime;
}
