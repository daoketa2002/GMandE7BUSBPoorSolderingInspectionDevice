using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    public partial class TestPageViewModel : ObservableObject, INavigationAware, IDisposable
    {
        #region 服务注入

        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly ILogger<TestPageViewModel> _logger;
        private readonly Serilog.ILogger _serilogLogger;

        #endregion

        #region 构造函数

        public TestPageViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            ILogger<TestPageViewModel> logger)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _logger = logger;
            _serilogLogger = Log.ForContext<TestPageViewModel>();

            // 初始化实时时钟
            InitializeClock();

            // 初始化测试项目（示例数据）
            InitializeTestItems();
        }

        #endregion

        #region 顶部左侧 - 生产统计面板

        /// <summary>
        /// 方案名称
        /// </summary>
        [ObservableProperty]
        private string _schemeName = "默认测试方案";

        /// <summary>
        /// 检查数量
        /// </summary>
        [ObservableProperty]
        private int _totalCount = 0;

        /// <summary>
        /// 良品数量
        /// </summary>
        [ObservableProperty]
        private int _passCount = 0;

        /// <summary>
        /// 不良数量
        /// </summary>
        [ObservableProperty]
        private int _failCount = 0;

        /// <summary>
        /// 清零计数器（需二次确认）
        /// </summary>
        [RelayCommand]
        private async Task ClearCountersAsync()
        {
            var confirmed = await _notificationService.ConfirmAsync(
                "确定要清零当前批次的统计数据吗？此操作不可恢复！",
                "清零确认");

            if (confirmed)
            {
                TotalCount = 0;
                PassCount = 0;
                FailCount = 0;
                _serilogLogger.Information("计数器已清零");
                await _notificationService.ShowInfoAsync("计数器已清零", "操作成功");
            }
        }

        #endregion

        #region 顶部中间 - 设备连接状态看板

        /// <summary>
        /// 实时时钟
        /// </summary>
        [ObservableProperty]
        private string _currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        private DispatcherTimer? _clockTimer;

        /// <summary>
        /// PLC 连接状态 (true=已连接)
        /// </summary>
        [ObservableProperty]
        private bool _isPlcConnected = false;

        /// <summary>
        /// PLC 连接状态文本
        /// </summary>
        [ObservableProperty]
        private string _plcStatusText = "断开";

        /// <summary>
        /// 扫描仪连接状态
        /// </summary>
        [ObservableProperty]
        private bool _isScannerConnected = false;

        [ObservableProperty]
        private string _scannerStatusText = "断开";

        /// <summary>
        /// 万用表连接状态
        /// </summary>
        [ObservableProperty]
        private bool _isDmmConnected = false;

        [ObservableProperty]
        private string _dmmStatusText = "断开";

        /// <summary>
        /// PC 状态
        /// </summary>
        [ObservableProperty]
        private bool _isPcReady = true;

        [ObservableProperty]
        private string _pcStatusText = "就绪";

        /// <summary>
        /// PLC 连接/重连命令
        /// </summary>
        [RelayCommand]
        private async Task ReconnectPlcAsync()
        {
            PlcStatusText = "连接中...";
            _serilogLogger.Information("正在尝试连接PLC...");

            // TODO: 调用实际的 PLC 连接服务
            await Task.Delay(500);

            IsPlcConnected = !IsPlcConnected;
            PlcStatusText = IsPlcConnected ? "已连接" : "断开";
            _serilogLogger.Information("PLC 连接状态: {Status}", PlcStatusText);
        }

        /// <summary>
        /// 扫描仪重连
        /// </summary>
        [RelayCommand]
        private async Task ReconnectScannerAsync()
        {
            ScannerStatusText = "连接中...";
            await Task.Delay(500);
            IsScannerConnected = !IsScannerConnected;
            ScannerStatusText = IsScannerConnected ? "已连接" : "断开";
        }

        /// <summary>
        /// 万用表重连
        /// </summary>
        [RelayCommand]
        private async Task ReconnectDmmAsync()
        {
            DmmStatusText = "连接中...";
            await Task.Delay(500);
            IsDmmConnected = !IsDmmConnected;
            DmmStatusText = IsDmmConnected ? "已连接" : "断开";
        }

        /// <summary>
        /// 初始化实时时钟
        /// </summary>
        private void InitializeClock()
        {
            _clockTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += (s, e) =>
            {
                CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };
            _clockTimer.Start();
        }

        #endregion

        #region 顶部右侧 - 测试状态显示

        /// <summary>
        /// 测试状态：待机/测试中/报错
        /// </summary>
        [ObservableProperty]
        private string _testStatus = "待机";

        /// <summary>
        /// 测试状态背景色
        /// </summary>
        [ObservableProperty]
        private string _testStatusColor = "#3498DB";

        /// <summary>
        /// 开始测试命令
        /// </summary>
        [RelayCommand]
        private async Task StartTestAsync()
        {
            // 序列号非空校验
            if (string.IsNullOrWhiteSpace(SerialNumber))
            {
                await _notificationService.ShowWarningAsync("请输入产品序列号！", "校验失败");
                return;
            }

            // 作业员非空校验
            if (string.IsNullOrWhiteSpace(OperatorName))
            {
                await _notificationService.ShowWarningAsync("请选择作业员！", "校验失败");
                return;
            }

            TestStatus = "测试中";
            TestStatusColor = "#F39C12";
            _serilogLogger.Information("开始测试 - 机种: {Model}, 序列号: {SN}, 作业员: {Operator}",
                ModelName, SerialNumber, OperatorName);

            // 模拟测试流程
            try
            {
                foreach (var item in TestItems)
                {
                    item.Judgment = string.Empty;
                    item.CheckResult = string.Empty;
                }

                for (int i = 0; i < TestItems.Count; i++)
                {
                    var item = TestItems[i];

                    // 模拟测试延迟
                    await Task.Delay(300);

                    // 模拟测试结果
                    var isPass = new Random().Next(0, 10) > 1; // 80% 通过率
                    item.Judgment = isPass ? "OK" : "NG";
                    item.CheckResult = isPass ? $"{new Random().Next(100, 500) / 100.0:F2} Ω" : "开路";
                }

                // 更新计数器
                TotalCount++;
                var ngCount = 0;
                foreach (var item in TestItems)
                {
                    if (item.Judgment == "NG")
                        ngCount++;
                }

                if (ngCount == 0)
                {
                    PassCount++;
                    TestStatus = "测试完成 - 良品";
                    TestStatusColor = "#27AE60";
                }
                else
                {
                    FailCount++;
                    TestStatus = "测试完成 - 不良";
                    TestStatusColor = "#E74C3C";
                }

                _serilogLogger.Information("测试完成 - 总数: {Total}, 良品: {Pass}, 不良: {Fail}",
                    TotalCount, PassCount, FailCount);
            }
            catch (Exception ex)
            {
                TestStatus = "报错";
                TestStatusColor = "#E74C3C";
                _serilogLogger.Error(ex, "测试过程发生错误");
                await _notificationService.ShowErrorAsync($"测试失败：{ex.Message}", "错误");
            }
        }

        #endregion

        #region 中部 - 信息录入区

        /// <summary>
        /// 机种名称
        /// </summary>
        [ObservableProperty]
        private string _modelName = string.Empty;

        /// <summary>
        /// 序列号
        /// </summary>
        [ObservableProperty]
        private string _serialNumber = string.Empty;

        /// <summary>
        /// 作业员名称
        /// </summary>
        [ObservableProperty]
        private string _operatorName = string.Empty;

        /// <summary>
        /// 作业员是否可编辑（选择后锁定）
        /// </summary>
        [ObservableProperty]
        private bool _isOperatorEditable = true;

        #endregion

        #region 下部 - 测试项目列表

        /// <summary>
        /// 测试项目集合
        /// </summary>
        public ObservableCollection<TestItemModel> TestItems { get; } = new();

        /// <summary>
        /// 初始化测试项目（示例数据）
        /// </summary>
        private void InitializeTestItems()
        {
            var items = new[]
            {
                new { Name = "A1-A2", Condition = "OPEN 开路" },
                new { Name = "A2-A3", Condition = "SHORT 短路" },
                new { Name = "A3-A4", Condition = "OPEN 开路" },
                new { Name = "A4-A5", Condition = "SHORT 短路" },
                new { Name = "B1-B2", Condition = "OPEN 开路" },
                new { Name = "B2-B3", Condition = "SHORT 短路" },
                new { Name = "B3-B4", Condition = "OPEN 开路" },
                new { Name = "C1-C2", Condition = "RESISTANCE 电阻" },
                new { Name = "C2-C3", Condition = "OPEN 开路" },
                new { Name = "D1-D2", Condition = "SHORT 短路" },
            };

            for (int i = 0; i < items.Length; i++)
            {
                TestItems.Add(new TestItemModel
                {
                    Index = i + 1,
                    ItemName = items[i].Name,
                    CheckCondition = items[i].Condition,
                    CheckResult = string.Empty,
                    Judgment = string.Empty
                });
            }
        }

        #endregion

        #region 底部 - 日志与操作区

        /// <summary>
        /// 日志集合
        /// </summary>
        public ObservableCollection<string> LogMessages { get; } = new();

        /// <summary>
        /// 添加日志
        /// </summary>
        private void AddLog(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogMessages.Add($"[{timestamp}] {message}");

                // 限制日志条数防止内存溢出
                while (LogMessages.Count > 500)
                {
                    LogMessages.RemoveAt(0);
                }
            });
        }

        /// <summary>
        /// 终了按钮 - 返回主菜单
        /// </summary>
        [RelayCommand]
        private async Task FinishAndReturnAsync()
        {
            var confirmed = await _notificationService.ConfirmAsync(
                "确定要结束当前测试并返回主菜单吗？",
                "确认返回");

            if (confirmed)
            {
                _serilogLogger.Information("用户点击终了按钮，返回主菜单");
                await _navigationService.NavigateToAsync<Views.MainMenuView>();
            }
        }

        #endregion

        #region INavigationAware 实现

        //public Task OnNavigatedToAsync(object? parameter = null)
        //{
        //    _serilogLogger.Debug("进入测试页面");
        //    AddLog("测试页面已就绪");
        //    return Task.CompletedTask;
        //}

        public Task OnNavigatedToAsync(object? parameter = null)
        {
            _serilogLogger.Debug("进入测试页面");

            // 接收传入的作业员参数
            if (parameter is OperatorModel selectedOperator)
            {
                OperatorName = selectedOperator.Name;
                IsOperatorEditable = false; // 锁定作业员输入框
                AddLog($"当前作业员: {selectedOperator.Name}");
                _serilogLogger.Information("测试页收到作业员参数: {Operator}", selectedOperator.Name);
            }
            else
            {
                AddLog("测试页面已就绪（未指定作业员）");
            }

            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            _serilogLogger.Debug("离开测试页面");
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            // 如果正在测试中，询问是否确认离开
            if (TestStatus == "测试中")
            {
                return Task.FromResult(false); // 测试中不允许离开
            }
            return Task.FromResult(true);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _clockTimer?.Stop();
            _clockTimer = null;
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}