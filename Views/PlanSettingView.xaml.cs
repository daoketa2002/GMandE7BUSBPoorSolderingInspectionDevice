using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.Windows.Controls;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// 方案设定主页
    /// </summary>
    [NavigationViewModel(typeof(PlanSettingViewModel))]
    public partial class PlanSettingView : UserControl
    {
        public PlanSettingView(PlanSettingViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
        }
    }
}