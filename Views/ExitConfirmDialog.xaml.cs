
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    public partial class ExitConfirmDialog : Window
    {
        public ExitConfirmDialog()
        {
            InitializeComponent();
            DataContext = new ExitConfirmViewModel(this);
        }
    }
}
