using CommunityToolkit.Mvvm.ComponentModel;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Views
{
    /// <summary>
    /// 作业员设定页 - UserControl 导航版本
    /// </summary>
    [NavigationViewModel(typeof(OperatorSettingsViewModel))]
    public partial class OperatorSettingsView : UserControl
    {
        private readonly OperatorSettingsViewModel _viewModel;

        public OperatorSettingsView(OperatorSettingsViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;

            // 页面加载后聚焦到输入框
            Loaded += (s, e) =>
            {
                InputNameTextBox.Focus();
                InputNameTextBox.SelectAll();
            };

            // Enter 键触发新增
            InputNameTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    _viewModel.AddOrUpdateCommand.Execute(null);
                }
            };

            // 左侧列表双击 → 聚焦输入框
            OperatorListBox.MouseDoubleClick += (s, e) =>
            {
                if (_viewModel.SelectedOperator != null)
                {
                    InputNameTextBox.Focus();
                    InputNameTextBox.SelectAll();
                }
            };
        }
    }
}