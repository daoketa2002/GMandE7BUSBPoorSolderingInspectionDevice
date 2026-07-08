namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>万用表测量值分类，决定后续判定和中止策略</summary>
public enum MeasurementValueKind
{
    /// <summary>正常非负有限数值</summary>
    Normal,
    /// <summary>+Infinity 或超量程大数（>ResistanceMaxValue），判定为 NG 但不中止</summary>
    PositiveInfinityOrOverRange,
    /// <summary>NaN，判定为 NG 且中止</summary>
    NaN,
    /// <summary>-Infinity，判定为 NG 且中止</summary>
    NegativeInfinity,
    /// <summary>负电阻值，判定为 NG 且中止</summary>
    NegativeResistance,
    /// <summary>无法解析，中止</summary>
    ParseFailed
}
