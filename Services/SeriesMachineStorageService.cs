using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services;

/// <summary>保存系列—机种参照目录，不调用方案存储服务。</summary>
public sealed class SeriesMachineStorageService : ISeriesMachineStorageService
{
    private static readonly object FileLock = new();
    private readonly string _filePath;
    private readonly ILogger<SeriesMachineStorageService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public SeriesMachineStorageService(
        IConfiguration configuration,
        ILogger<SeriesMachineStorageService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var configuredPath = configuration.GetValue<string>(
            "SeriesMachineStorage:FilePath",
            Path.Combine("设置", "seriesMachines.json"));
        _filePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath);
    }

    public async Task<SeriesMachineCatalog> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            lock (FileLock)
            {
                if (!File.Exists(_filePath))
                    return new SeriesMachineCatalog();

                var json = File.ReadAllText(_filePath);
                var catalog = JsonSerializer.Deserialize<SeriesMachineCatalog>(json, _jsonOptions)
                    ?? new SeriesMachineCatalog();
                catalog.Series ??= new List<SeriesMachineGroup>();
                foreach (var series in catalog.Series)
                    series.Machines ??= new List<ReferenceMachineModel>();
                return catalog;
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(SeriesMachineCatalog catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ct.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            lock (FileLock)
            {
                try
                {
                    catalog.Version = 1;
                    catalog.Series ??= new List<SeriesMachineGroup>();
                    foreach (var series in catalog.Series)
                        series.Machines ??= new List<ReferenceMachineModel>();

                    var directory = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);

                    var json = JsonSerializer.Serialize(catalog, _jsonOptions);
                    File.WriteAllText(_filePath, json, new System.Text.UTF8Encoding(true));
                    _logger.LogInformation("系列—机种参照目录已保存: {FilePath}", _filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[参照目录][审计] 保存失败: {FilePath}", _filePath);
                    throw;
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public int GetNextSeriesId(SeriesMachineCatalog catalog)
        => (catalog?.Series?.Select(item => item.Id).DefaultIfEmpty(0).Max() ?? 0) + 1;

    public int GetNextMachineId(SeriesMachineGroup series)
        => (series?.Machines?.Select(item => item.Id).DefaultIfEmpty(0).Max() ?? 0) + 1;
}
