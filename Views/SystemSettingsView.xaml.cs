using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.Windows.Controls;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// 系统设定视图
    /// 使用 NavigationViewModelAttribute 自动绑定 ViewModel
    /// </summary>
    [NavigationViewModel(typeof(SystemSettingsViewModel))]
    public partial class SystemSettingsView : UserControl
    {
        public SystemSettingsView()
        {
            InitializeComponent();
            // DataContext 由 NavigationService 在导航时自动注入，无需手动设置
        }
    }
}