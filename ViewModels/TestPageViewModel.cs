// ============================================================
// 文件: ViewModels/TestPageViewModel.cs
// 描述: 运行界面 ViewModel
// 改动: 
//   1. 新增 OnStepStarted 事件处理 —— 标记当前检测项目为"测试中"
//   2. LoadPlanItemsAsync 初始化 CheckResult = "未测试"
//   3. ResetToReadyState 恢复 CheckResult = "未测试"
//   4. 事件订阅/取消订阅生命周期管理
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
            _testRecordStorage = testRecordStorage ?? throw new ArgumentNullException(nameof(testRecordStorage));
            _inspectionEngine = inspectionEngine;

            InitializeClock();
            SubscribeToHardwareEvents();

            // ⭐ 启动时同步设备连接状态
            SyncDeviceStates();
        }

        #endregion

        #region 顶部左侧 - 生产统计面板

        /// <summary>
        /// 当前方案名称（绑定ComboBox.Text，支持手动输入）
        /// 变更时自动尝试匹配下拉列表项，匹配成功则加载检测项目
        /// </summary>
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
        /// 驱动 ComboBox 红色边框 + ToolTip 提示"当前方案无效，请重新选择"
        /// </summary>
        [ObservableProperty]
        private bool _isSchemeNameInvalid;

        /// <summary>
        /// 方案名称变更时触发（MVVM CommunityToolkit 自动生成）
        /// 用户手动输入或下拉选择时：
        /// 1. 清空 → 清空检测项目列表 + 界面日志
        /// 2. 匹配成功 → 自动选中 + 清除无效标记 + 加载方案检测项目
        /// 3. 匹配失败 → 取消选中 + 界面日志提示
        /// </summary>
        partial void OnSchemeNameChanged(string value)
        {
            // 场景1：用户清空了方案名称
            if (string.IsNullOrWhiteSpace(value))
            {
                SelectedPlanName = null;
                IsSchemeNameInvalid = false;
                TestItems.Clear();
                AddLog("方案名称已清空，检测项目列表已清空");
                return;
            }

            // 场景2&3：尝试在方案下拉列表中精确匹配（忽略大小写）
            string? matched = PlanNameOptions.FirstOrDefault(
                planName => string.Equals(planName, value, StringComparison.OrdinalIgnoreCase));

            if (matched != null)
            {
                // 场景2：匹配成功 → 自动选中列表项 + 清除无效标记 + 加载检测项目
                SelectedPlanName = matched;
                IsSchemeNameInvalid = false;
                _ = LoadPlanItemsAsync();
            }
            else
            {
                // 场景3：匹配失败 → 取消选中 + 验证有效性(驱动红色边框+ToolTip) + 清空检测列表 + 日志
                SelectedPlanName = null;

                // 调用验证方法设置 IsSchemeNameInvalid = true，驱动红色边框和ToolTip
                ValidateCurrentSchemeName();

                // 无效方案清空检测项目列表，避免展示旧数据误导操作员
                TestItems.Clear();

                AddLog($"⚠️ 方案 [{value}] 不在当前机种 [{ModelName}] 的方案列表中，检测列表已清空");
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
                IsSensorReady = true;
                UpdateUIState();
                _sensorSimTimer?.Stop();
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

        /// <summary>
        /// 机种名称
        /// 变更时联动刷新方案下拉列表 + 验证当前方案名有效性
        /// 若方案名变为无效，清空检测项目列表
        /// </summary>
        [ObservableProperty]
        private string _modelName = string.Empty;

        [ObservableProperty]
        private string _serialNumber = string.Empty;

        [ObservableProperty]
        private string _operatorName = string.Empty;

        [ObservableProperty]
        private bool _isOperatorEditable = true;

        /// <summary>
        /// 机种名称变更时触发（MVVM CommunityToolkit 自动生成）
        /// 委托给异步处理方法，确保方案列表刷新完成后再验证
        /// </summary>
        partial void OnModelNameChanged(string value)
        {
            // 机种名称变化 → 异步处理：刷新方案列表 → 验证方案有效性 → 必要时清空检测列表
            _ = HandleModelNameChangedAsync(value);
        }

        /// <summary>
        /// 机种名称变更的异步处理（由 OnModelNameChanged 委托调用）
        /// 执行顺序：
        /// 1. 刷新方案下拉列表（等待异步IO完成）
        /// 2. 用新列表验证当前方案名是否有效
        /// 3. 若无效 → 清空检测项目列表 + 输出界面日志提示操作员
        /// 
        /// 设计说明：
        /// - 必须 await 异步刷新，否则验证时列表还是旧的，导致误判
        /// - 验证和清空逻辑抽离为独立方法，保持单一职责
        /// </summary>
        /// <param name="newMachineType">新的机种名称</param>
        private async Task HandleModelNameChangedAsync(string newMachineType)
        {
            // 步骤1：等待方案下拉列表刷新完成（关键：必须等待，避免时序问题）
            await RefreshPlanNameOptionsAsync(newMachineType);

            // 步骤2：用刷新后的方案列表验证当前方案名
            ValidateCurrentSchemeName();

            // 步骤3：方案名无效 → 清空检测项目列表并提示操作员
            if (IsSchemeNameInvalid)
            {
                TestItems.Clear();
                AddLog($"⚠️ 机种已切换为 [{newMachineType}]，方案 [{SchemeName}] 不属于该机种，检测列表已清空，请重新选择方案");
            }
        }

        #endregion

        #region 下部 - 测试项目列表

        /// <summary>测试项目集合（绑定DataGrid）</summary>
        public ObservableCollection<TestItemModel> TestItems { get; } = new();

        /// <summary>
        /// 从方案文件中加载检测项目
        /// 根据当前机种名称和方案名称精确匹配方案，无匹配时清空检测列表
        /// ★ CheckMode 存储值已是中文，直接赋值即可
        /// 
        /// 状态初始化：
        ///   每个检测项目 CheckResult 初始化为 "未测试"
        ///   后续由 InspectionEngine 事件驱动状态变更：
        ///     StepStarted  → "测试中"
        ///     StepCompleted → 实际测量值 + OK/NG
        /// </summary>
        private async Task LoadPlanItemsAsync()
        {
            TestItems.Clear();

            // 机种名称或方案名称为空 → 清空检测列表
            if (string.IsNullOrWhiteSpace(ModelName) || string.IsNullOrWhiteSpace(SchemeName))
            {
                AddLog("⚠️ 未指定机种或方案，检测列表为空");
                return;
            }

            var allPlans = await _planStorageService.LoadAllPlansAsync();

            // 精确匹配机种和方案名
            var currentPlan = allPlans.FirstOrDefault(p =>
                string.Equals(p.MachineType, ModelName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.PlanName, SchemeName, StringComparison.OrdinalIgnoreCase));

            if (currentPlan == null)
            {
                AddLog($"⚠️ 未找到匹配方案: 机种={ModelName}, 方案={SchemeName}，检测列表为空");
                return;
            }

            if (currentPlan.Items.Count == 0)
            {
                AddLog($"⚠️ 方案 [{currentPlan.PlanName}] 无检测项目，检测列表为空");
                return;
            }

            AddLog($"📋 已加载方案: {currentPlan.MachineType} - {currentPlan.PlanName}");

            foreach (var item in currentPlan.Items.OrderBy(i => i.Index))
            {
                // ★ CheckMode 已是中文 "导通"/"电阻值"，无需转换
                TestItems.Add(new TestItemModel
                {
                    Index = item.Index,
                    ItemName = item.ItemName,
                    CheckMode = item.CheckMode,  // ★ 直接赋值
                    LowerLimitText = FormatLowerLimit(item),
                    UpperLimitText = FormatUpperLimit(item),
                    CheckResult = "未测试",       // ⭐ 初始化状态：检测未开始
                    Judgment = string.Empty
                });
            }
        }


        /// <summary>
        /// 格式化下限显示文本
        /// 导通模式：显示期望状态（如 "OPEN(开路)"）
        /// 电阻模式：显示数值下限
        /// ★ 比较使用 CheckModeConstants 常量
        /// </summary>
        private static string FormatLowerLimit(PlanItem item)
        {
            if (item.CheckMode == CheckModeConstants.Resistance)
                return item.LowerLimit?.ToString("F1") ?? "-";

            // 导通模式：显示期望结果（ModeValue 替代旧 Unit 字段）
            return item.ModeValue switch
            {
                "SHORT" => "SHORT(短路)",
                _ => "OPEN(开路)"
            };
        }

        /// <summary>
        /// 格式化上限显示文本
        /// 导通模式：显示 "-"（无对应参数）
        /// 电阻模式：显示数值上限
        /// ★ 比较使用 CheckModeConstants 常量
        /// </summary>
        private static string FormatUpperLimit(PlanItem item)
        {
            if (item.CheckMode == CheckModeConstants.Resistance)
                return item.UpperLimit?.ToString("F1") ?? "-";

            // 导通模式：上限无意义，用 "-" 占位
            return "-";
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

            var finalResult = TestItems.All(i => i.Judgment == "OK") ? "OK" : "NG";

            TotalCount++;
            if (finalResult == "OK") PassCount++;
            else FailCount++;

            var allPlans = await _planStorageService.LoadAllPlansAsync();
            var currentPlan = allPlans.FirstOrDefault();
            var machineType = currentPlan?.MachineType ?? "Unknown";
            var planName = currentPlan?.PlanName ?? "Unknown";

            var ngItems = TestItems.Where(i => i.Judgment == "NG").ToList();
            var ngDetail = ngItems.Any()
                ? string.Join("\n", ngItems.Select(i => $"  • {i.ItemName}: {i.CheckResult} → NG"))
                : "无";

            var confirmed = await _notificationService.ConfirmAsync(
                $"当前方案 [{planName}] 所有项目已检测完毕\n\n" +
                $"综合判定: [{finalResult}]\n\n" +
                $"NG项目:\n{ngDetail}\n\n" +
                $"是否保存本次检测记录？",
                "检测完成");

            if (confirmed)
            {
                await SaveLogToDatabaseAsync(machineType, finalResult);
                await _notificationService.ShowInfoAsync("检测记录已保存！", "保存成功");
                ResetToReadyState();
            }
            else
            {
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
        /// 将本次检测结果保存到 CSV 文件（实际测量值写入动态列）
        ///
        /// 改动说明（方案需求变动）：
        /// PinResult.Result 存储实际测量值（如 "2.5"、"开路"）而非判定文本
        /// 综合判定保留在 FinalResult 列
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
                    MachineType = currentPlan?.MachineType ?? ModelName,
                    SerialNumber = SerialNumber,
                    PlanName = currentPlan?.PlanName ?? SchemeName,
                    Operator = OperatorName,
                    FinalResult = finalResult,
                    PinResults = TestItems.Select(item => new PinResult
                    {
                        PinName = item.ItemName,
                        // ★ 写入实际测量值（如 "2.5"、"开路"）而非判定文本
                        Result = item.CheckResult
                    }).ToList()
                };

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
        /// 回到准备态（保存成功后或操作员取消保存后调用）
        /// 清空输入信息，保留统计数据，恢复检测项目列表为初始状态
        /// 
        /// 状态恢复：
        ///   每个检测项目 CheckResult → "未测试"
        ///   Judgment → 清空
        ///   注意：不重置 CheckMode/LowerLimitText/UpperLimitText，这些来自方案配置
        /// </summary>
        private void ResetToReadyState()
        {
            SerialNumber = string.Empty;
            foreach (var item in TestItems)
            {
                // ⭐ 恢复为初始状态："未测试"
                // 不重置 CheckMode/LowerLimitText/UpperLimitText，这些来自方案配置
                item.CheckResult = "未测试";
                item.Judgment = string.Empty;
            }
            UiState = TestUIState.CanStart;
            AddLog("✅ 准备就绪，可进行下一次检测");
        }

        #endregion

        #region 终了按钮

        /// <summary>
        /// 终了按钮命令 —— 支持测试中终止
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

            if (UiState == TestUIState.Testing)
            {
                _inspectionEngine?.Stop();
                AddLog("⚠️ 操作员终止了当前测试");
            }

            _sensorSimTimer?.Stop();
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

                while (LogMessages.Count > 500)
                    LogMessages.RemoveAt(0);
            });
        }

        #endregion

        #region 硬件事件订阅

        private void SubscribeToHardwareEvents()
        {
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

            _deviceManager.BarcodeScanned += OnScannerBarcodeParsed;

            if (_inspectionEngine != null)
            {
                // ═══════════════════════════════════════════════════════
                // 订阅 InspectionEngine 的全部事件
                // 事件时序：StateChanged → StepStarted → StepCompleted → ... → InspectionCompleted
                // ═══════════════════════════════════════════════════════
                _inspectionEngine.StateChanged += OnInspectionStateChanged;
                _inspectionEngine.StepStarted += OnStepStarted;        // ⭐ 新增：检测步骤开始
                _inspectionEngine.StepCompleted += OnStepCompleted;
                _inspectionEngine.InspectionCompleted += OnInspectionCompleted;
                _inspectionEngine.LogMessage += (s, msg) => AddLog(msg);
            }
        }

        /// <summary>
        /// 扫码枪条码解析回调
        /// 自动填充机种名称和序列号，触发方案联动
        /// </summary>
        private void OnScannerBarcodeParsed(object? sender, BarcodeParsedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (UiState == TestUIState.Testing || UiState == TestUIState.PendingSave)
                {
                    AddLog("⚠️ 测试中禁止扫码，条码已忽略");
                    return;
                }

                // ⭐ 填充机种名称（OnModelNameChanged会自动触发方案下拉刷新+验证）
                ModelName = e.ModelName;
                SerialNumber = e.SerialPart ?? string.Empty;

                // ⭐ 扫码后验证方案名有效性（OnModelNameChanged已调用Validate，此处冗余但安全）
                ValidateCurrentSchemeName();

                AddLog($"📷 扫描到条码: 机种={e.ModelName}, 序列号={e.SerialPart}");
            });
        }

        /// <summary>
        /// 检测流程状态变更回调 —— 由 InspectionEngine.StateChanged 事件触发
        /// 将引擎内部状态映射为 UI 状态，驱动界面锁定/解锁
        /// </summary>
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

        /// <summary>
        /// 检测步骤开始回调 —— 由 InspectionEngine.StepStarted 事件触发
        /// 将当前检测项 CheckResult 标记为"测试中"，让操作员看到检测进度
        /// 
        /// 状态流转：
        ///   未测试 → 测试中（本方法）→ 实际测量值+OK/NG（OnStepCompleted）
        /// 
        /// 执行线程：InspectionEngine 工作线程 → 通过 Dispatcher 调度到 UI 线程
        /// </summary>
        /// <param name="sender">事件源（InspectionEngine 实例）</param>
        /// <param name="e">事件参数，包含 StepIndex 和 TestPoint</param>
        private void OnStepStarted(object? sender, StepStartedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 根据 StepIndex 从 TestItems 集合中获取对应的行
                // StepIndex 为 0-based，与 TestItems 索引一一对应
                var item = TestItems.ElementAtOrDefault(e.StepIndex);
                if (item != null)
                {
                    // 标记为"测试中" —— UI DataGrid 会实时反映此变化
                    item.CheckResult = "测试中";
                    // 清除上一轮的判定残留（确保不会显示旧结果）
                    item.Judgment = string.Empty;

                    // 输出日志到界面终端，方便操作员查看检测进度
                    AddLog($"🔍 [{e.StepIndex + 1}/{TestItems.Count}] {e.TestPoint.Name} 检测中...");
                }
                else
                {
                    // 防御性编程：索引越界时记录警告（正常流程不应出现）
                    _logger.LogWarning("StepStarted 事件中的 StepIndex={StepIndex} 超出 TestItems 范围（Count={Count}）",
                        e.StepIndex, TestItems.Count);
                }
            });
        }

        /// <summary>
        /// 检测步骤完成回调 —— 由 InspectionEngine.StepCompleted 事件触发
        /// 与 OnStepStarted 配合使用，形成完整的状态流转：
        ///   未测试 → 测试中（OnStepStarted）→ 实际测量值+OK/NG（本方法）
        /// 将万用表实际测量值写入 CheckResult，将判定结果写入 Judgment
        /// </summary>
        private void OnStepCompleted(object? sender, StepCompletedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var item = TestItems.ElementAtOrDefault(e.StepIndex);
                if (item != null)
                {
                    item.CheckResult = FormatMeasurementResult(e.Measurement, e.TestPoint);
                    item.Judgment = e.TestPoint.Judgment;
                }
            });
        }

        /// <summary>
        /// 格式化测量结果显示文本
        /// 导通模式：值极大 → "开路"，值极小 → "短路"，否则显示数值
        /// 电阻值模式：显示具体阻值
        /// ★ 比较使用 CheckModeConstants 常量
        /// </summary>
        private static string FormatMeasurementResult(MeasurementResult measurement, TestPointConfig testPoint)
        {
            if (!measurement.IsValid)
                return "测量失败";

            double value = measurement.Value;

            // ★ 使用中文常量比较
            if (testPoint.CheckMode == CheckModeConstants.Continuity)
            {
                // 导通模式：根据测量值判断物理状态
                if (value > 1_000_000.0)     // > 1MΩ → 开路
                    return "开路";
                if (value < 1.0)             // < 1Ω → 短路
                    return "短路";
                return $"{value:F4} Ω";      // 中间值显示数值
            }

            // 电阻值模式：显示实测阻值
            return $"{value:F4} Ω";
        }

        /// <summary>
        /// 全部检测完成回调 —— 由 InspectionEngine.InspectionCompleted 事件触发
        /// 汇总结果、更新统计、弹窗确认保存
        /// </summary>
        private void OnInspectionCompleted(object? sender, InspectionCompletedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(async () =>
            {
                var msg = e.Result.IsAllPassed
                    ? $"✅ 检测完成: 良品 (耗时{e.Result.Duration.TotalSeconds:F1}s)"
                    : $"❌ 检测完成: 不良 (耗时{e.Result.Duration.TotalSeconds:F1}s)";
                AddLog(msg);

                await OnAllPinsTestedAsync();
            });
        }

        #endregion

        #region INavigationAware

        /// <summary>
        /// 页面导航进入时触发
        /// 初始化方案下拉列表、验证方案有效性、加载检测项目
        /// </summary>
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
            // ⭐ 步骤3：加载方案项目到检测列表（无匹配时列表为空）
            await LoadPlanItemsAsync();

            // ⭐ 重新订阅扫码事件（确保不重复订阅）
            _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;
            _deviceManager.BarcodeScanned += OnScannerBarcodeParsed;

            // ⭐ 同步设备连接状态
            SyncDeviceStates();

            // 启动传感器模拟
            StartSensorSimulation();
        }

        public Task OnNavigatedFromAsync()
        {
            _logger.LogInformation("离开运行界面");
            _sensorSimTimer?.Stop();

            if (_deviceManager != null)
            {
                _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;
            }

            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            if (UiState == TestUIState.Testing)
                return Task.FromResult(false);
            return Task.FromResult(true);
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════
        // 方案名称联动逻辑（机种 ↔ 方案）
        // ═══════════════════════════════════════════════════════════════

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
        /// 更新 IsSchemeNameInvalid 属性（驱动UI红色边框提示）
        /// 无效时同步输出界面日志（方便操作员在日志区看到警告原因）
        /// 
        /// 验证规则：
        /// - 机种为空 → 无需验证，始终有效
        /// - 方案名为空 → 用户尚未输入，允许通过
        /// - 两者均非空 → 检查方案名是否在当前机种的方案列表中
        /// </summary>
        private void ValidateCurrentSchemeName()
        {
            // 规则1：机种为空 → 无需验证
            if (string.IsNullOrWhiteSpace(ModelName))
            {
                IsSchemeNameInvalid = false;
                return;
            }

            // 规则2：方案名为空 → 用户尚未输入，允许通过
            if (string.IsNullOrWhiteSpace(SchemeName))
            {
                IsSchemeNameInvalid = false;
                return;
            }

            // 规则3：检查方案名是否存在于当前机种的方案列表中（忽略大小写）
            bool isValid = PlanNameOptions.Any(
                planName => string.Equals(planName, SchemeName, StringComparison.OrdinalIgnoreCase));

            // 更新UI绑定属性（驱动ComboBox红色边框 + ToolTip）
            IsSchemeNameInvalid = !isValid;

            // 无效时输出日志：系统日志 + 界面日志（双通道，方便排查和提示操作员）
            if (!isValid)
            {
                string warningMessage = $"⚠️ 方案无效: 机种=[{ModelName}]，方案=[{SchemeName}]，该方案不属于当前机种，请重新选择方案";
                AddLog(warningMessage);
                _logger.LogWarning("方案名无效: 机种={ModelName}, 方案名={SchemeName}", ModelName, SchemeName);
            }
        }

        #region 自动连接硬件

        /// <summary>
        /// 同步设备连接状态（从DeviceConnectionManager读取当前状态）
        /// </summary>
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

        /// <summary>
        /// 释放所有资源，取消所有事件订阅
        /// 确保不产生内存泄漏
        /// </summary>
        public void Dispose()
        {
            _clockTimer?.Stop();
            _clockTimer = null;
            _sensorSimTimer?.Stop();
            _sensorSimTimer = null;

            if (_deviceManager != null)
            {
                _deviceManager.BarcodeScanned -= OnScannerBarcodeParsed;
            }

            // ⭐ 取消订阅 InspectionEngine 的所有事件，防止内存泄漏
            if (_inspectionEngine != null)
            {
                _inspectionEngine.StateChanged -= OnInspectionStateChanged;
                _inspectionEngine.StepStarted -= OnStepStarted;      // ⭐ 新增
                _inspectionEngine.StepCompleted -= OnStepCompleted;
                _inspectionEngine.InspectionCompleted -= OnInspectionCompleted;
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}