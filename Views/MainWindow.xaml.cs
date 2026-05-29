
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly INavigationService _navigationService;

        // 通过构造函数注入，不再从App.Current获取
        public MainWindow(INavigationService navigationService)
        {
            InitializeComponent();

            _navigationService = navigationService;

            // 注册主区域
            _navigationService.RegisterRegion(RegionNames.Main, MainRegion);

            // 窗口加载完成后自动导航到主菜单
            this.Loaded += async (s, e) =>
            {
                await _navigationService.NavigateToAsync<MainMenuView>();
            };

            // 窗口关闭时释放资源
            this.Closed += (s, e) =>
            {
                if (_navigationService is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            };
        }
    }
}
