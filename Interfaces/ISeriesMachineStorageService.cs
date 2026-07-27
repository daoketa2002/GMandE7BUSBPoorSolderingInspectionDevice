using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

/// <summary>系列—机种参照目录的持久化接口。</summary>
public interface ISeriesMachineStorageService
{
    Task<SeriesMachineCatalog> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(SeriesMachineCatalog catalog, CancellationToken ct = default);
    int GetNextSeriesId(SeriesMachineCatalog catalog);
    int GetNextMachineId(SeriesMachineGroup series);
}
