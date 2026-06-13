using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.Windows.Controls;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// 方案编辑页面
    /// </summary>
    [NavigationViewModel(typeof(PlanEditViewModel))]
    public partial class PlanEditView : UserControl
    {
        public PlanEditView(PlanEditViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        }
    }
}