using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
using System.Windows.Navigation;
using System.Windows.Shapes;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    [NavigationViewModel(typeof(MainMenuViewModel))]
    public partial class MainMenuView : UserControl
    {
        public MainMenuView(MainMenuViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // 绑定各按钮命令
            RunScreenBtn.Command = viewModel.NavigateToRunScreenCommand;
            PlanSettingBtn.Command = viewModel.NavigateToPlanSettingCommand;      
            DataExtractBtn.Command = viewModel.NavigateToDataExtractCommand;
            PlcTestBtn.Command = viewModel.NavigateToPlcCommunicationTestCommand;
            SystemSettingsBtn.Command = viewModel.NavigateToSystemSettingsCommand;
            ExitBtn.Command = viewModel.ExitApplicationCommand;

#if DEBUG
            var developmentToolButton = new Button
            {
                Content = "开发测试工具",
                Background = new SolidColorBrush(Color.FromRgb(142, 68, 173)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Command = viewModel.NavigateToDevelopmentDataToolCommand
            };
            MainButtonPanel.Children.Insert(MainButtonPanel.Children.Count - 1, developmentToolButton);
#endif
        }
    }
}
