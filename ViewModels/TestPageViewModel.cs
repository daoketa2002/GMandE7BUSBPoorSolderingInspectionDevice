// ============================================================
// 文件: ViewModels/TestPageViewModel.cs
// 描述: 运行界面 ViewModel（修改部分）
// 改动说明:
//   - 移除 ScannerIntegrationHelper 字段
//   - 注入 IDeviceConnectionManager 替代手动连接硬件
//   - 移除 AutoConnectHardwareAsync() 方法
//   - 移除手动 ReconnectPlc/Scanner/Dmm 中的连接逻辑
//   - 统一订阅 DeviceConnectionManager 的状态事件
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
        // private readonly IScannerBarcodeService? _scannerService;
        private readonly IDeviceConnectionManager _deviceManager;
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
            IDeviceConnectionManager deviceManager,
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
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            //_scannerService = scannerBarcodeService;
            _testRecordStorage = testRecordStorage ?? throw new ArgumentNullException(nameof(testRecordStorage));
            _inspectionEngine = inspectionEngine;

            InitializeClock();
            SubscribeToHardwareEvents();

            // ⭐ 启动时同步设备连接状态
            SyncDeviceStates();
        }

        #endregion

        #region 顶部左侧 - 生产统计面板

        [ObservableProperty]
        private string _schemeName = string.Empty;

        /// <summary>
        /// 方案名称下拉选项列表（与机种联动）
        /// 机种为空时 → 空列表，用户只能手动输入
        /// 机种有值时 → 该机种下的所有方案名（从JSON文件读取）
        /// </summary>
        public ObservableCollection<string> PlanNameOptions { get; } = new();

        /// <summary>
        /// 当前在下拉列表中选中的方案名称
        /// 绑定 ComboBox.SelectedItem，用于区分"手动输入"和"下拉选择"
        /// 手动输入匹配成功时自动赋值，匹配失败或机种无效时赋值为null
        /// </summary>
        [ObservableProperty]
        private string? _selectedPlanName;

        /// <summary>
        /// 方案名称是否无效
        /// 当机种非空、方案名非空、但方案名不属于当前机种时为True
        /// 驱动 ComboBox 红色边框 + ToolTip 提示
        /// </summary>
        [ObservableProperty]
        private bool _isSchemeNameInvalid;

        /// <summary>
        /// 方案名称变更时触发（MVVM CommunityToolkit 自动生成）
        /// 用户手动输入时自动尝试匹配下拉列表项
        /// 匹配成功 → 自动选中该项，清除无效标记
        /// 匹配失败 → 取消选中，交由 ValidateCurrentSchemeName 判断是否无效
        /// </summary>
        partial void OnSchemeNameChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SelectedPlanName = null;
                IsSchemeNameInvalid = false;
                return;
            }

            // 尝试在方案下拉列表中精确匹配用户输入
            var matched = PlanNameOptions.FirstOrDefault(
                p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase));

            if (matched != null)
            {
                // 匹配成功 → 自动选中列表项，清除无效标记
                SelectedPlanName = matched;
                IsSchemeNameInvalid = false;
            }
            else
            {
                // 匹配失败 → 取消选中（保持手动输入文本）
                SelectedPlanName = null;
                // 无效标记由外部调用 ValidateCurrentSchemeName 统一判断
            }
        }

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
            await _deviceManager.ReconnectDeviceAsync("PLC");
            // 状态由事件回调自动更新，无需手动设置
        }

        /// <summary>
        /// 扫描仪重连命令
        /// ⭐ 增强用户反馈和日志
        /// </summary>
        [RelayCommand]
        private async Task ReconnectScannerAsync()
        {
            ScannerStatusText = "连接中...";
            IsScannerConnected = false;

            AddLog($"🔄 正在尝试重新连接扫描仪...");

            try
            {
                await _deviceManager.ReconnectDeviceAsync("Scanner");

                // 状态由事件回调自动更新，但增加日志确保用户看到结果
                if (IsScannerConnected)
                {
                    AddLog($"✅ 扫描仪重连成功");
                }
                else
                {
                    AddLog($"❌ 扫描仪重连失败 - 请检查：1.USB线是否插好 2.端口配置是否正确 3.设备管理器中COM口是否存在");
                    await _notificationService.ShowWarningAsync(
                        "扫描仪连接失败！\n\n请检查：\n1. USB线是否插好\n2. 设备管理器中COM口是否存在\n3. 系统设定中端口配置是否正确",
                        "连接失败");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫描仪重连异常");
                AddLog($"❌ 扫描仪重连异常: {ex.Message}");
                await _notificationService.ShowErrorAsync($"扫描仪重连失败：{ex.Message}", "错误");
            }
        }

        /// <summary>
        /// 万用表重连命令
        /// </summary>
        [RelayCommand]
        private async Task ReconnectDmmAsync()
        {
            DmmStatusText = "连接中...";
            await _deviceManager.ReconnectDeviceAsync("DMM");
            // 状态由事件回调自动更新
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

        /// <summary>
        /// 机种名称变更时触发（MVVM CommunityToolkit 自动生成）
        /// 联动刷新方案下拉列表 + 验证当前方案名有效性
        /// </summary>
        partial void OnModelNameChanged(string value)
        {
            // 机种名称变化 → 异步刷新方案下拉选项
            _ = RefreshPlanNameOptionsAsync(value);

            // 机种变更后验证当前方案名是否有效
            ValidateCurrentSchemeName();
        }

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
            // ❌ 删除原有的 PLC/DMM/Scanner 事件订阅代码

            // ⭐ 订阅全局设备连接管理器的状态变更事件
            _deviceManager.PlcConnectionStateChanged += (s, e) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsPlcConnected = e.IsConnected;
                    PlcStatusText = e.StatusText;
                    UpdateUIState();
                });
            };

            _deviceManager.DmmConnectionStateChanged += (s, e) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsDmmConnected = e.IsConnected;
                    DmmStatusText = e.StatusText;
                    UpdateUIState();
                });
            };

            _deviceManager.ScannerConnectionStateChanged += (s, e) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsScannerConnected = e.IsConnected;
                    ScannerStatusText = e.StatusText;
                    UpdateUIState();
                });
            };

            // ⭐ 订阅扫码事件（由 DeviceConnectionManager 统一转发）
            _deviceManager.BarcodeScanned += OnScannerBarcodeParsed;

            // InspectionEngine 事件保持不变
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
                // ⭐ 填充机种名称和序列号
                ModelName = e.ModelName;

                // ⭐ 扫码填充机种名称后，自动刷新方案下拉列表
                // 注意：OnModelNameChanged 已在属性setter中自动触发 RefreshPlanNameOptionsAsync
                // 此处额外调用验证，确保扫码后方案名有效性判断正确
                ValidateCurrentSchemeName();

                SerialNumber = e.SerialPart ?? string.Empty;
                AddLog($"📷 扫描到条码: 机种={e.ModelName}, 序列号={e.SerialPart}");
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

            // ⭐ 步骤1：先刷新方案下拉选项（根据当前机种名称，首次进入时机种为空→列表为空）
            await RefreshPlanNameOptionsAsync(ModelName);
            // ⭐ 步骤2：验证当前方案名有效性
            ValidateCurrentSchemeName();
            // ⭐ 步骤3：再加载方案项目到检测列表
            await LoadPlanItemsAsync();

            // ⭐ 重新订阅扫码事件（确保不重复订阅）
            _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;
            _deviceManager.BarcodeScanned += OnScannerBarcodeParsed;

            // ⭐ 同步设备连接状态（不再手动连接）
            SyncDeviceStates();

            // 启动传感器模拟（TODO: 替换为真实PLC轮询）
            StartSensorSimulation();
        }

        public Task OnNavigatedFromAsync()
        {
            _logger.LogInformation("离开运行界面");
            _sensorSimTimer?.Stop();

            // ⭐ 取消订阅扫码事件
            if (_deviceManager != null)
            {
                _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;
            }

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

        #region 方案名称联动逻辑（机种 ↔ 方案）
        /// <summary>
        /// 根据机种名称异步刷新方案下拉选项列表
        /// 参考 PlanSettingViewModel.RefreshPlanNameOptionsAsync 实现
        /// </summary>
        /// <param name="machineType">机种名称（空字符串表示清空列表）</param>
        private async Task RefreshPlanNameOptionsAsync(string machineType)
        {
            PlanNameOptions.Clear();

            // 机种为空 → 方案下拉空列表（用户只能手动输入）
            if (string.IsNullOrWhiteSpace(machineType))
            {
                _logger.LogDebug("机种为空，方案下拉选项已清空");
                return;
            }

            try
            {
                // 从文件系统加载该机种下的所有方案名
                var planNames = await _planStorageService.GetPlanNamesByMachineTypeAsync(machineType);

                foreach (var name in planNames)
                {
                    PlanNameOptions.Add(name);
                }

                _logger.LogDebug("方案下拉选项已刷新，机种={MachineType}，共 {Count} 个方案",
                    machineType, PlanNameOptions.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载机种 {MachineType} 的方案列表失败", machineType);
            }
        }

        /// <summary>
        /// 验证当前方案名称是否属于当前机种
        /// 更新 IsSchemeNameInvalid 属性，驱动UI红色边框提示
        /// 
        /// 规则：
        /// - 机种为空 → 始终有效（无需验证）
        /// - 方案名为空 → 有效（用户尚未输入，允许）
        /// - 方案名非空且机种非空 → 检查方案名是否在该机种的方案列表中
        /// </summary>
        private void ValidateCurrentSchemeName()
        {
            // 机种为空 → 无需验证
            if (string.IsNullOrWhiteSpace(ModelName))
            {
                IsSchemeNameInvalid = false;
                return;
            }

            // 方案名为空 → 用户尚未输入，允许
            if (string.IsNullOrWhiteSpace(SchemeName))
            {
                IsSchemeNameInvalid = false;
                return;
            }

            // 检查方案名是否在当前机种的方案列表中
            var isValid = PlanNameOptions.Any(
                p => string.Equals(p, SchemeName, StringComparison.OrdinalIgnoreCase));

            IsSchemeNameInvalid = !isValid;

            if (!isValid)
            {
                _logger.LogWarning("方案名无效: 机种={ModelName}, 方案名={SchemeName}", ModelName, SchemeName);
            }
        }

        #endregion

        #region 自动连接硬件

        // ⭐ 新增：同步设备状态方法
        private void SyncDeviceStates()
        {
            IsPlcConnected = _deviceManager.IsPlcConnected;
            PlcStatusText = _deviceManager.PlcStatusText;
            IsDmmConnected = _deviceManager.IsDmmConnected;
            DmmStatusText = _deviceManager.DmmStatusText;
            IsScannerConnected = _deviceManager.IsScannerConnected;
            ScannerStatusText = _deviceManager.ScannerStatusText;
            UpdateUIState();
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _clockTimer?.Stop();
            _clockTimer = null;
            _sensorSimTimer?.Stop();
            _sensorSimTimer = null;

            // ⭐ 取消订阅全局设备管理器事件
            if (_deviceManager != null)
            {
                _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;
                // 注意：连接状态事件不需要取消订阅，因为 DeviceConnectionManager 是全局单例
                // 但扫码事件需要取消，避免在页面销毁后仍然触发
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