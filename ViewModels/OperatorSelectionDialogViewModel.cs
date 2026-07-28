using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

public partial class OperatorSelectionDialogViewModel : ObservableObject
{
    private readonly IOperatorStorageService _storageService;
    private readonly IOperatorStateService _operatorStateService;
    private readonly IScannerBarcodeService _scannerBarcodeService;
    private readonly ILogger<OperatorSelectionDialogViewModel> _logger;
    private bool _barcodeSubscribed;

    public OperatorSelectionDialogViewModel(
        IOperatorStorageService storageService,
        IOperatorStateService operatorStateService,
        IScannerBarcodeService scannerBarcodeService,
        ILogger<OperatorSelectionDialogViewModel> logger)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _operatorStateService = operatorStateService ?? throw new ArgumentNullException(nameof(operatorStateService));
        _scannerBarcodeService = scannerBarcodeService ?? throw new ArgumentNullException(nameof(scannerBarcodeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ObservableCollection<OperatorModel> Operators { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteOperatorCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmSelectionCommand))]
    private OperatorModel? _selectedOperator;

    [ObservableProperty]
    private string _inputName = string.Empty;

    [ObservableProperty]
    private string _hintText = "请选择作业员，或输入名称后新增";

    public event Action<bool?>? RequestClose;

    public async Task LoadAsync()
    {
        Operators.Clear();
        var list = await _storageService.LoadOperatorsAsync();
        foreach (var item in list.OrderBy(o => o.Name))
        {
            Operators.Add(item);
        }

        var current = _operatorStateService.CurrentOperator;
        SelectedOperator = current == null
            ? Operators.FirstOrDefault()
            : Operators.FirstOrDefault(o => o.Id == current.Id)
              ?? Operators.FirstOrDefault(o => string.Equals(o.Name, current.Name, StringComparison.OrdinalIgnoreCase))
              ?? Operators.FirstOrDefault();

        SubscribeToBarcode();
    }

    /// <summary>订阅原始扫码事件，防止重复加载造成重复订阅。</summary>
    private void SubscribeToBarcode()
    {
        if (_barcodeSubscribed)
            return;

        _scannerBarcodeService.BarcodeReceived += OnBarcodeReceived;
        _barcodeSubscribed = true;
    }

    /// <summary>弹窗关闭时退订原始扫码事件。</summary>
    public void UnsubscribeFromBarcode()
    {
        if (!_barcodeSubscribed)
            return;

        _scannerBarcodeService.BarcodeReceived -= OnBarcodeReceived;
        _barcodeSubscribed = false;
    }

    private void OnBarcodeReceived(object? sender, BarcodeReceivedEventArgs e)
    {
        void ApplyBarcode()
        {
            var raw = e.Barcode.Trim();
            if (!InputValidationHelper.IsValidOperatorName(raw))
            {
                HintText = "作业员扫码内容无效，请重新扫描或手动输入。";
                _logger.LogWarning("[作业员扫码][校验拒绝] 原始内容不符合名称规则");
                return;
            }

            var existing = Operators.FirstOrDefault(item =>
                string.Equals(item.Name.Trim(), raw, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                SelectedOperator = existing;
                InputName = existing.Name;
                HintText = $"扫码已选中作业员：{existing.Name}";
                return;
            }

            InputName = raw;
            SelectedOperator = null;
            HintText = $"未找到作业员 {raw}，请确认后点击新增。";
        }

        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.BeginInvoke((Action)ApplyBarcode);
        else
            ApplyBarcode();
    }

    [RelayCommand]
    private async Task AddOperatorAsync()
    {
        var name = InputName.Trim();
        if (!InputValidationHelper.IsValidOperatorName(name))
        {
            HintText = "作业员名称不能为空、不能超过 20 个字符且不能包含控制字符";
            _logger.LogWarning("[作业员][校验拒绝] 名称不符合输入规则");
            return;
        }

        var existing = Operators.FirstOrDefault(o =>
            string.Equals(o.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectedOperator = existing;
            HintText = $"作业员已存在，已选中：{existing.Name}";
            return;
        }

        var newOperator = new OperatorModel
        {
            Id = await _storageService.GetNextIdAsync(),
            Name = name,
            CreatedAt = DateTime.Now
        };

        Operators.Add(newOperator);
        await _storageService.SaveOperatorsAsync(Operators.ToList());
        SelectedOperator = newOperator;
        InputName = string.Empty;
        HintText = $"已新增并选中：{newOperator.Name}";
        _logger.LogWarning("[作业员][审计] 已新增作业员: {Operator}", newOperator.Name);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteOperator))]
    private async Task DeleteOperatorAsync()
    {
        if (SelectedOperator == null)
            return;

        var result = MessageBox.Show(
            $"确定要删除作业员 \"{SelectedOperator.Name}\" 吗？",
            "删除作业员",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        var removed = SelectedOperator;
        Operators.Remove(removed);
        if (_operatorStateService.CurrentOperator?.Id == removed.Id)
        {
            _operatorStateService.CurrentOperator = null;
        }

        await _storageService.SaveOperatorsAsync(Operators.ToList());
        SelectedOperator = Operators.FirstOrDefault();
        HintText = Operators.Count == 0 ? "暂无作业员，请先新增" : "作业员已删除，请重新选择";
        _logger.LogWarning("[作业员][审计] 已删除作业员: {Operator}", removed.Name);
    }

    private bool CanDeleteOperator() => SelectedOperator != null;

    [RelayCommand(CanExecute = nameof(CanConfirmSelection))]
    private void ConfirmSelection()
    {
        if (SelectedOperator == null)
            return;

        _operatorStateService.CurrentOperator = SelectedOperator;
        _logger.LogWarning("[作业员][审计] 当前作业员已选择: {Operator}", SelectedOperator.Name);
        RequestClose?.Invoke(true);
    }

    private bool CanConfirmSelection() => SelectedOperator != null;

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}
