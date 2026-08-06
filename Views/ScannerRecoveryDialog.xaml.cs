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

    public void StartAcceptanceDrainMode()
    {
        _viewModel.EnableAcceptanceDrainMode();
    }

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
