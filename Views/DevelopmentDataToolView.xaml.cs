using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.Windows.Controls;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    [NavigationViewModel(typeof(DevelopmentDataToolViewModel))]
    public partial class DevelopmentDataToolView : UserControl
    {
        public DevelopmentDataToolView(DevelopmentDataToolViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
