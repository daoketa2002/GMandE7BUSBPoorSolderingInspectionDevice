using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services;

/// <summary>保存当前参照选择；参照数据变化不会触碰方案和历史记录。</summary>
public sealed class ReferenceSelectionStateService : IReferenceSelectionStateService
{
    private static readonly object FileLock = new();
    private readonly string _filePath;
    private readonly ILogger<ReferenceSelectionStateService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ReferenceSelectionStateService(
        IConfiguration configuration,
        ILogger<ReferenceSelectionStateService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var configuredPath = configuration.GetValue<string>(
            "ReferenceSelectionState:FilePath",
            Path.Combine("设置", "currentReferenceSelection.json"));
        _filePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath);
        CurrentSelection = new ReferenceSelectionContext();
    }

    public ReferenceSelectionContext CurrentSelection { get; private set; }
    public bool HasValidSelection
        => !string.IsNullOrWhiteSpace(CurrentSelection.SeriesName)
            && !string.IsNullOrWhiteSpace(CurrentSelection.ReferenceMachineType)
            && WorkstationConstants.IsValid(CurrentSelection.Workstation);

    public event EventHandler? SelectionChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var selection = await Task.Run(() =>
        {
            lock (FileLock)
            {
                if (!File.Exists(_filePath))
                    return new ReferenceSelectionContext();

                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<ReferenceSelectionContext>(json, _jsonOptions)
                    ?? new ReferenceSelectionContext();
            }
        }, ct).ConfigureAwait(false);

        CurrentSelection = Normalize(selection);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetSelectionAsync(ReferenceSelectionContext selection, CancellationToken ct = default)
    {
        var normalized = Normalize(selection);
        if (!WorkstationConstants.IsValid(normalized.Workstation))
            throw new ArgumentException("工位必须是左工位或右工位", nameof(selection));

        await SaveCoreAsync(normalized, ct).ConfigureAwait(false);
        CurrentSelection = normalized;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        var empty = new ReferenceSelectionContext();
        await SaveCoreAsync(empty, ct).ConfigureAwait(false);
        CurrentSelection = empty;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RenameSeriesAsync(string oldName, string newName, CancellationToken ct = default)
    {
        if (!string.Equals(CurrentSelection.SeriesName, oldName, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        return SetSelectionAsync(new ReferenceSelectionContext
        {
            SeriesName = newName,
            ReferenceMachineType = CurrentSelection.ReferenceMachineType,
            Workstation = CurrentSelection.Workstation
        }, ct);
    }

    public Task RenameMachineAsync(string seriesName, string oldName, string newName, CancellationToken ct = default)
    {
        if (!string.Equals(CurrentSelection.SeriesName, seriesName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(CurrentSelection.ReferenceMachineType, oldName, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        return SetSelectionAsync(new ReferenceSelectionContext
        {
            SeriesName = CurrentSelection.SeriesName,
            ReferenceMachineType = newName,
            Workstation = CurrentSelection.Workstation
        }, ct);
    }

    private async Task SaveCoreAsync(ReferenceSelectionContext selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Run(() =>
        {
            lock (FileLock)
            {
                try
                {
                    var directory = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);
                    var json = JsonSerializer.Serialize(selection, _jsonOptions);
                    File.WriteAllText(_filePath, json, new System.Text.UTF8Encoding(true));
                    _logger.LogInformation("当前参照选择已保存: {Series}/{Machine}/{Workstation}",
                        selection.SeriesName, selection.ReferenceMachineType, selection.Workstation);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[参照选择][审计] 保存失败: {FilePath}", _filePath);
                    throw;
                }
            }
        }, ct).ConfigureAwait(false);
    }

    private static ReferenceSelectionContext Normalize(ReferenceSelectionContext? selection)
    {
        selection ??= new ReferenceSelectionContext();
        return new ReferenceSelectionContext
        {
            SeriesName = selection.SeriesName?.Trim() ?? string.Empty,
            ReferenceMachineType = selection.ReferenceMachineType?.Trim() ?? string.Empty,
            Workstation = selection.Workstation?.Trim() ?? string.Empty
        };
    }
}
