// ============================================================
// 文件: ViewModels/LogDataViewModel.cs
// 描述: 日志数据页面 ViewModel —— 重构版
// 改动:
//   - 数据源从CSV切换为SQLite（通过ILogDatabaseService）
//   - 新增日期范围筛选（开始日期/结束日期）
//   - 新增判定结果筛选（OK/NG/全部）
//   - 新增方案筛选器联动（全部方案=Pin并集，具体方案=该方案列）
//   - 新增分页功能（每页条数+翻页导航）
// ============================================================

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 日志数据页面 ViewModel
    /// 管理检测日志的查询、筛选、分页和导出
    /// 数据来源：SQLite（LogRecords + PinResults表）
    /// </summary>
    public partial class LogDataViewModel : ObservableObject, INavigationAware
    {
        #region 服务注入

        private readonly ILogDatabaseService _logDatabaseService;
        private readonly IPlanStorageService _planStorageService;
        private readonly ICsvExportService _csvExportService;
        private readonly INavigationService _navigationService;
        private readonly ILogger<LogDataViewModel> _logger;

        #endregion

        #region 构造函数

        public LogDataViewModel(
            ILogDatabaseService logDatabaseService,
            IPlanStorageService planStorageService,
            ICsvExportService csvExportService,
            INavigationService navigationService,
            ILogger<LogDataViewModel> logger)
        {
            _logDatabaseService = logDatabaseService ?? throw new ArgumentNullException(nameof(logDatabaseService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _csvExportService = csvExportService ?? throw new ArgumentNullException(nameof(csvExportService));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // 初始化每页条数选项
            PageSizeOptions = new ObservableCollection<int> { 20, 50, 100 };
        }

        #endregion

        #region 检索条件属性

        /// <summary>机种名称下拉框数据源（从数据库去重获取）</summary>
        [ObservableProperty]
        private ObservableCollection<string> _machineTypes = new();

        /// <summary>用户选择的机种名称</summary>
        [ObservableProperty]
        private string _selectedMachineType = string.Empty;

        /// <summary>序列号检索条件</summary>
        [ObservableProperty]
        private string _searchSerialNumber = string.Empty;

        /// <summary>方案名称下拉框数据源（"全部方案" + 数据库已有方案）</summary>
        [ObservableProperty]
        private ObservableCollection<string> _planNames = new();

        /// <summary>用户选择的方案名称（"全部方案"表示不做方案筛选）</summary>
        [ObservableProperty]
        private string _selectedPlanName = "全部方案";

        /// <summary>开始日期（筛选时间范围的起始）</summary>
        [ObservableProperty]
        private DateTime? _startDate = null;

        /// <summary>结束日期（筛选时间范围的结束）</summary>
        [ObservableProperty]
        private DateTime? _endDate = null;

        /// <summary>判定结果筛选（"全部" / "OK" / "NG"）</summary>
        [ObservableProperty]
        private string _filterFinalResult = "全部";

        /// <summary>判定结果选项列表</summary>
        public ObservableCollection<string> FinalResultOptions { get; } = new()
        {
            "全部", "OK", "NG"
        };

        #endregion

        #region 分页属性

        /// <summary>当前页码（从1开始）</summary>
        [ObservableProperty]
        private int _pageIndex = 1;

        /// <summary>每页显示条数</summary>
        [ObservableProperty]
        private int _pageSize = 20;

        /// <summary>符合条件的总记录数</summary>
        [ObservableProperty]
        private int _totalCount = 0;

        /// <summary>总页数（根据TotalCount和PageSize计算）</summary>
        [ObservableProperty]
        private int _totalPages = 0;

        /// <summary>分页信息文本（如 "第1页/共5页"）</summary>
        [ObservableProperty]
        private string _pageInfoText = "第1页/共1页";

        /// <summary>每页条数选项（20/50/100）</summary>
        public ObservableCollection<int> PageSizeOptions { get; }

        /// <summary>是否可以翻到上一页</summary>
        [ObservableProperty]
        private bool _canGoPrevious = false;

        /// <summary>是否可以翻到下一页</summary>
        [ObservableProperty]
        private bool _canGoNext = false;

        /// <summary>
        /// 当PageSize变更时自动重新查询并重置到第1页
        /// </summary>
        partial void OnPageSizeChanged(int value)
        {
            PageIndex = 1;
            _ = SearchAsync();
        }

        #endregion

        #region 数据与显示属性

        /// <summary>当前页的日志数据列表（绑定DataGrid）</summary>
        [ObservableProperty]
        private ObservableCollection<LogRecord> _logDataList = new();

        /// <summary>当前数据中出现的所有动态列名（用于XAML动态生成列）</summary>
        /// <remarks>
        /// 全部方案模式：取所有行的PinResults中PinName的并集
        /// 具体方案模式：从方案JSON中按定义顺序取Pin名称
        /// </remarks>
        [ObservableProperty]
        private ObservableCollection<string> _dynamicHeaders = new();

        /// <summary>底部状态栏文字</summary>
        [ObservableProperty]
        private string _totalCountText = "共 0 条记录";

        /// <summary>是否正在加载数据（控制加载提示的显示）</summary>
        [ObservableProperty]
        private bool _isLoading;

        #endregion

        #region 命令

        /// <summary>
        /// 执行检索 —— 多条件AND组合 + 分页
        /// </summary>
        [RelayCommand]
        private async Task SearchAsync()
        {
            try
            {
                _logger.LogInformation(
                    "执行检索 - 机种:{Machine}, 序列号:{Serial}, 方案:{Plan}, " +
                    "日期:{Start}~{End}, 判定:{Result}, 页码:{Page}/{Size}",
                    SelectedMachineType, SearchSerialNumber, SelectedPlanName,
                    StartDate?.ToString("yyyy-MM-dd") ?? "*",
                    EndDate?.ToString("yyyy-MM-dd") ?? "*",
                    FilterFinalResult, PageIndex, PageSize);

                IsLoading = true;

                // 调用数据库分页查询
                var (records, total) = await _logDatabaseService.QueryLogsAsync(
                    series: string.IsNullOrWhiteSpace(SelectedMachineType) ? null : SelectedMachineType.Trim(),
                    serialNumber: string.IsNullOrWhiteSpace(SearchSerialNumber) ? null : SearchSerialNumber.Trim(),
                    planName: SelectedPlanName == "全部方案" ? null : SelectedPlanName,
                    startDate: StartDate,
                    endDate: EndDate,
                    finalResult: FilterFinalResult == "全部" ? null : FilterFinalResult,
                    pageIndex: PageIndex,
                    pageSize: PageSize);

                TotalCount = total;
                TotalPages = (int)Math.Ceiling((double)total / PageSize);
                LogDataList = new ObservableCollection<LogRecord>(records);

                // 更新动态列（方案联动）
                await UpdateDynamicHeadersAsync(records);

                // 更新UI状态
                UpdatePageInfo();
                UpdatePaginationButtons();
                UpdateTotalCount();

                _logger.LogInformation("检索完成，共{Total}条，当前页{Count}条", total, records.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检索失败");
                MessageBox.Show($"检索失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 重置所有检索条件并清空表格
        /// </summary>
        [RelayCommand]
        private void Reset()
        {
            SelectedMachineType = string.Empty;
            SearchSerialNumber = string.Empty;
            SelectedPlanName = "全部方案";
            StartDate = null;
            EndDate = null;
            FilterFinalResult = "全部";
            PageIndex = 1;

            LogDataList = new ObservableCollection<LogRecord>();
            DynamicHeaders = new ObservableCollection<string>();
            UpdateTotalCount();
            UpdatePageInfo();
            UpdatePaginationButtons();
        }

        /// <summary>
        /// 翻到上一页
        /// </summary>
        [RelayCommand]
        private async Task PreviousPageAsync()
        {
            if (PageIndex > 1)
            {
                PageIndex--;
                await SearchAsync();
            }
        }

        /// <summary>
        /// 翻到下一页
        /// </summary>
        [RelayCommand]
        private async Task NextPageAsync()
        {
            if (PageIndex < TotalPages)
            {
                PageIndex++;
                await SearchAsync();
            }
        }

        /// <summary>
        /// 导出当前全部数据（所有页）为CSV文件
        /// </summary>
        [RelayCommand]
        private async Task ExportToCsvAsync()
        {
            try
            {
                _logger.LogInformation("执行导出，当前筛选共{Total}条", TotalCount);

                // 查询全部数据（不分页，最多导出10000条防止内存溢出）
                var (allRecords, _) = await _logDatabaseService.QueryLogsAsync(
                    series: string.IsNullOrWhiteSpace(SelectedMachineType) ? null : SelectedMachineType.Trim(),
                    serialNumber: string.IsNullOrWhiteSpace(SearchSerialNumber) ? null : SearchSerialNumber.Trim(),
                    planName: SelectedPlanName == "全部方案" ? null : SelectedPlanName,
                    startDate: StartDate,
                    endDate: EndDate,
                    finalResult: FilterFinalResult == "全部" ? null : FilterFinalResult,
                    pageIndex: 1,
                    pageSize: 10000);

                // 导出数据列表（含行号索引）
                var exportData = allRecords.Select((record, index) => new
                {
                    Record = record,
                    RowIndex = index + 1  // 序号从1开始
                }).ToList();

                // 构建导出列定义 —— 统一使用 Func<LogRecord, string> 单参数委托
                var dynamicHeaders = DynamicHeaders.ToList();
                var headers = new List<(string Header, Func<LogRecord, string> ValueSelector)>
                                {
                                    // 固定列
                                    ("序号",       m => (allRecords.IndexOf(m) + 1).ToString()),
                                    ("机种名称",   m => m.Series),
                                    ("序列号",     m => m.SerialNumber),
                                    ("方案名称",   m => m.PlanName),
                                    ("操作员",     m => m.Operator),
                                    ("综合判定",   m => m.FinalResult),
                                };

                // 动态列（Pin检测项）
                foreach (var dh in dynamicHeaders)
                {
                    var capturedHeader = dh;
                    headers.Add((capturedHeader, m =>
                    {
                        var pinResult = m.PinResults?.FirstOrDefault(p => p.PinName == capturedHeader);
                        return pinResult?.Result ?? "-";
                    }
                    ));
                }

                // 检测时间列
                headers.Add(("检测时间", m => m.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")));

                var defaultFileName = $"日志数据导出_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                await _csvExportService.ExportWithDialogAsync(allRecords, headers, defaultFileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出失败");
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 清空所有检索条件输入框（不清空表格数据）
        /// 与「重置」的区别：重置会清空表格并恢复默认值；
        /// 清空条件只清空输入框，方便用户快速重新输入
        /// </summary>
        [RelayCommand]
        private void ClearConditions()
        {
            _logger.LogInformation("清空检索条件输入框");

            SelectedMachineType = string.Empty;
            SearchSerialNumber = string.Empty;
            SelectedPlanName = "全部方案";
            StartDate = null;
            EndDate = null;
            FilterFinalResult = "全部";
            PageIndex = 1;

            // 不清空表格数据，不触发重新检索
            // 用户可修改条件后手动点击「检索」
        }

        /// <summary>
        /// 返回主菜单
        /// </summary>
        [RelayCommand]
        private async Task GoBackAsync()
        {
            await _navigationService.NavigateToAsync<MainMenuView>();
        }

        #endregion

        #region INavigationAware

        /// <summary>
        /// 页面导航进入时触发 —— 加载下拉框选项，不展示数据
        /// 数据在用户点击"检索"时才加载
        /// </summary>
        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogInformation("进入日志数据页面");
            IsLoading = true;

            try
            {
                // 加载机种下拉列表（从数据库）
                var types = await _logDatabaseService.GetMachineTypesAsync();
                MachineTypes = new ObservableCollection<string>(types);

                // 加载方案下拉列表（"全部方案" + 数据库已有方案）
                var plans = await _logDatabaseService.GetPlanNamesAsync();
                PlanNames = new ObservableCollection<string>(
                    new[] { "全部方案" }.Concat(plans));

                // 不加载数据 —— 表格保持空白，等待用户点击"检索"
                LogDataList = new ObservableCollection<LogRecord>();
                DynamicHeaders = new ObservableCollection<string>();
                UpdateTotalCount();
                UpdatePageInfo();
                UpdatePaginationButtons();

                _logger.LogInformation("日志数据页面就绪，机种数:{Types}, 方案数:{Plans}", types.Count, plans.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化日志数据页面失败");
                MessageBox.Show($"初始化失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

        /// <summary>
        /// 更新动态列名集合（方案联动核心逻辑）
        /// 全部方案：取所有记录中PinResults的PinName并集
        /// 具体方案：从方案JSON中按定义顺序取Pin名称
        /// </summary>
        private async Task UpdateDynamicHeadersAsync(List<LogRecord> records)
        {
            List<string> headers;

            if (SelectedPlanName == "全部方案")
            {
                // 全部方案模式：取Pin名称并集
                headers = records
                    .SelectMany(r => r.PinResults ?? new List<PinResult>())
                    .Select(p => p.PinName)
                    .Distinct()
                    .ToList();
            }
            else
            {
                // 具体方案模式：从方案JSON按定义顺序获取Pin列表
                var allPlans = await _planStorageService.LoadAllPlansAsync();
                var plan = allPlans.FirstOrDefault(p => p.PlanName == SelectedPlanName);
                headers = plan?.Items
                    .OrderBy(i => i.Index)
                    .Select(i => i.ItemName)
                    .ToList() ?? new List<string>();
            }

            DynamicHeaders = new ObservableCollection<string>(headers);
        }

        /// <summary>
        /// 更新底部状态栏记录数显示
        /// </summary>
        private void UpdateTotalCount()
        {
            TotalCountText = $"共 {TotalCount} 条记录";
        }

        /// <summary>
        /// 更新分页信息文本
        /// </summary>
        private void UpdatePageInfo()
        {
            PageInfoText = TotalPages > 0
                ? $"第 {PageIndex} 页 / 共 {TotalPages} 页"
                : "第 1 页 / 共 1 页";
        }

        /// <summary>
        /// 更新翻页按钮的可用状态
        /// </summary>
        private void UpdatePaginationButtons()
        {
            CanGoPrevious = PageIndex > 1;
            CanGoNext = PageIndex < TotalPages;
        }

        #endregion
    }
}