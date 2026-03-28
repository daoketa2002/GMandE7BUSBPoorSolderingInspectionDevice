using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;

namespace WPFStandardFramework.ViewModels
{
    public partial class ExitConfirmViewModel : ObservableObject
    {
        private readonly ILogger _logger;
        private readonly Window _dialog;

        public ExitConfirmViewModel(Window dialog)
        {
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _logger = Log.ForContext<ExitConfirmViewModel>();
        }

        [RelayCommand]
        private void ConfirmExit()
        {
            try
            {
                _logger.Information("用户确认退出");
                _dialog.DialogResult = true;
                _dialog.Close();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "确认退出时发生错误");
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            try
            {
                _logger.Debug("用户取消退出");
                _dialog.DialogResult = false;
                _dialog.Close();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "取消退出时发生错误");
            }
        }
    }
}
