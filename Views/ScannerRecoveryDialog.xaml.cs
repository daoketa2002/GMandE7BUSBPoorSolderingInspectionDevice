using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.ComponentModel;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views;

public partial class ScannerRecoveryDialog : Window
{
    private readonly ScannerRecoveryDialogViewModel _viewModel;

    public ScannerRecoveryDialog(ScannerRecoveryDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>向运行页提供用户在弹窗中确认使用的有效产品条码。</summary>
    public BarcodeParsedEventArgs? VerifiedProductBarcode
        => _viewModel.VerifiedProductBarcode;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void OnRequestClose(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.Dispose();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.CanClose)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
