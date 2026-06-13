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
    /// <summary>
    /// 日志数据页面 ViewModel — 管理CSV日志数据的加载、检索和导出。
    /// </summary>
    /// <remarks>
    /// <para><b>数据流：</b></para>
    /// <para>1. 页面导航进入时（OnNavigatedToAsync）→ 仅加载机种下拉列表，不展示任何数据。</para>
    /// <para>2. 用户点击「检索」→ 首次检索时加载所有CSV文件到 _allData 缓存，后续检索直接用缓存过滤。</para>
    /// <para>3. 用户点击「重置」→ 清空检索条件，清空表格数据。</para>
    /// <para>4. 用户点击「导出」→ 将当前 LogDataList 导出为CSV文件。</para>
    /// <para><b>动态列说明：</b>不同CSV文件可能有不同的检测项列（如 B4-B5、A1-A2），
    /// 动态列保持文件中的原始顺序，不同文件的列不合并。</para>
    /// </remarks>
    public partial class LogDataViewModel : ObservableObject, INavigationAware
    {
        private readonly ILogDataService _logDataService;
        private readonly ICsvExportService _csvExportService;
        private readonly INavigationService _navigationService;
        private readonly ILogger<LogDataViewModel> _logger;

        /// <summary>全量数据缓存 — 首次检索时从CSV加载，后续检索直接复用</summary>
        private List<LogDataModel> _allData = new();

        /// <summary>标记全量数据是否已加载（避免重复读取文件）</summary>
        private bool _dataLoaded = false;

        /// <summary>
        /// 构造器 — 通过DI注入所需服务。
        /// </summary>
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

        /// <summary>机种名称下拉框的数据源（如 E78、GM5）</summary>
        [ObservableProperty]
        private ObservableCollection<string> _machineTypes = new();

        /// <summary>用户在下拉框中选择或手动输入的机种名称</summary>
        [ObservableProperty]
        private string _selectedMachineType = string.Empty;

        /// <summary>用户输入的序列号检索条件</summary>
        [ObservableProperty]
        private string _searchSerialNumber = string.Empty;

        /// <summary>用户输入的方案名称检索条件</summary>
        [ObservableProperty]
        private string _searchPlanName = string.Empty;

        /// <summary>当前显示在 DataGrid 中的日志数据列表</summary>
        [ObservableProperty]
        private ObservableCollection<LogDataModel> _logDataList = new();

        /// <summary>当前数据中出现的所有动态列名（用于XAML动态生成列）</summary>
        [ObservableProperty]
        private ObservableCollection<string> _dynamicHeaders = new();

        /// <summary>底部状态栏文字（如 "共 15 条记录"）</summary>
        [ObservableProperty]
        private string _totalCountText = "共 0 条记录";

        /// <summary>是否正在加载数据（控制加载提示的显示）</summary>
        [ObservableProperty]
        private bool _isLoading;

        #endregion

        #region 命令

        /// <summary>
        /// 执行组合检索 — 首次检索时从CSV文件加载数据，后续直接过滤缓存。
        /// 三个条件（机种名称、序列号、方案名称）为 AND 关系，空白条件视为忽略。
        /// </summary>
        [RelayCommand]
        private async Task SearchAsync()
        {
            try
            {
                _logger.LogInformation("执行检索 - 机种:{MachineType}, 序列号:{Serial}, 方案:{Plan}",
                    SelectedMachineType, SearchSerialNumber, SearchPlanName);

                // 首次检索时加载数据，后续直接复用缓存
                if (!_dataLoaded)
                {
                    IsLoading = true;
                    try
                    {
                        _allData = await _logDataService.LoadAllLogDataAsync();
                        _dataLoaded = true;
                        _logger.LogInformation("首次检索 - 已加载 {Count} 条日志数据", _allData.Count);
                    }
                    finally
                    {
                        IsLoading = false;
                    }
                }

                var filtered = _logDataService.Search(
                    _allData,
                    string.IsNullOrWhiteSpace(SelectedMachineType) ? null : SelectedMachineType.Trim(),
                    string.IsNullOrWhiteSpace(SearchSerialNumber) ? null : SearchSerialNumber.Trim(),
                    string.IsNullOrWhiteSpace(SearchPlanName) ? null : SearchPlanName.Trim());

                LogDataList = new ObservableCollection<LogDataModel>(filtered);
                UpdateDynamicHeaders(filtered);
                UpdateTotalCount();

                _logger.LogInformation("检索完成，匹配 {Count} 条记录", filtered.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检索失败");
                MessageBox.Show($"检索失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 重置检索条件并清空表格数据。
        /// </summary>
        [RelayCommand]
        private void Reset()
        {
            SelectedMachineType = string.Empty;
            SearchSerialNumber = string.Empty;
            SearchPlanName = string.Empty;

            LogDataList = new ObservableCollection<LogDataModel>();
            DynamicHeaders = new ObservableCollection<string>();
            UpdateTotalCount();
        }

        /// <summary>
        /// 将当前表格数据导出为CSV文件，弹出保存对话框。
        /// 列顺：固定列 → 动态列 → 日期 → 时间。
        /// </summary>
        [RelayCommand]
        private async Task ExportToCsvAsync()
        {
            try
            {
                _logger.LogInformation("执行导出，当前数据 {Count} 条", LogDataList.Count);

                var dynamicHeaders = DynamicHeaders.ToList();

                // 构建列头定义：固定列 → 动态列 → 日期时间
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
                    var capturedHeader = dh; // 闭包捕获
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

        /// <summary>
        /// 返回主菜单页面。
        /// </summary>
        [RelayCommand]
        private async Task GoBackAsync()
        {
            await _navigationService.NavigateToAsync<MainMenuView>();
        }

        #endregion

        #region INavigationAware

        /// <summary>
        /// 页面导航进入时触发 — 仅加载机种下拉列表，不展示数据。
        /// 数据在用户首次点击「检索」时才加载。
        /// </summary>
        /// <param name="parameter">导航参数（当前未使用）</param>
        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogInformation("进入日志数据页面");
            IsLoading = true;

            try
            {
                // 仅加载机种名称列表（供下拉框使用）
                var types = await _logDataService.GetMachineTypesAsync();
                MachineTypes = new ObservableCollection<string>(types);

                // 不加载数据 — 表格保持空白，等待用户点击「检索」
                _allData = new List<LogDataModel>();
                _dataLoaded = false;
                LogDataList = new ObservableCollection<LogDataModel>();
                DynamicHeaders = new ObservableCollection<string>();
                UpdateTotalCount();

                _logger.LogInformation("日志数据页面就绪，机种数量: {Count}，等待用户检索", types.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化日志数据页面失败");
                MessageBox.Show($"初始化日志数据页面失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 页面导航离开时触发。
        /// </summary>
        public Task OnNavigatedFromAsync()
        {
            _logger.LogInformation("离开日志数据页面");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 检查是否允许从当前页面导航离开（始终允许）。
        /// </summary>
        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion

        #region 私有方法


        /// <summary>
        /// 根据当前数据更新动态列名集合（触发XAML列生成）。
        /// </summary>
        /// <param name="data">要提取动态列名的数据</param>
        private void UpdateDynamicHeaders(List<LogDataModel> data)
        {
            var headers = _logDataService.GetAllDynamicHeaders(data);
            DynamicHeaders = new ObservableCollection<string>(headers);
        }

        /// <summary>
        /// 更新底部状态栏的记录数显示。
        /// </summary>
        private void UpdateTotalCount()
        {
            TotalCountText = $"共 {LogDataList.Count} 条记录";
        }

        #endregion
    }
}