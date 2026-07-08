using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>单个测试点配置（引脚、检测方式、阈值等）</summary>
public class TestPointConfig
{
    public string Name { get; set; } = string.Empty;
    public string PinLeft { get; set; } = string.Empty;
    public string PinRight { get; set; } = string.Empty;
    public ushort PinLeftCode { get; set; }
    public ushort PinRightCode { get; set; }
    public string PinLeftPolarity { get; set; } = PinPolarityConstants.Positive;
    public string PinRightPolarity { get; set; } = PinPolarityConstants.Negative;
    public ushort PinLeftPolarityCode { get; set; }
    public ushort PinRightPolarityCode { get; set; } = 1;
    public string CheckMode { get; set; } = CheckModeConstants.Continuity;
    public double? LowerLimit { get; set; }
    public double? UpperLimit { get; set; }
    public string? ModeValue { get; set; } = "OPEN";
    public double ContinuityThresholdOhm { get; set; } = 10.0;
    public string ActualContinuityState { get; set; } = string.Empty;
    public int? RelayChannel { get; set; }
    public double ActualValue { get; set; }
    public string Judgment { get; set; } = string.Empty;
    public bool IsTested { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    public static TestPointConfig FromPlanItem(PlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var (pinLeft, pinRight) = SplitItemName(item.ItemName);
        string leftPolarity = PinPolarityConstants.Normalize(item.PinLeftPolarity, PinPolarityConstants.Positive);
        string rightPolarity = PinPolarityConstants.Normalize(item.PinRightPolarity, PinPolarityConstants.Negative);

        return new TestPointConfig
        {
            Name = item.ItemName,
            PinLeft = pinLeft,
            PinRight = pinRight,
            PinLeftCode = PlcAddressMap.ConvertPinNameToNumber(pinLeft),
            PinRightCode = PlcAddressMap.ConvertPinNameToNumber(pinRight),
            PinLeftPolarity = leftPolarity,
            PinRightPolarity = rightPolarity,
            PinLeftPolarityCode = PlcAddressMap.ConvertPolarityToValue(leftPolarity),
            PinRightPolarityCode = PlcAddressMap.ConvertPolarityToValue(rightPolarity),
            CheckMode = item.CheckMode,
            LowerLimit = item.LowerLimit,
            UpperLimit = item.UpperLimit,
            ModeValue = item.ModeValue
        };
    }

    private static (string Left, string Right) SplitItemName(string itemName)
    {
        var parts = (itemName ?? string.Empty).Split('-', 2);
        return (
            parts.Length > 0 ? parts[0].Trim() : string.Empty,
            parts.Length > 1 ? parts[1].Trim() : string.Empty);
    }
}
