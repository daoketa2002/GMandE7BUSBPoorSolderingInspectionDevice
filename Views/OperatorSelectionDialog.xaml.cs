using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views;

public partial class OperatorSelectionDialog : Window
{
    private readonly OperatorSelectionDialogViewModel _viewModel;

    public OperatorSelectionDialog(OperatorSelectionDialogViewModel viewModel)
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

    private void OperatorListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.ConfirmSelectionCommand.CanExecute(null))
        {
            _viewModel.ConfirmSelectionCommand.Execute(null);
        }
    }
}
