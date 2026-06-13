using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    public partial class LogDataViewModel : ObservableObject, INavigationAware
    {
        private readonly ILogDataService _logDataService;
        private readonly ICsvExportService _csvExportService;
        private readonly INavigationService _navigationService;
        private readonly ILogger<LogDataViewModel> _logger;

        private List<LogDataModel> _allData = new();

        public LogDataViewModel(
            ILogDataService logDataService,
            ICsvExportService csvExportService,
            INavigationService navigationService,
            ILogger<LogDataViewModel> logger)
        {
            _logDataService = logDataService ?? throw new ArgumentNullException(nameof(logDataService));
            _csvExportService = csvExportService ?? throw new ArgumentNullException(nameof(csvExportService));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region 属性

        [ObservableProperty]
        private ObservableCollection<string> _machineTypes = new();

        [ObservableProperty]
        private string _selectedMachineType = string.Empty;

        [ObservableProperty]
        private string _searchSerialNumber = string.Empty;

        [ObservableProperty]
        private string _searchPlanName = string.Empty;

        [ObservableProperty]
        private ObservableCollection<LogDataModel> _logDataList = new();

        [ObservableProperty]
        private ObservableCollection<string> _dynamicHeaders = new();

        [ObservableProperty]
        private string _totalCountText = "共 0 条记录";

        [ObservableProperty]
        private bool _isLoading;

        #endregion

        #region 命令

        [RelayCommand]
        private void Search()
        {
            try
            {
                _logger.LogInformation("执行检索 - 机种:{MachineType}, 序列号:{Serial}, 方案:{Plan}",
                    SelectedMachineType, SearchSerialNumber, SearchPlanName);

                var filtered = _logDataService.Search(
                    _allData,
                    string.IsNullOrWhiteSpace(SelectedMachineType) ? null : SelectedMachineType.Trim(),
                    string.IsNullOrWhiteSpace(SearchSerialNumber) ? null : SearchSerialNumber.Trim(),
                    string.IsNullOrWhiteSpace(SearchPlanName) ? null : SearchPlanName.Trim());

                LogDataList = new ObservableCollection<LogDataModel>(filtered);
                UpdateDynamicHeaders(filtered);
                UpdateTotalCount();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检索失败");
                MessageBox.Show($"检索失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void Reset()
        {
            SelectedMachineType = string.Empty;
            SearchSerialNumber = string.Empty;
            SearchPlanName = string.Empty;

            LogDataList = new ObservableCollection<LogDataModel>(_allData);
            UpdateDynamicHeaders(_allData);
            UpdateTotalCount();
        }

        [RelayCommand]
        private async Task ExportToCsvAsync()
        {
            try
            {
                _logger.LogInformation("执行导出，当前数据 {Count} 条", LogDataList.Count);

                var dynamicHeaders = DynamicHeaders.ToList();

                var headers = new List<(string Header, Func<LogDataModel, string> ValueSelector)>
                {
                    ("序号",       m => m.Index.ToString()),
                    ("机种名称",   m => m.MachineType),
                    ("序列号",     m => m.SerialNumber),
                    ("方案名称",   m => m.PlanName),
                    ("检查者",     m => m.Inspector),
                    ("综合判定",   m => m.Judgment),
                };

                foreach (var dh in dynamicHeaders)
                {
                    var capturedHeader = dh;
                    headers.Add((capturedHeader, m =>
                        m.DynamicItems.TryGetValue(capturedHeader, out var val) ? val : string.Empty));
                }

                headers.Add(("日期", m => m.Date));
                headers.Add(("时间", m => m.Time));

                var defaultFileName = $"日志数据导出_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                await _csvExportService.ExportWithDialogAsync(LogDataList, headers, defaultFileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出失败");
                MessageBox.Show($"导出失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private async Task GoBackAsync()
        {
            await _navigationService.NavigateToAsync<MainMenuView>();
        }

        #endregion

        #region INavigationAware

        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogInformation("进入日志数据页面");
            IsLoading = true;

            try
            {
                var types = await _logDataService.GetMachineTypesAsync();
                MachineTypes = new ObservableCollection<string>(types);

                _allData = await _logDataService.LoadAllLogDataAsync();
                LogDataList = new ObservableCollection<LogDataModel>(_allData);

                UpdateDynamicHeaders(_allData);
                UpdateTotalCount();

                _logger.LogInformation("日志数据加载完成，共 {Count} 条", _allData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载日志数据失败");
                MessageBox.Show($"加载日志数据失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public Task OnNavigatedFromAsync()
        {
            _logger.LogInformation("离开日志数据页面");
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion

        #region 私有方法

        private void UpdateDynamicHeaders(List<LogDataModel> data)
        {
            var headers = _logDataService.GetAllDynamicHeaders(data);
            DynamicHeaders = new ObservableCollection<string>(headers);
        }

        private void UpdateTotalCount()
        {
            TotalCountText = $"共 {LogDataList.Count} 条记录";
        }

        #endregion
    }
}