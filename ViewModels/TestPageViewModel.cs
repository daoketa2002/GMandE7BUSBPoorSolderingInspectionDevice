// ============================================================
// 文件: ViewModels/TestPageViewModel.cs
// 描述: 运行界面 ViewModel —— 实现三态锁定机制、日志保存、
//      设备连接检查、传感器模拟
// 重构内容:
//   - 三态枚举: Ready / CanStart / Testing / PendingSave
//   - 移除开始测试按钮，改为PLC信号触发
//   - 终了按钮支持测试中终止
//   - 待保存态弹窗确认 → 事务保存 / 取消清空结果
// ============================================================

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 运行界面 UI 状态枚举
    /// Ready:       准备态 —— 输入控件可编辑，等待设备就绪
    /// CanStart:    可启用态 —— 所有设备就绪+基板到位，等待PLC启动信号
    /// Testing:     测试态 —— 检测进行中，所有输入锁定
    /// PendingSave: 待保存态 —— 全部Pin测完，等待操作员确认保存
    /// </summary>
    public enum TestUIState
    {
        Ready,
        CanStart,
        Testing,
        PendingSave
    }

    public partial class TestPageViewModel : ObservableObject, INavigationAware, IDisposable
    {
        #region 服务注入

        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly ILogger<TestPageViewModel> _logger;
        private readonly IPlanStorageService _planStorageService;
        private readonly IOperatorStateService _operatorStateService;
        private readonly IDeviceSettingsService _settingsService;
        private readonly ITestRecordStorage _testRecordStorage;

        // 硬件服务
        private readonly ITcpClientPLCMotionService _plcService;
        private readonly GwInstekGDM9060Driver _dmmDriver;
        private readonly IScannerBarcodeService? _scannerService;
        private readonly InspectionEngine? _inspectionEngine;

        #endregion

        #region 字段

        private DispatcherTimer? _clockTimer;
        private DispatcherTimer? _sensorSimTimer;  // TODO: 替换为真实PLC轮询

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
            IPlanStorageService planStorageService,
            IScannerBarcodeService scannerBarcodeService,
            ITestRecordStorage testRecordStorage,
            InspectionEngine? inspectionEngine = null)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _operatorStateService = operatorStateService ?? throw new ArgumentNullException(nameof(operatorStateService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _dmmDriver = dmmDriver ?? throw new ArgumentNullException(nameof(dmmDriver));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _scannerService = scannerBarcodeService;
            _testRecordStorage = testRecordStorage ?? throw new ArgumentNullException(nameof(testRecordStorage));
            _inspectionEngine = inspectionEngine;

            InitializeClock();
            SubscribeToHardwareEvents();
        }

        #endregion

        #region 顶部左侧 - 生产统计面板

        [ObservableProperty]
        private string _schemeName = "默认测试方案";

        /// <summary>检查总数（不清零，累计）</summary>
        [ObservableProperty]
        private int _totalCount = 0;

        /// <summary>良品数量（不清零，累计）</summary>
        [ObservableProperty]
        private int _passCount = 0;

        /// <summary>不良数量（不清零，累计）</summary>
        [ObservableProperty]
        private int _failCount = 0;

        /// <summary>
        /// 清零计数器命令（弹窗确认后清零）
        /// </summary>
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
                _logger.LogInformation("计数器已清零");
                await _notificationService.ShowInfoAsync("计数器已清零", "操作成功");
            }
        }

        #endregion

        #region 顶部中间 - 设备连接状态看板

        [ObservableProperty]
        private string _currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

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
        /// 初始化实时时钟（每秒更新）
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

        /// <summary>
        /// PLC 连接/重连命令
        /// </summary>
        [RelayCommand]
        private async Task ReconnectPlcAsync()
        {
            PlcStatusText = "连接中...";
            try
            {
                if (_plcService.IsConnected)
                    await _plcService.StopAsync();

                await _plcService.StartAsync();

                IsPlcConnected = _plcService.IsConnected;
                PlcStatusText = _plcService.IsConnected ? "已连接" : "断开";

                AddLog(_plcService.IsConnected ? "✅ PLC 连接成功" : "❌ PLC 连接失败");
                UpdateUIState();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PLC连接失败");
                IsPlcConnected = false;
                PlcStatusText = "连接失败";
                AddLog($"❌ PLC连接失败: {ex.Message}");
                UpdateUIState();
            }
        }

        /// <summary>
        /// 扫描仪重连命令
        /// </summary>
        [RelayCommand]
        private async Task ReconnectScannerAsync()
        {
            ScannerStatusText = "连接中...";
            try
            {
                if (_scannerService != null)
                {
                    _scannerService.Disconnect();
                    var settings = _settingsService.LoadSettings();
                    var portName = settings.ScannerSerialCommunication?.SerialNumber ?? "COM9";
                    var result = _scannerService.Connect(portName);

                    IsScannerConnected = result;
                    ScannerStatusText = result ? "已连接" : "断开";
                    AddLog(result ? $"✅ 扫描枪已连接 ({portName})" : $"❌ 扫描枪连接失败 ({portName})");
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
            UpdateUIState();
            await Task.CompletedTask;
        }

        /// <summary>
        /// 万用表重连命令
        /// </summary>
        [RelayCommand]
        private async Task ReconnectDmmAsync()
        {
            DmmStatusText = "连接中...";
            try
            {
                var settings = _settingsService.LoadSettings();
                var host = settings.TcpClientGWInstek?.Host ?? "192.168.1.4";
                var port = settings.TcpClientGWInstek?.Port ?? 5025;

                if (_dmmDriver.IsConnected)
                    await _dmmDriver.DisconnectAsync();

                var result = await _dmmDriver.ConnectAsync(host, port);

                IsDmmConnected = result;
                DmmStatusText = result ? "已连接" : "断开";
                AddLog(result ? $"✅ 万用表已连接 ({host}:{port})" : $"❌ 万用表连接失败 ({host}:{port})");
            }
            catch (Exception ex)
            {
                IsDmmConnected = false;
                DmmStatusText = "连接失败";
                AddLog($"❌ 万用表连接失败: {ex.Message}");
            }
            UpdateUIState();
        }

        #endregion

        #region 顶部右侧 - 测试状态显示

        /// <summary>物检传感器是否检测到基板安装到位</summary>
        [ObservableProperty]
        private bool _isSensorReady = false;

        /// <summary>测试状态显示文本（如"待机中"/"可启用"）</summary>
        [ObservableProperty]
        private string _sensorStatusText = "待机中";

        /// <summary>当前UI状态</summary>
        [ObservableProperty]
        private TestUIState _uiState = TestUIState.Ready;

        /// <summary>输入控件是否可用（准备态/可启用态=true，其他=false）</summary>
        [ObservableProperty]
        private bool _isInputEnabled = true;

        /// <summary>
        /// TODO: 模拟物检传感器信号
        /// 实际应从PLC读取（如通过Modbus读取X0端口状态）
        /// 当前使用定时器切换状态以验证UI逻辑
        /// </summary>
        private void StartSensorSimulation()
        {
            _sensorSimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _sensorSimTimer.Tick += (s, e) =>
            {
                // TODO: 替换为真实PLC读取代码
                // var coilState = await _plcService.ExecuteReadOperationAsync(0x01, 1, 0, 1, 1000);
                // IsSensorReady = coilState?.Data?[0] == 1;

                // 模拟：首次进入页面3秒后传感器检测到基板
                IsSensorReady = true;
                UpdateUIState();
                _sensorSimTimer?.Stop(); // 模拟只需一次
            };
            _sensorSimTimer.Start();
        }

        /// <summary>
        /// 根据所有设备连接状态和传感器状态更新UI状态
        /// 全部就绪 → CanStart（可启用）
        /// 任一不满足 → Ready（待机中）
        /// </summary>
        private void UpdateUIState()
        {
            // 测试中或待保存态不允许状态回退
            if (UiState == TestUIState.Testing || UiState == TestUIState.PendingSave)
                return;

            var allReady = IsPlcConnected && IsScannerConnected
                           && IsDmmConnected && IsSensorReady;

            UiState = allReady ? TestUIState.CanStart : TestUIState.Ready;
            SensorStatusText = IsSensorReady ? "可启用" : "待机中";
            IsInputEnabled = (UiState == TestUIState.Ready || UiState == TestUIState.CanStart);
        }

        /// <summary>
        /// 检查所有设备是否就绪，返回未就绪设备列表
        /// 用于按下启动按钮时的前置检查
        /// </summary>
        private List<string> GetNotReadyDevices()
        {
            var notReady = new List<string>();
            if (!IsSensorReady) notReady.Add("物检传感器未检测到基板");
            if (!IsPlcConnected) notReady.Add("PLC未连接");
            if (!IsScannerConnected) notReady.Add("扫描仪未连接");
            if (!IsDmmConnected) notReady.Add("万用表未连接");
            return notReady;
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

        /// <summary>测试项目集合（绑定DataGrid）</summary>
        public ObservableCollection<TestItemModel> TestItems { get; } = new();

        /// <summary>
        /// 从方案文件中加载默认检测项目
        /// </summary>
        private async Task LoadPlanItemsAsync()
        {
            TestItems.Clear();

            var allPlans = await _planStorageService.LoadAllPlansAsync();
            var currentPlan = allPlans.FirstOrDefault();

            if (currentPlan == null || currentPlan.Items.Count == 0)
            {
                SchemeName = "无方案 - 使用默认项目";
                AddLog("⚠️ 未找到任何方案，使用默认检测项目");
                LoadDefaultTestItems();
                return;
            }

            SchemeName = currentPlan.PlanName;
            AddLog($"📋 已加载方案: {currentPlan.MachineType} - {currentPlan.PlanName}");

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
        }

        /// <summary>
        /// 加载默认检测项目（无方案时的回退）
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

        #endregion

        #region 待保存态 —— 弹窗确认与事务保存

        /// <summary>
        /// 全部Pin检测完成后触发（由InspectionEngine的InspectionCompleted事件调用）
        /// 1. 计算FinalResult（全部OK→OK，否则NG）
        /// 2. 更新统计数据（不清零）
        /// 3. 弹窗展示NG项目明细
        /// 4. 确认→事务保存；取消→清空结果但保留Pin列表
        /// </summary>
        private async Task OnAllPinsTestedAsync()
        {
            UiState = TestUIState.PendingSave;

            // 计算综合判定
            var finalResult = TestItems.All(i => i.Judgment == "OK") ? "OK" : "NG";

            // 更新顶部统计数据（累计，不清零）
            TotalCount++;
            if (finalResult == "OK") PassCount++;
            else FailCount++;

            // 获取当前方案信息
            var allPlans = await _planStorageService.LoadAllPlansAsync();
            var currentPlan = allPlans.FirstOrDefault();
            var machineType = currentPlan?.MachineType ?? "Unknown";    // 该部分要修改，因方案model已经修改
            var planName = currentPlan?.PlanName ?? "Unknown";

            // 构建NG项目明细（供弹窗展示）
            var ngItems = TestItems.Where(i => i.Judgment == "NG").ToList();
            var ngDetail = ngItems.Any()
                ? string.Join("\n", ngItems.Select(i => $"  • {i.ItemName}: {i.CheckResult} → NG"))
                : "无";

            // 弹窗确认
            var confirmed = await _notificationService.ConfirmAsync(
                $"当前方案 [{planName}] 所有项目已检测完毕\n\n" +
                $"综合判定: [{finalResult}]\n\n" +
                $"NG项目:\n{ngDetail}\n\n" +
                $"是否保存本次检测记录？",
                "检测完成");

            if (confirmed)
            {
                // 事务保存到SQLite
                await SaveLogToDatabaseAsync(machineType, finalResult);             // 该代码要修改，因方案model已经修改
                await _notificationService.ShowInfoAsync("检测记录已保存！", "保存成功");
                ResetToReadyState();
            }
            else
            {
                // 取消：清空检测结果，保留Pin列表结构
                // 统计数据不清零（需求文档明确要求）
                foreach (var item in TestItems)
                {
                    item.CheckResult = string.Empty;
                    item.Judgment = string.Empty;
                }
                UiState = TestUIState.CanStart;
                AddLog("📝 操作员取消保存，检测结果已清空，可重新测试");
            }
        }

        /// <summary>
        /// 将本次检测结果保存到SQLite数据库（事务写入）
        /// </summary>
        private async Task SaveLogToDatabaseAsync(string series, string finalResult)
        {
            try
            {
                var allPlans = await _planStorageService.LoadAllPlansAsync();
                var currentPlan = allPlans.FirstOrDefault();

                var record = new LogRecord
                {
                    Timestamp = DateTime.Now,
                    Series = series,
                    SerialNumber = SerialNumber,
                    PlanName = currentPlan?.PlanName ?? "Unknown",
                    Operator = OperatorName,
                    FinalResult = finalResult,
                    PinResults = TestItems.Select(item => new PinResult
                    {
                        PinName = item.ItemName,
                        Result = item.Judgment == "OK" ? "OK" : $"{item.CheckResult} → NG"
                    }).ToList()
                };

                //await _logDatabaseService.SaveLogRecordAsync(record);
                await _testRecordStorage.SaveRecordAsync(record);
                AddLog($"💾 检测记录已保存 - SN:{SerialNumber}, 结果:{finalResult}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存检测记录失败");
                AddLog($"❌ 保存失败: {ex.Message}");
                await _notificationService.ShowErrorAsync($"保存失败：{ex.Message}", "错误");
            }
        }

        /// <summary>
        /// 回到准备态（保存成功后调用）
        /// 清空输入信息，保留统计数据
        /// </summary>
        private void ResetToReadyState()
        {
            SerialNumber = string.Empty;
            // 保留 ModelName 和 OperatorName（通常不变）
            foreach (var item in TestItems)
            {
                item.CheckResult = string.Empty;
                item.Judgment = string.Empty;
            }
            UiState = TestUIState.CanStart;
            AddLog("✅ 准备就绪，可进行下一次检测");
        }

        #endregion

        #region 终了按钮

        /// <summary>
        /// 终了按钮命令 —— 支持测试中终止
        /// 准备态/可启用态：直接返回主菜单
        /// 测试态：弹窗确认后终止测试并返回
        /// 待保存态：弹窗确认后丢弃数据并返回
        /// </summary>
        [RelayCommand]
        private async Task FinishAndReturnAsync()
        {
            string confirmMsg = UiState switch
            {
                TestUIState.Testing => "正在测试中，确定要终止当前测试并返回主菜单吗？\n未完成的测试数据将丢失！",
                TestUIState.PendingSave => "有未保存的检测结果，返回将丢失本次所有数据，确定继续吗？",
                _ => "确定要返回主菜单吗？"
            };

            var confirmed = await _notificationService.ConfirmAsync(confirmMsg, "确认返回");
            if (!confirmed) return;

            // 如果在测试中，发送PLC停止信号并中止检测引擎
            if (UiState == TestUIState.Testing)
            {
                _inspectionEngine?.Stop();
                AddLog("⚠️ 操作员终止了当前测试");
            }

            // 清理传感器模拟定时器
            _sensorSimTimer?.Stop();

            // 回到准备态
            ResetToReadyState();

            await _navigationService.NavigateToAsync<MainMenuView>();
        }

        #endregion

        #region 底部 - 日志区

        public ObservableCollection<string> LogMessages { get; } = new();

        private void AddLog(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogMessages.Add($"[{timestamp}] {message}");

                // 限制日志条数防止内存溢出
                while (LogMessages.Count > 500)
                    LogMessages.RemoveAt(0);
            });
        }

        #endregion

        #region 硬件事件订阅

        private void SubscribeToHardwareEvents()
        {
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
                    UpdateUIState();
                });
            };

            _dmmDriver.ConnectionStateChanged += (s, connected) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsDmmConnected = connected;
                    DmmStatusText = connected ? "已连接" : "断开";
                    UpdateUIState();
                });
            };

            if (_scannerService != null)
            {
                _scannerService.BarcodeParsed += OnScannerBarcodeParsed;
                _scannerService.ConnectionStateChanged += (s, connected) =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        IsScannerConnected = connected;
                        ScannerStatusText = connected ? "已连接" : "断开";
                        UpdateUIState();
                    });
                };
            }

            if (_inspectionEngine != null)
            {
                _inspectionEngine.StateChanged += OnInspectionStateChanged;
                _inspectionEngine.StepCompleted += OnStepCompleted;
                _inspectionEngine.InspectionCompleted += OnInspectionCompleted;
                _inspectionEngine.LogMessage += (s, msg) => AddLog(msg);
            }
        }

        private void OnScannerBarcodeParsed(object? sender, BarcodeParsedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 测试态禁止扫码
                if (UiState == TestUIState.Testing || UiState == TestUIState.PendingSave)
                {
                    AddLog("⚠️ 测试中禁止扫码，条码已忽略");
                    return;
                }
                SerialNumber = e.SerialPart ?? e.RawBarcode;
                AddLog($"📷 扫描到条码: {e.RawBarcode}");
            });
        }

        private void OnInspectionStateChanged(object? sender, InspectionStateChangedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                UiState = e.NewState switch
                {
                    InspectionState.Testing => TestUIState.Testing,
                    InspectionState.CompletedPass => TestUIState.PendingSave,
                    InspectionState.CompletedFail => TestUIState.PendingSave,
                    _ => UiState
                };
                IsInputEnabled = (UiState != TestUIState.Testing && UiState != TestUIState.PendingSave);
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
            Application.Current.Dispatcher.Invoke(async () =>
            {
                var msg = e.Result.IsAllPassed
                    ? $"✅ 检测完成: 良品 (耗时{e.Result.Duration.TotalSeconds:F1}s)"
                    : $"❌ 检测完成: 不良 (耗时{e.Result.Duration.TotalSeconds:F1}s)";
                AddLog(msg);

                // 进入待保存态逻辑
                await OnAllPinsTestedAsync();
            });
        }

        #endregion

        #region INavigationAware

        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogInformation("进入运行界面");

            var operatorName = _operatorStateService?.CurrentOperatorName ?? "默认作业员";
            OperatorName = operatorName;
            IsOperatorEditable = false;
            IsInputEnabled = true;
            UiState = TestUIState.Ready;

            AddLog($"当前作业员: {operatorName}");

            await LoadPlanItemsAsync();

            // 自动连接硬件
            _ = AutoConnectHardwareAsync();

            // 启动传感器模拟（TODO: 替换为真实PLC轮询）
            StartSensorSimulation();
        }

        public Task OnNavigatedFromAsync()
        {
            _logger.LogInformation("离开运行界面");
            _sensorSimTimer?.Stop();
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            // 测试中不允许直接返回（必须通过终了按钮）
            if (UiState == TestUIState.Testing)
                return Task.FromResult(false);
            return Task.FromResult(true);
        }

        #endregion

        #region 自动连接硬件

        private async Task AutoConnectHardwareAsync()
        {
            AddLog("正在自动连接硬件设备...");
            await ReconnectPlcAsync();
            await ReconnectDmmAsync();
            await ReconnectScannerAsync();
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _clockTimer?.Stop();
            _clockTimer = null;
            _sensorSimTimer?.Stop();
            _sensorSimTimer = null;

            if (_scannerService != null)
                _scannerService.BarcodeParsed -= OnScannerBarcodeParsed;

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