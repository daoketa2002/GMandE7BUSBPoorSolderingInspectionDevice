using System.Collections.Generic;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>系列—机种参照目录，仅供作业员选择参照信息。</summary>
public sealed class SeriesMachineCatalog
{
    public int Version { get; set; } = 1;
    public List<SeriesMachineGroup> Series { get; set; } = new();
}

/// <summary>一个系列及其参照机种集合。</summary>
public sealed class SeriesMachineGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<ReferenceMachineModel> Machines { get; set; } = new();
}

/// <summary>系列下的参照机种。</summary>
public sealed class ReferenceMachineModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
