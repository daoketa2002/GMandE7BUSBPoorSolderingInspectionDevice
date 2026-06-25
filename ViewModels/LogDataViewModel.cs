// ============================================================
// 文件: ViewModels/LogDataViewModel.cs
// 描述: 日志数据页面 ViewModel —— 重构版
// 改动:
//   - 数据源从CSV切换为SQLite（通过ITestRecordStorage）
//   - 新增日期范围筛选（开始日期/结束日期）
//   - 新增判定结果筛选（OK/NG/全部）
//   - 新增方案筛选器联动（全部方案=Pin并集，具体方案=该方案列）
//   - 新增分页功能（每页条数+翻页导航）
//   ⭐ 新增扫描枪集成：扫码自动填充机种名称和序列号
// ============================================================

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Common;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 日志数据页面 ViewModel
    /// 管理检测日志的查询、筛选、分页和导出
    /// 数据来源：CSV（通过ITestRecordStorage）
    /// 集成扫描枪：扫码自动填充机种名称和序列号
    /// </summary>
    public partial class LogDataViewModel : ObservableObject, INavigationAware
    {
        #region 服务注入

        private readonly IPlanStorageService _planStorageService;
        private readonly ICsvExportService _csvExportService;
        private readonly INavigationService _navigationService;
        private readonly ITestRecordStorage _testRecordStorage;
        private readonly IDeviceConnectionManager _deviceManager;
        private readonly ILogger<LogDataViewModel> _logger;

        #endregion

        #region 构造函数

        public LogDataViewModel(
            IPlanStorageService planStorageService,
            ICsvExportService csvExportService,
            INavigationService navigationService,
            ITestRecordStorage testRecordStorage,
            IDeviceConnectionManager deviceManager,
            ILogger<LogDataViewModel> logger)
        {
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _csvExportService = csvExportService ?? throw new ArgumentNullException(nameof(csvExportService));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _testRecordStorage = testRecordStorage ?? throw new ArgumentNullException(nameof(testRecordStorage));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ⭐ 同步初始状态
            IsScannerConnected = _deviceManager.IsScannerConnected;
            ScannerStatusText = _deviceManager.ScannerStatusText;

            // ⭐ 订阅全局设备管理器事件
            _deviceManager.ScannerConnectionStateChanged += OnScannerConnectionStateChanged;
            _deviceManager.BarcodeScanned += OnScannerBarcodeScanned;

            // 初始化每页条数选项
            PageSizeOptions = new ObservableCollection<int> { 20, 50, 100 };
        }

        #endregion

        #region 扫描枪相关属性（通过 Helper 暴露）

        /// <summary>扫描枪是否已连接</summary>
        [ObservableProperty]
        private bool _isScannerConnected;

        /// <summary>扫描枪状态文本</summary>
        [ObservableProperty]
        private string _scannerStatusText = "扫描枪未连接";

        /// <summary>最近扫描的原始条码</summary>
        [ObservableProperty]
        private string _scannedBarcode = string.Empty;

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

        /// <summary>是否有开始日期（控制×清除按钮显隐）</summary>
        [ObservableProperty]
        private bool _hasStartDate;

        /// <summary>是否有结束日期（控制×清除按钮显隐）</summary>
        [ObservableProperty]
        private bool _hasEndDate;

        /// <summary>
        /// 当StartDate变化时，同步更新HasStartDate
        /// </summary>
        partial void OnStartDateChanged(DateTime? value)
        {
            HasStartDate = value.HasValue;
        }

        /// <summary>
        /// 当EndDate变化时，同步更新HasEndDate
        /// </summary>
        partial void OnEndDateChanged(DateTime? value)
        {
            HasEndDate = value.HasValue;
        }

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
        [ObservableProperty]
        private ObservableCollection<string> _dynamicHeaders = new();

        /// <summary>底部状态栏文字</summary>
        [ObservableProperty]
        private string _totalCountText = "共 0 条记录";

        /// <summary>是否正在加载数据（控制加载提示的显示）</summary>
        [ObservableProperty]
        private bool _isLoading;

        #endregion

        #region 扫描枪事件处理

        // ⭐ 新增：扫描枪连接状态变更回调
        private void OnScannerConnectionStateChanged(object? sender, DeviceConnectionStateChangedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsScannerConnected = e.IsConnected;
                ScannerStatusText = e.StatusText;
            });
        }


        /// <summary>
        /// 扫描枪条码接收回调
        /// 自动填充机种名称（SelectedMachineType）和序列号（SearchSerialNumber）
        /// 
        /// 条码格式："T998248391,250919,00004Z"
        ///   第1段 → 机种名称
        ///   第3段 → 序列号
        /// </summary>
        private void OnScannerBarcodeScanned(object? sender, BarcodeParsedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ScannedBarcode = e.RawBarcode;
                _logger.LogInformation("扫描枪收到条码: {Barcode}, 机种={Model}, 序列号={Serial}",
                    e.RawBarcode, e.ModelName, e.SerialPart);

                // 自动填充机种名称
                if (!string.IsNullOrWhiteSpace(e.ModelName))
                {
                    SelectedMachineType = e.ModelName;
                }

                // 自动填充序列号
                if (!string.IsNullOrWhiteSpace(e.SerialPart))
                {
                    SearchSerialNumber = e.SerialPart;
                }
            });
        }

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
                // ★ 校验日期区间：开始日期不能晚于结束日期
                if (StartDate.HasValue && EndDate.HasValue && StartDate.Value > EndDate.Value)
                {
                    _logger.LogWarning("日期区间校验失败: 开始日期 {Start} 晚于结束日期 {End}",
                        StartDate.Value.ToString("yyyy-MM-dd"), EndDate.Value.ToString("yyyy-MM-dd"));
                    System.Windows.MessageBox.Show(
                        $"开始日期（{StartDate.Value:yyyy-MM-dd}）不能晚于结束日期（{EndDate.Value:yyyy-MM-dd}）！\n请重新选择日期后检索。",
                        "日期区间错误",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }

                _logger.LogInformation(
                    "执行检索 - 机种:{Machine}, 序列号:{Serial}, 方案:{Plan}, " +
                    "日期:{Start}~{End}, 判定:{Result}, 页码:{Page}/{Size}",
                    SelectedMachineType, SearchSerialNumber, SelectedPlanName,
                    StartDate?.ToString("yyyy-MM-dd") ?? "*",
                    EndDate?.ToString("yyyy-MM-dd") ?? "*",
                    FilterFinalResult, PageIndex, PageSize);

                IsLoading = true;

                // 分页查询
                var (records, total) = await _testRecordStorage.QueryRecordsAsync(
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

                string message = ex.Message.Contains("being used by another process")
                    ? "检索失败：日志文件正被其他程序占用（如Excel/记事本），请关闭后重试。"
                    : $"检索失败：{ex.Message}";

                MessageBox.Show(message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                var (allRecords, _) = await _testRecordStorage.QueryRecordsAsync(
                    series: string.IsNullOrWhiteSpace(SelectedMachineType) ? null : SelectedMachineType.Trim(),
                    serialNumber: string.IsNullOrWhiteSpace(SearchSerialNumber) ? null : SearchSerialNumber.Trim(),
                    planName: SelectedPlanName == "全部方案" ? null : SelectedPlanName,
                    startDate: StartDate,
                    endDate: EndDate,
                    finalResult: FilterFinalResult == "全部" ? null : FilterFinalResult,
                    pageIndex: 1,
                    pageSize: 10000);

                // 构建导出列定义
                var dynamicHeaders = DynamicHeaders.ToList();
                var headers = new List<(string Header, Func<LogRecord, string> ValueSelector)>
                                {
                                    ("序号",       m => (allRecords.IndexOf(m) + 1).ToString()),
                                    ("机种名称",   m => m.Series),
                                    ("序列号",     m => m.SerialNumber),
                                    ("方案名称",   m => m.PlanName),
                                    ("操作员",     m => m.Operator),
                                    ("综合判定",   m => m.FinalResult),
                                };

                // 动态列
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

                // 日期和时间
                headers.Add(("日期", m => m.Timestamp.ToString("yyyy年MM月dd日")));
                headers.Add(("时间", m => m.Timestamp.ToString("HH时mm分ss秒")));

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
        /// </summary>
        [RelayCommand]
        private void ClearConditions()
        {
            _logger.LogInformation("清空检索条件输入框（含开始日期、结束日期）");

            SelectedMachineType = string.Empty;
            SearchSerialNumber = string.Empty;
            SelectedPlanName = "全部方案";
            StartDate = null;    // 自动触发 HasStartDate=false，×按钮隐藏
            EndDate = null;      // 自动触发 HasEndDate=false，×按钮隐藏
            FilterFinalResult = "全部";
            PageIndex = 1;
        }

        /// <summary>
        /// 清除开始日期
        /// </summary>
        [RelayCommand]
        private void ClearStartDate()
        {
            _logger.LogInformation("用户点击×清除了开始日期");
            StartDate = null;  // 置null后OnStartDateChanged自动将HasStartDate设为false，×按钮自动隐藏
        }

        /// <summary>
        /// 清除结束日期
        /// </summary>
        [RelayCommand]
        private void ClearEndDate()
        {
            _logger.LogInformation("用户点击×清除了结束日期");
            EndDate = null;   // 置null后OnEndDateChanged自动将HasEndDate设为false，×按钮自动隐藏
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
        /// 页面导航进入时触发
        /// 加载下拉框选项，订阅并自动连接扫描枪
        /// 数据在用户点击"检索"时才加载
        /// </summary>
        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogInformation("进入日志数据页面");
            IsLoading = true;

            try
            {
                // 加载机种下拉列表
                var types = await _testRecordStorage.GetMachineTypesAsync();
                MachineTypes = new ObservableCollection<string>(types);

                // 加载方案下拉列表
                var plans = await _testRecordStorage.GetPlanNamesAsync();
                PlanNames = new ObservableCollection<string>(
                    new[] { "全部方案" }.Concat(plans));

                // 不加载数据 —— 表格保持空白，等待用户点击"检索"
                LogDataList = new ObservableCollection<LogRecord>();
                DynamicHeaders = new ObservableCollection<string>();
                UpdateTotalCount();
                UpdatePageInfo();
                UpdatePaginationButtons();

                _logger.LogInformation("日志数据页面就绪，机种数:{Types}, 方案数:{Plans}", types.Count, plans.Count);

                // ⭐ 重新订阅扫码事件（确保不重复订阅）
                _deviceManager.BarcodeScanned -= OnScannerBarcodeScanned;
                _deviceManager.BarcodeScanned += OnScannerBarcodeScanned;

                // ⭐ 同步扫描枪连接状态（不再手动连接）
                IsScannerConnected = _deviceManager.IsScannerConnected;
                ScannerStatusText = _deviceManager.ScannerStatusText;
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

        /// <summary>
        /// 页面导航离开时触发
        /// ⭐ 必须取消订阅，防止在其他页面扫码时误触发本页检索逻辑
        /// </summary>
        public Task OnNavigatedFromAsync()
        {
            _logger.LogInformation("离开日志数据页面");

            // ⭐ 页面离开时取消订阅扫码事件，避免在其他页面触发
            if (_deviceManager != null)
            {
                _deviceManager.BarcodeScanned -= OnScannerBarcodeScanned;
            }

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
        /// 统一从实际数据中提取Pin列名，确保始终有列显示
        /// </summary>
        private async Task UpdateDynamicHeadersAsync(List<LogRecord> records)
        {
            List<string> headers;

            headers = records
                .SelectMany(r => r.PinResults ?? new List<PinResult>())
                .Select(p => p.PinName)
                .Distinct()
                .ToList();

            // 如果数据中有具体方案名，且方案JSON存在，则按JSON定义顺序排序
            if (SelectedPlanName != "全部方案" && !string.IsNullOrWhiteSpace(SelectedPlanName))
            {
                try
                {
                    var allPlans = await _planStorageService.LoadAllPlansAsync();
                    var plan = allPlans.FirstOrDefault(p => p.PlanName == SelectedPlanName);
                    if (plan != null && plan.Items.Count > 0)
                    {
                        var orderedNames = plan.Items
                            .OrderBy(i => i.Index)
                            .Select(i => i.ItemName)
                            .ToList();

                        var csvOnlyHeaders = headers
                            .Where(h => !orderedNames.Contains(h, StringComparer.OrdinalIgnoreCase))
                            .ToList();

                        headers = orderedNames
                            .Where(o => headers.Contains(o, StringComparer.OrdinalIgnoreCase))
                            .Concat(csvOnlyHeaders)
                            .ToList();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "从方案JSON获取Pin顺序失败，使用CSV原始顺序");
                }
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