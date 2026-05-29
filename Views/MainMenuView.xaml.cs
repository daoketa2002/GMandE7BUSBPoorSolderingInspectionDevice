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

            RunScreenBtn.Command = viewModel.NavigateToRunScreenCommand;
            DataExtractBtn.Command = viewModel.NavigateToDataExtractCommand;
            SystemSettingsBtn.Command = viewModel.NavigateToSystemSettingsCommand;
            ExitBtn.Command = viewModel.ExitApplicationCommand;
        }
    }
}

