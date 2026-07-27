namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>工位业务值与 PLC 数值的唯一映射。</summary>
public static class WorkstationConstants
{
    public const string Left = "左工位";
    public const string Right = "右工位";

    public static bool IsValid(string? value)
        => value is Left or Right;

    public static ushort ToPlcValue(string workstation)
        => workstation switch
        {
            Left => 1,
            Right => 2,
            _ => throw new System.ArgumentException($"未知工位：{workstation}", nameof(workstation))
        };
}
