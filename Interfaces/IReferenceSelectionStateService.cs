using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

/// <summary>当前参照选择的读取、变更和持久化接口。</summary>
public interface IReferenceSelectionStateService
{
    ReferenceSelectionContext CurrentSelection { get; }
    bool HasValidSelection { get; }
    event EventHandler? SelectionChanged;

    Task LoadAsync(CancellationToken ct = default);
    Task SetSelectionAsync(ReferenceSelectionContext selection, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task RenameSeriesAsync(string oldName, string newName, CancellationToken ct = default);
    Task RenameMachineAsync(string seriesName, string oldName, string newName, CancellationToken ct = default);
}
