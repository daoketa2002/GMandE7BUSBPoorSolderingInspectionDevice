using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views;

public partial class SeriesMachineSelectionDialog : Window
{
    private readonly SeriesMachineSelectionDialogViewModel _viewModel;

    public SeriesMachineSelectionDialog(SeriesMachineSelectionDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
        Closed += (_, _) => _viewModel.RequestClose -= OnRequestClose;
    }

    private void OnRequestClose(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }
}
