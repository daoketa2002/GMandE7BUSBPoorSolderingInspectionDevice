using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

/// <summary>维护系列—机种参照目录，并在确认时完成 DT308 写入。</summary>
public partial class SeriesMachineSelectionDialogViewModel : ObservableObject
{
    private readonly ISeriesMachineStorageService _storageService;
    private readonly IReferenceSelectionStateService _selectionStateService;
    private readonly IPlcDevice _plcDevice;
    private readonly INotificationService _notificationService;
    private readonly ILogger<SeriesMachineSelectionDialogViewModel> _logger;
    private SeriesMachineCatalog _catalog = new();

    public SeriesMachineSelectionDialogViewModel(
        ISeriesMachineStorageService storageService,
        IReferenceSelectionStateService selectionStateService,
        IPlcDevice plcDevice,
        INotificationService notificationService,
        ILogger<SeriesMachineSelectionDialogViewModel> logger)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _selectionStateService = selectionStateService ?? throw new ArgumentNullException(nameof(selectionStateService));
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ObservableCollection<SeriesMachineGroup> SeriesItems { get; } = new();
    public ObservableCollection<ReferenceMachineModel> MachineItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionPreviewText))]
    private SeriesMachineGroup? _selectedSeries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionPreviewText))]
    private ReferenceMachineModel? _selectedMachine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionPreviewText))]
    private string _seriesNameInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionPreviewText))]
    private string _machineNameInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionPreviewText))]
    private string _workstation = WorkstationConstants.Left;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsLeftWorkstation => Workstation == WorkstationConstants.Left;
    public bool IsRightWorkstation => Workstation == WorkstationConstants.Right;
    public string SelectionPreviewText
        => $"{SelectedSeries?.Name ?? SeriesNameInput.Trim()} / "
            + $"{SelectedMachine?.Name ?? MachineNameInput.Trim()} / {Workstation}";

    public event Action<bool?>? RequestClose;

    partial void OnSelectedSeriesChanged(SeriesMachineGroup? value)
    {
        MachineItems.Clear();
        if (value != null)
        {
            SeriesNameInput = value.Name;
            foreach (var machine in value.Machines.OrderBy(item => item.Name))
                MachineItems.Add(machine);
            SelectedMachine = MachineItems.FirstOrDefault();
        }
        else
        {
            SelectedMachine = null;
        }

        OnPropertyChanged(nameof(SelectionPreviewText));
    }

    partial void OnSelectedMachineChanged(ReferenceMachineModel? value)
    {
        MachineNameInput = value?.Name ?? string.Empty;
        OnPropertyChanged(nameof(SelectionPreviewText));
    }

    partial void OnWorkstationChanged(string value)
    {
        OnPropertyChanged(nameof(IsLeftWorkstation));
        OnPropertyChanged(nameof(IsRightWorkstation));
        OnPropertyChanged(nameof(SelectionPreviewText));
    }

    public async Task LoadAsync()
    {
        _catalog = await _storageService.LoadAsync();
        await _selectionStateService.LoadAsync();
        ReloadSeriesItems();

        var current = _selectionStateService.CurrentSelection;
        SelectedSeries = SeriesItems.FirstOrDefault(item =>
            string.Equals(item.Name, current.SeriesName, StringComparison.OrdinalIgnoreCase))
            ?? SeriesItems.FirstOrDefault();
        SelectedMachine = SelectedSeries?.Machines.FirstOrDefault(item =>
            string.Equals(item.Name, current.ReferenceMachineType, StringComparison.OrdinalIgnoreCase))
            ?? SelectedMachine;
        if (WorkstationConstants.IsValid(current.Workstation))
            Workstation = current.Workstation;
    }

    [RelayCommand]
    private async Task AddSeriesAsync()
    {
        var name = SeriesNameInput.Trim();
        var error = NameValidationHelper.ValidateSeriesName(name);
        if (error != null)
        {
            await _notificationService.ShowWarningAsync(error, "系列校验失败");
            return;
        }

        if (_catalog.Series.Any(item => string.Equals(item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            await _notificationService.ShowWarningAsync("系列名称已存在。", "系列校验失败");
            return;
        }

        var series = new SeriesMachineGroup { Id = _storageService.GetNextSeriesId(_catalog), Name = name };
        _catalog.Series.Add(series);
        await _storageService.SaveAsync(_catalog);
        ReloadSeriesItems();
        SelectedSeries = series;
        _logger.LogWarning("[参照目录][审计] 新增系列: {Series}", name);
    }

    [RelayCommand]
    private async Task RenameSeriesAsync()
    {
        if (SelectedSeries == null) return;
        var oldName = SelectedSeries.Name;
        var newName = SeriesNameInput.Trim();
        var error = NameValidationHelper.ValidateSeriesName(newName);
        if (error != null)
        {
            await _notificationService.ShowWarningAsync(error, "系列校验失败");
            return;
        }
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)
            && _catalog.Series.Any(item => string.Equals(item.Name.Trim(), newName, StringComparison.OrdinalIgnoreCase)))
        {
            await _notificationService.ShowWarningAsync("系列名称已存在。", "系列校验失败");
            return;
        }

        SelectedSeries.Name = newName;
        await _storageService.SaveAsync(_catalog);
        await _selectionStateService.RenameSeriesAsync(oldName, newName);
        ReloadSeriesItems();
        SelectedSeries = SeriesItems.First(item => ReferenceEquals(item, SelectedSeries) ||
            string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase));
        _logger.LogWarning("[参照目录][审计] 系列改名: {OldName} -> {NewName}", oldName, newName);
    }

    [RelayCommand]
    private async Task DeleteSeriesAsync()
    {
        if (SelectedSeries == null) return;
        var series = SelectedSeries;
        var message = series.Machines.Count > 0
            ? $"系列“{series.Name}”下仍有 {series.Machines.Count} 个机种。\n\n删除只会删除参照目录，不会删除方案和历史数据，是否继续？"
            : $"确定删除系列“{series.Name}”吗？\n\n不会删除方案和历史数据。";
        if (!await _notificationService.ConfirmAsync(message, "确认删除系列")) return;

        _catalog.Series.Remove(series);
        await _storageService.SaveAsync(_catalog);
        if (string.Equals(_selectionStateService.CurrentSelection.SeriesName, series.Name, StringComparison.OrdinalIgnoreCase))
            await _selectionStateService.ClearAsync();
        ReloadSeriesItems();
        SelectedSeries = SeriesItems.FirstOrDefault();
        _logger.LogWarning("[参照目录][审计] 删除系列: {Series}", series.Name);
    }

    [RelayCommand]
    private async Task AddMachineAsync()
    {
        if (SelectedSeries == null)
        {
            await _notificationService.ShowWarningAsync("请先选择系列。", "机种校验失败");
            return;
        }
        var name = MachineNameInput.Trim();
        var error = NameValidationHelper.ValidateMachineType(name);
        if (error != null)
        {
            await _notificationService.ShowWarningAsync(error, "机种校验失败");
            return;
        }
        if (SelectedSeries.Machines.Any(item => string.Equals(item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            await _notificationService.ShowWarningAsync("当前系列内的机种名称已存在。", "机种校验失败");
            return;
        }

        var machine = new ReferenceMachineModel
        {
            Id = _storageService.GetNextMachineId(SelectedSeries),
            Name = name
        };
        SelectedSeries.Machines.Add(machine);
        await _storageService.SaveAsync(_catalog);
        ReloadSeriesItems();
        SelectedSeries = SeriesItems.First(item => item.Id == SelectedSeries.Id);
        SelectedMachine = MachineItems.FirstOrDefault(item => item.Id == machine.Id);
        _logger.LogWarning("[参照目录][审计] 新增机种: {Series}/{Machine}", SelectedSeries.Name, name);
    }

    [RelayCommand]
    private async Task RenameMachineAsync()
    {
        if (SelectedSeries == null || SelectedMachine == null) return;
        var oldName = SelectedMachine.Name;
        var newName = MachineNameInput.Trim();
        var error = NameValidationHelper.ValidateMachineType(newName);
        if (error != null)
        {
            await _notificationService.ShowWarningAsync(error, "机种校验失败");
            return;
        }
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)
            && SelectedSeries.Machines.Any(item => string.Equals(item.Name.Trim(), newName, StringComparison.OrdinalIgnoreCase)))
        {
            await _notificationService.ShowWarningAsync("当前系列内的机种名称已存在。", "机种校验失败");
            return;
        }

        var seriesName = SelectedSeries.Name;
        SelectedMachine.Name = newName;
        await _storageService.SaveAsync(_catalog);
        await _selectionStateService.RenameMachineAsync(seriesName, oldName, newName);
        ReloadSeriesItems();
        SelectedSeries = SeriesItems.First(item => item.Id == SelectedSeries.Id);
        SelectedMachine = MachineItems.FirstOrDefault(item => item.Id == SelectedMachine.Id);
        _logger.LogWarning("[参照目录][审计] 机种改名: {Series}/{OldName} -> {NewName}", seriesName, oldName, newName);
    }

    [RelayCommand]
    private async Task DeleteMachineAsync()
    {
        if (SelectedSeries == null || SelectedMachine == null) return;
        var seriesName = SelectedSeries.Name;
        var machine = SelectedMachine;
        if (!await _notificationService.ConfirmAsync(
            $"确定删除参照机种“{machine.Name}”吗？\n\n不会删除方案和历史数据。", "确认删除机种")) return;

        SelectedSeries.Machines.Remove(machine);
        await _storageService.SaveAsync(_catalog);
        if (string.Equals(_selectionStateService.CurrentSelection.SeriesName, seriesName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_selectionStateService.CurrentSelection.ReferenceMachineType, machine.Name, StringComparison.OrdinalIgnoreCase))
            await _selectionStateService.ClearAsync();
        ReloadSeriesItems();
        SelectedSeries = SeriesItems.FirstOrDefault(item => string.Equals(item.Name, seriesName, StringComparison.OrdinalIgnoreCase));
        _logger.LogWarning("[参照目录][审计] 删除机种: {Series}/{Machine}", seriesName, machine.Name);
    }

    [RelayCommand]
    private async Task ConfirmSelectionAsync()
    {
        if (SelectedSeries == null || SelectedMachine == null)
        {
            await _notificationService.ShowWarningAsync("请选择系列和参照机种。", "选择不完整");
            return;
        }
        if (!WorkstationConstants.IsValid(Workstation))
        {
            await _notificationService.ShowWarningAsync("请选择左工位或右工位。", "选择不完整");
            return;
        }
        if (!_plcDevice.IsConnected)
        {
            await _notificationService.ShowWarningAsync("PLC 未连接，无法写入工位。", "工位写入失败");
            return;
        }

        IsBusy = true;
        try
        {
            var plcResult = await _plcDevice.WriteWorkstationAsync(Workstation);
            if (!plcResult.IsSuccess)
            {
                _logger.LogWarning("[参照选择][审计] DT308 写入失败: {Message}", plcResult.Message);
                await _notificationService.ShowErrorAsync("工位写入失败，请检查 PLC 连接。", "工位写入失败");
                return;
            }

            await _selectionStateService.SetSelectionAsync(new ReferenceSelectionContext
            {
                SeriesName = SelectedSeries.Name,
                ReferenceMachineType = SelectedMachine.Name,
                Workstation = Workstation
            });
            _logger.LogWarning("[参照选择][审计] 已确认: {Series}/{Machine}/{Workstation}",
                SelectedSeries.Name, SelectedMachine.Name, Workstation);
            RequestClose?.Invoke(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    [RelayCommand]
    private void SelectLeftWorkstation() => Workstation = WorkstationConstants.Left;

    [RelayCommand]
    private void SelectRightWorkstation() => Workstation = WorkstationConstants.Right;

    private void ReloadSeriesItems()
    {
        var selectedId = SelectedSeries?.Id;
        SeriesItems.Clear();
        foreach (var series in _catalog.Series.OrderBy(item => item.Name))
            SeriesItems.Add(series);
        if (selectedId.HasValue)
            SelectedSeries = SeriesItems.FirstOrDefault(item => item.Id == selectedId.Value);
        OnPropertyChanged(nameof(SelectionPreviewText));
    }
}
