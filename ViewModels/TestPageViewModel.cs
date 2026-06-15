// 📁 ViewModels/TestPageViewModel.cs（完整修正版）
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    public partial class TestPageViewModel : ObservableObject, INavigationAware, IDisposable
    {
        #region 服务注入

        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly ILogger<TestPageViewModel> _logger;
        private readonly Serilog.ILogger _serilogLogger;
        private readonly ICurrentPlanService _currentPlanService;

        // ⭐ 硬件服务
        private readonly ITcpClientPLCMotionService _plcService;
        private readonly GwInstekGDM9060Driver _dmmDriver;
        private readonly HoneywellH1900Scanner? _scanner;
        private readonly InspectionEngine? _inspectionEngine;

        // ⭐ 新增：设置服务（避免手动new）
        private readonly IDeviceSettingsService _settingsService;
        private readonly IOperatorStateService _operatorStateService;

        #endregion

        #region 构造函数

        public TestPageViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IOperatorStateService operatorStateService,
            ILogger<TestPageViewModel> logger,
            ITcpClientPLCMotionService plcService,
            GwInstekGDM9060Driver dmmDriver,
            IDeviceSettingsService settingsService,
            ICurrentPlanService currentPlanService,
            HoneywellH1900Scanner? scanner = null,
            InspectionEngine? inspectionEngine = null)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _logger = logger;
            _serilogLogger = Log.ForContext<TestPageViewModel>();
            _operatorStateService = operatorStateService ?? throw new ArgumentNullException(nameof(operatorStateService));
            _currentPlanService = currentPlanService ?? throw new ArgumentNullException(nameof(currentPlanService));

            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _dmmDriver = dmmDriver ?? throw new ArgumentNullException(nameof(dmmDriver));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _scanner = scanner;
            _inspectionEngine = inspectionEngine;

            InitializeClock();
            SubscribeToHardwareEvents();
        }

        #endregion

        #region 硬件事件订阅

        private void SubscribeToHardwareEvents()
        {
            // PLC 通知事件
            _plcService.OnNotification += (s, e) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (e.Type == NotificationType.Success || e.Type == NotificationType.ConnectionRestored)
                    {
                        IsPlcConnected = true;
                        PlcStatusText = "已连接";
                    }
                    else if (e.Type == NotificationType.Error || e.Type == NotificationType.Critical)
                    {
                        IsPlcConnected = false;
                        PlcStatusText = "断开";
                    }
                });
            };

            // 万用表连接状态
            _dmmDriver.ConnectionStateChanged += (s, connected) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsDmmConnected = connected;
                    DmmStatusText = connected ? "已连接" : "断开";
                });
            };

            // 万用表测量数据
            _dmmDriver.MeasurementReceived += (s, e) =>
            {
                AddLog($"📏 万用表读数: {e.Result}");
            };

            // 扫描枪事件
            if (_scanner != null)
            {
                _scanner.BarcodeReceived += OnScannerBarcodeReceived;
                _scanner.ConnectionStateChanged += (s, connected) =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        IsScannerConnected = connected;
                        ScannerStatusText = connected ? "已连接" : "断开";
                    });
                };
            }

            // 检测引擎事件
            if (_inspectionEngine != null)
            {
                _inspectionEngine.StateChanged += OnInspectionStateChanged;
                _inspectionEngine.StepCompleted += OnStepCompleted;
                _inspectionEngine.InspectionCompleted += OnInspectionCompleted;
                _inspectionEngine.LogMessage += (s, msg) => AddLog(msg);
            }
        }

        private void OnScannerBarcodeReceived(object? sender, BarcodeReceivedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                SerialNumber = e.Barcode;
                AddLog($"📷 扫描到条码: {e.Barcode}");
            });
        }

        private void OnInspectionStateChanged(object? sender, InspectionStateChangedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                TestStatus = e.NewState switch
                {
                    InspectionState.Idle => "待机",
                    InspectionState.Initializing => "初始化中",
                    InspectionState.Testing => "测试中",
                    InspectionState.CompletedPass => "测试完成 - 良品",
                    InspectionState.CompletedFail => "测试完成 - 不良",
                    InspectionState.Aborted => "已中止",
                    InspectionState.Error => "报错",
                    _ => "未知"
                };
            });
        }

        private void OnStepCompleted(object? sender, StepCompletedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var item = TestItems.ElementAtOrDefault(e.StepIndex);
                if (item != null)
                {
                    item.CheckResult = e.Measurement.IsValid
                        ? $"{e.Measurement.Value:F4} Ω"
                        : "测量失败";
                    item.Judgment = e.TestPoint.Judgment;
                }
            });
        }

        private void OnInspectionCompleted(object? sender, InspectionCompletedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                TotalCount++;
                if (e.Result.IsAllPassed)
                {
                    PassCount++;
                }
                else
                {
                    FailCount++;
                }

                var msg = e.Result.IsAllPassed
                    ? $"✅ 检测完成: 良品 (耗时{e.Result.Duration.TotalSeconds:F1}s)"
                    : $"❌ 检测完成: 不良 (耗时{e.Result.Duration.TotalSeconds:F1}s)";
                AddLog(msg);
            });
        }

        #endregion

        #region 顶部左侧 - 生产统计面板

        [ObservableProperty]
        private string _schemeName = "默认测试方案";

        [ObservableProperty]
        private int _totalCount = 0;

        [ObservableProperty]
        private int _passCount = 0;

        [ObservableProperty]
        private int _failCount = 0;

        [RelayCommand]
        private async Task ClearCountersAsync()
        {
            var confirmed = await _notificationService.ConfirmAsync(
                "确定要清零当前批次的统计数据吗？此操作不可恢复！", "清零确认");

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

        [ObservableProperty]
        private string _currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        private DispatcherTimer? _clockTimer;

        [ObservableProperty]
        private bool _isPlcConnected = false;

        [ObservableProperty]
        private string _plcStatusText = "断开";

        [ObservableProperty]
        private bool _isScannerConnected = false;

        [ObservableProperty]
        private string _scannerStatusText = "断开";

        [ObservableProperty]
        private bool _isDmmConnected = false;

        [ObservableProperty]
        private string _dmmStatusText = "断开";

        /// <summary>
        /// PLC 连接/重连命令
        /// </summary>
        [RelayCommand]
        private async Task ReconnectPlcAsync()
        {
            PlcStatusText = "连接中...";
            _serilogLogger.Information("正在尝试连接PLC...");

            try
            {
                if (_plcService.IsConnected)
                {
                    await _plcService.StopAsync();
                }

                await _plcService.StartAsync();

                IsPlcConnected = _plcService.IsConnected;
                PlcStatusText = _plcService.IsConnected ? "已连接" : "断开";

                if (_plcService.IsConnected)
                {
                    AddLog("✅ PLC 连接成功");
                }
            }
            catch (Exception ex)
            {
                _serilogLogger.Error(ex, "PLC连接失败");
                IsPlcConnected = false;
                PlcStatusText = "连接失败";
                AddLog($"❌ PLC连接失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 扫描仪重连（✅ 修正：使用注入的 _settingsService）
        /// </summary>
        [RelayCommand]
        private async Task ReconnectScannerAsync()
        {
            ScannerStatusText = "连接中...";

            try
            {
                if (_scanner != null)
                {
                    _scanner.Disconnect();

                    // ✅ 修正：直接使用注入的 _settingsService
                    var settings = _settingsService.LoadSettings();
                    var portName = settings.ScannerSerialCommunication?.SerialNumber ?? "COM9";
                    var result = _scanner.Connect(portName);

                    IsScannerConnected = result;
                    ScannerStatusText = result ? "已连接" : "断开";

                    AddLog(result
                        ? $"✅ 扫描枪已连接 ({portName})"
                        : $"❌ 扫描枪连接失败 ({portName})");
                }
                else
                {
                    ScannerStatusText = "未配置";
                    AddLog("⚠️ 扫描枪服务未注册");
                }
            }
            catch (Exception ex)
            {
                IsScannerConnected = false;
                ScannerStatusText = "连接失败";
                AddLog($"❌ 扫描枪连接失败: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// 万用表重连（✅ 修正：使用注入的 _settingsService）
        /// </summary>
        [RelayCommand]
        private async Task ReconnectDmmAsync()
        {
            DmmStatusText = "连接中...";

            try
            {
                // ✅ 修正：直接使用注入的 _settingsService
                var settings = _settingsService.LoadSettings();
                var host = settings.TcpClientGWInstek?.Host ?? "192.168.1.4";
                var port = settings.TcpClientGWInstek?.Port ?? 5025;

                if (_dmmDriver.IsConnected)
                {
                    await _dmmDriver.DisconnectAsync();
                }

                var result = await _dmmDriver.ConnectAsync(host, port);

                IsDmmConnected = result;
                DmmStatusText = result ? "已连接" : "断开";

                AddLog(result
                    ? $"✅ 万用表已连接 ({host}:{port})"
                    : $"❌ 万用表连接失败 ({host}:{port})");
            }
            catch (Exception ex)
            {
                IsDmmConnected = false;
                DmmStatusText = "连接失败";
                AddLog($"❌ 万用表连接失败: {ex.Message}");
            }
        }

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

        [ObservableProperty]
        private string _testStatus = "待机";

        /// <summary>
        /// 开始测试命令
        /// </summary>
        [RelayCommand]
        private async Task StartTestAsync()
        {
            if (string.IsNullOrWhiteSpace(SerialNumber))
            {
                await _notificationService.ShowWarningAsync("请输入产品序列号！", "校验失败");
                return;
            }

            if (string.IsNullOrWhiteSpace(OperatorName))
            {
                await _notificationService.ShowWarningAsync("请选择作业员！", "校验失败");
                return;
            }

            if (!_plcService.IsConnected)
            {
                await _notificationService.ShowWarningAsync("PLC未连接，请先连接PLC！", "硬件未就绪");
                return;
            }

            if (!_dmmDriver.IsConnected)
            {
                await _notificationService.ShowWarningAsync("万用表未连接，请先连接万用表！", "硬件未就绪");
                return;
            }

            try
            {
                AddLog($"🚀 开始检测 - 机种:{ModelName}, SN:{SerialNumber}, 作业员:{OperatorName}");

                if (_inspectionEngine != null)
                {
                    await _inspectionEngine.RunInspectionAsync(
                        SerialNumber, ModelName, OperatorName);
                }
                else
                {
                    AddLog("⚠️ 检测引擎未注册，使用模拟模式");
                    await RunSimulatedTestAsync();
                }
            }
            catch (Exception ex)
            {
                TestStatus = "报错";
                _serilogLogger.Error(ex, "测试过程发生错误");
                AddLog($"❌ 测试失败: {ex.Message}");
                await _notificationService.ShowErrorAsync($"测试失败：{ex.Message}", "错误");
            }
        }

        /// <summary>
        /// 模拟测试（无真实硬件时的回退方案）
        /// </summary>
        private async Task RunSimulatedTestAsync()
        {
            TestStatus = "测试中";

            foreach (var item in TestItems)
            {
                item.Judgment = string.Empty;
                item.CheckResult = string.Empty;
            }

            var random = new Random();
            int ngCount = 0;

            for (int i = 0; i < TestItems.Count; i++)
            {
                var item = TestItems[i];
                await Task.Delay(200);

                var isPass = random.Next(0, 10) > 1;
                item.Judgment = isPass ? "OK" : "NG";
                item.CheckResult = isPass ? $"{random.Next(100, 500) / 100.0:F2} Ω" : "开路";

                if (!isPass) ngCount++;
            }

            TotalCount++;
            if (ngCount == 0)
            {
                PassCount++;
                TestStatus = "测试完成 - 良品";
            }
            else
            {
                FailCount++;
                TestStatus = "测试完成 - 不良";
            }

            AddLog($"✅ 模拟测试完成 (良品:{PassCount}, 不良:{FailCount})");
        }

        #endregion

        #region 中部 - 信息录入区

        [ObservableProperty]
        private string _modelName = string.Empty;

        [ObservableProperty]
        private string _serialNumber = string.Empty;

        [ObservableProperty]
        private string _operatorName = string.Empty;

        [ObservableProperty]
        private bool _isOperatorEditable = true;

        #endregion

        #region 下部 - 测试项目列表

        public ObservableCollection<TestItemModel> TestItems { get; } = new();

        /// <summary>
        /// 默认检测项目（无方案时的回退）
        /// </summary>
        private void LoadDefaultTestItems()
        {
            var items = new[] 
            {
                new { Name = "A1-A2", Condition = "OPEN" },
                new { Name = "A2-A3", Condition = "SHORT" },
                new { Name = "A3-A4", Condition = "OPEN" },
                new { Name = "A4-A5", Condition = "SHORT" },
                new { Name = "B1-B2", Condition = "OPEN" },
                new { Name = "B2-B3", Condition = "SHORT" },
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

        /// <summary>
        ///  从全局当前方案加载检测项目
        /// </summary>
        private void LoadCurrentPlanItems()
        {
            TestItems.Clear();

            var currentPlan = _currentPlanService.CurrentPlan;
            if (currentPlan == null || currentPlan.Items.Count == 0)
            {
                // 没有方案时使用默认项目
                SchemeName = "无方案 - 使用默认项目";
                AddLog("⚠️ 未选择方案，使用默认检测项目");
                LoadDefaultTestItems();
                return;
            }

            SchemeName = currentPlan.PlanName;
            ModelName = currentPlan.Model;
            AddLog($"📋 已加载方案: {_currentPlanService.CurrentPlanName}");

            foreach (var item in currentPlan.Items.OrderBy(i => i.Index))
            {
                TestItems.Add(new TestItemModel
                {
                    Index = item.Index,
                    ItemName = item.ItemName,
                    CheckCondition = item.CheckCondition,
                    CheckResult = string.Empty,
                    Judgment = string.Empty
                });
            }

            AddLog($"共加载 {TestItems.Count} 个检测项目");
        }

        #endregion

        #region 底部 - 日志与操作区

        public ObservableCollection<string> LogMessages { get; } = new();

        private void AddLog(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogMessages.Add($"[{timestamp}] {message}");

                while (LogMessages.Count > 500)
                {
                    LogMessages.RemoveAt(0);
                }
            });
        }

        [RelayCommand]
        private async Task FinishAndReturnAsync()
        {
            var confirmed = await _notificationService.ConfirmAsync(
                "确定要结束当前测试并返回主菜单吗？", "确认返回");

            if (confirmed)
            {
                _serilogLogger.Information("用户点击终了按钮，返回主菜单");
                await _navigationService.NavigateToAsync<Views.MainMenuView>();
            }
        }

        #endregion

        #region INavigationAware 实现

        public Task OnNavigatedToAsync(object? parameter = null)
        {
            _serilogLogger.Debug("进入测试页面");

            // 从全局状态服务读取当前作业员
            var operatorName = _operatorStateService?.CurrentOperatorName ?? "默认作业员";
            OperatorName = operatorName;
            IsOperatorEditable = false;

            AddLog($"当前作业员: {operatorName}");

            // 从全局方案服务加载检测项目
            LoadCurrentPlanItems();

            _serilogLogger.Information("测试页当前作业员: {Operator}, 当前方案: {Plan}",
                operatorName, _currentPlanService.CurrentPlanName);

            // 自动连接硬件
            _ = AutoConnectHardwareAsync();

            return Task.CompletedTask;
        }

        private async Task AutoConnectHardwareAsync()
        {
            AddLog("正在自动连接硬件设备...");

            await ReconnectPlcAsync();
            await ReconnectDmmAsync();
            await ReconnectScannerAsync();
        }

        public Task OnNavigatedFromAsync()
        {
            _serilogLogger.Debug("离开测试页面");
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            if (TestStatus == "测试中")
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _clockTimer?.Stop();
            _clockTimer = null;

            if (_scanner != null)
            {
                _scanner.BarcodeReceived -= OnScannerBarcodeReceived;
            }

            if (_inspectionEngine != null)
            {
                _inspectionEngine.StateChanged -= OnInspectionStateChanged;
                _inspectionEngine.StepCompleted -= OnStepCompleted;
                _inspectionEngine.InspectionCompleted -= OnInspectionCompleted;
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}