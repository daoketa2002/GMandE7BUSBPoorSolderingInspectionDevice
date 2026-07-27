namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>当前作业的参照系列、参照机种和工位。</summary>
public sealed class ReferenceSelectionContext
{
    public string SeriesName { get; set; } = string.Empty;
    public string ReferenceMachineType { get; set; } = string.Empty;
    public string Workstation { get; set; } = string.Empty;
}
