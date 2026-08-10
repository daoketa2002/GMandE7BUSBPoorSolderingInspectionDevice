using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

/// <summary>从检测方案派生系列和机种，并在确认时完成 DT308 写入。</summary>
public partial class SeriesMachineSelectionDialogViewModel : ObservableObject
{
    private readonly IReferenceSelectionStateService _selectionStateService;
    private readonly IPlanStorageService _planStorageService;
    private readonly IPlcDevice _plcDevice;
    private readonly IScannerBarcodeService _scannerBarcodeService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<SeriesMachineSelectionDialogViewModel> _logger;
    private readonly List<PlanModel> _allPlans = new();
    private bool _barcodeSubscribed;

    /// <summary>方案扫码查询版本，丢弃晚于后续扫码返回的旧结果。</summary>
    private int _planLookupVersion;

    public SeriesMachineSelectionDialogViewModel(
        IReferenceSelectionStateService selectionStateService,
        IPlanStorageService planStorageService,
        IPlcDevice plcDevice,
        IScannerBarcodeService scannerBarcodeService,
        INotificationService notificationService,
        ILogger<SeriesMachineSelectionDialogViewModel> logger)
    {
        _selectionStateService = selectionStateService ?? throw new ArgumentNullException(nameof(selectionStateService));
        _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _scannerBarcodeService = scannerBarcodeService ?? throw new ArgumentNullException(nameof(scannerBarcodeService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>从方案中提取的系列候选，不承担目录维护职责。</summary>
    public ObservableCollection<string> SeriesItems { get; } = new();

    /// <summary>当前系列下从方案中提取并去重的机种候选。</summary>
    public ObservableCollection<string> MachineItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionPreviewText))]
    private string? _selectedSeries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionPreviewText))]
    private string? _selectedMachine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLeftWorkstation))]
    [NotifyPropertyChangedFor(nameof(IsRightWorkstation))]
    [NotifyPropertyChangedFor(nameof(SelectionPreviewText))]
    private string _workstation = WorkstationConstants.Left;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _hintText = string.Empty;

    public bool IsLeftWorkstation => Workstation == WorkstationConstants.Left;
    public bool IsRightWorkstation => Workstation == WorkstationConstants.Right;
    public string SelectionPreviewText
        => $"{SelectedSeries ?? "未选择系列"} / {SelectedMachine ?? "未选择机种"} / {Workstation}";

    public event Action<bool?>? RequestClose;

    /// <summary>系列变化后仅刷新机种候选，不自动选中第一个机种。</summary>
    partial void OnSelectedSeriesChanged(string? value)
    {
        MachineItems.Clear();
        SelectedMachine = null;

        if (!string.IsNullOrWhiteSpace(value))
        {
            foreach (var machineName in _allPlans
                .Where(plan => string.Equals(plan.Series.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(plan => plan.MachineType.Trim())
                .Where(machineName => !string.IsNullOrWhiteSpace(machineName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(machineName => machineName, StringComparer.OrdinalIgnoreCase))
            {
                MachineItems.Add(machineName);
            }
        }

        OnPropertyChanged(nameof(SelectionPreviewText));
    }

    partial void OnWorkstationChanged(string value)
    {
        OnPropertyChanged(nameof(IsLeftWorkstation));
        OnPropertyChanged(nameof(IsRightWorkstation));
        OnPropertyChanged(nameof(SelectionPreviewText));
    }

    /// <summary>
    /// 加载方案缓存并派生候选列表。当前参照状态只用于初始化工位，
    /// 系列和机种必须由本次弹窗重新选择。
    /// </summary>
    public async Task LoadAsync()
    {
        var allPlans = await _planStorageService.LoadAllPlansAsync();
        _allPlans.Clear();
        _allPlans.AddRange(allPlans.Where(plan =>
            !string.IsNullOrWhiteSpace(plan.Series)
            && !string.IsNullOrWhiteSpace(plan.MachineType)));

        await _selectionStateService.LoadAsync();

        SeriesItems.Clear();
        foreach (var seriesName in _allPlans
            .Select(plan => plan.Series.Trim())
            .Where(seriesName => !string.IsNullOrWhiteSpace(seriesName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(seriesName => seriesName, StringComparer.OrdinalIgnoreCase))
        {
            SeriesItems.Add(seriesName);
        }

        // 打开弹窗时不恢复系列和机种选中项，避免把上一次确认结果误当成本次确认。
        SelectedSeries = null;
        SelectedMachine = null;
        MachineItems.Clear();
        HintText = string.Empty;

        var current = _selectionStateService.CurrentSelection;
        if (WorkstationConstants.IsValid(current.Workstation))
            Workstation = current.Workstation;

        SubscribeToBarcode();
    }

    /// <summary>订阅共享产品条码解析事件，复用统一解析结果。</summary>
    private void SubscribeToBarcode()
    {
        if (_barcodeSubscribed)
            return;

        _scannerBarcodeService.BarcodeParsed += OnBarcodeParsed;
        _barcodeSubscribed = true;
    }

    /// <summary>弹窗关闭时退订共享扫码事件并使旧查询失效。</summary>
    public void UnsubscribeFromBarcode()
    {
        Interlocked.Increment(ref _planLookupVersion);
        if (!_barcodeSubscribed)
            return;

        _scannerBarcodeService.BarcodeParsed -= OnBarcodeParsed;
        _barcodeSubscribed = false;
    }

    private void OnBarcodeParsed(object? sender, BarcodeParsedEventArgs e)
    {
        var machineName = e.ModelName.Trim();
        if (string.IsNullOrWhiteSpace(machineName))
            return;

        void StartBarcodeLookup() => _ = ApplyBarcodePlanSelectionAsync(machineName);

        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke((Action)StartBarcodeLookup);
        else
            StartBarcodeLookup();
    }

    /// <summary>
    /// 根据缓存方案查询扫码机种，并按唯一 Series + MachineType + Workstation 组合自动选择。
    /// </summary>
    private async Task ApplyBarcodePlanSelectionAsync(string machineName)
    {
        int lookupVersion = Interlocked.Increment(ref _planLookupVersion);
        try
        {
            // 方案已在 LoadAsync 缓存，扫码时只做内存过滤，避免连续扫码时重复读取文件。
            await Task.CompletedTask;

            if (!_barcodeSubscribed || lookupVersion != Volatile.Read(ref _planLookupVersion))
                return;

            var matches = _allPlans
                .Where(plan => string.Equals(plan.MachineType.Trim(), machineName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                ClearSelection();
                HintText = "无该系列机种，请确认条码或方案设置。";
                _logger.LogWarning("[扫码联动][无匹配] 机种={MachineType}", machineName);
                return;
            }

            var validWorkstationMatches = matches
                .Where(plan => WorkstationConstants.IsValid(plan.Workstation))
                .ToList();
            if (validWorkstationMatches.Count == 0)
            {
                ClearSelection();
                var invalidPlan = matches[0];
                HintText = $"方案 {invalidPlan.PlanName} 的工位配置无效，请修正方案后重试。";
                _logger.LogWarning("[扫码联动][方案异常] 机种={MachineType}, 方案={PlanName}, 工位={Workstation}",
                    machineName, invalidPlan.PlanName, invalidPlan.Workstation);
                return;
            }

            var combinations = validWorkstationMatches
                .Select(plan => new
                {
                    Series = plan.Series.Trim(),
                    MachineType = plan.MachineType.Trim(),
                    Workstation = plan.Workstation.Trim()
                })
                .GroupBy(item => $"{item.Series}\u001F{item.MachineType}\u001F{item.Workstation}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (combinations.Count != 1)
            {
                ClearSelection();
                HintText = "该机种对应多个系列或工位，请手动选择。";
                _logger.LogWarning("[扫码联动][业务组合歧义] 机种={MachineType}, 组合数量={Count}",
                    machineName, combinations.Count);
                return;
            }

            var combination = combinations[0];
            // 先设置系列，等待系列变更刷新机种列表，再设置机种。
            SelectedSeries = SeriesItems.FirstOrDefault(series =>
                string.Equals(series, combination.Series, StringComparison.OrdinalIgnoreCase));
            SelectedMachine = MachineItems.FirstOrDefault(machine =>
                string.Equals(machine, combination.MachineType, StringComparison.OrdinalIgnoreCase));
            Workstation = combination.Workstation;
            HintText = $"扫码已匹配：{combination.Series} / {combination.MachineType} / {combination.Workstation}";
            _logger.LogInformation("[扫码联动][唯一业务组合] 已填充：{Series}/{MachineType}/{Workstation}",
                combination.Series, combination.MachineType, combination.Workstation);
        }
        catch (Exception ex)
        {
            if (!_barcodeSubscribed || lookupVersion != Volatile.Read(ref _planLookupVersion))
                return;

            _logger.LogError(ex, "[扫码联动][异常] 查询机种方案失败：{MachineType}", machineName);
            HintText = "读取机种检测方案失败，请检查方案设置。";
        }
    }

    private void ClearSelection()
    {
        SelectedSeries = null;
        SelectedMachine = null;
        MachineItems.Clear();
    }

    [RelayCommand]
    private async Task ConfirmSelectionAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSeries) || string.IsNullOrWhiteSpace(SelectedMachine))
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
                SeriesName = SelectedSeries,
                ReferenceMachineType = SelectedMachine,
                Workstation = Workstation
            });
            _logger.LogWarning("[参照选择][审计] 已确认: {Series}/{Machine}/{Workstation}",
                SelectedSeries, SelectedMachine, Workstation);
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
}
