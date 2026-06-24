// ============================================================
// 文件: ViewModels/PlanEditViewModel.cs
// 描述: 方案编辑/新增页面 ViewModel
// 改动:
//   - 去除"系列"字段，改为"机种"字段
//   - 使用 ScannerIntegrationHelper 替代内联扫描枪代码
//   - 新增 CreatedTime/LastModifiedTime 时间戳
// ============================================================

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Common;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    /// <summary>
    /// 方案编辑/新增页面 ViewModel
    /// 管理检测方案的创建和编辑，包含检测项目列表的增删改和排序
    /// 集成扫描枪：扫码自动填充机种名称（MachineType）
    /// </summary>
    public partial class PlanEditViewModel : ObservableObject, INavigationAware
    {
        #region 服务注入

        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IPlanStorageService _planStorageService;
        private readonly IDeviceConnectionManager _deviceManager;
        private readonly ILogger<PlanEditViewModel> _logger;

        #endregion

        #region 字段

        /// <summary>
        /// 是否为编辑模式（true=编辑已有方案，false=新增方案）
        /// </summary>
        private bool _isEditMode = false;

        /// <summary>
        /// 编辑模式下的原始方案（用于处理机种变更时的文件移动）
        /// </summary>
        private PlanModel? _originalPlan;

        #endregion

        #region 扫描枪相关属性（通过 ScannerIntegrationHelper 暴露给UI绑定）

        [ObservableProperty]
        private bool _isScannerConnected;

        [ObservableProperty]
        private string _scannerStatusText = "扫描枪未连接";

        [ObservableProperty]
        private string _scannedBarcode = string.Empty;

        [ObservableProperty]
        private string _scannerPortName = "COM9";

        #endregion

        #region 构造函数

        public PlanEditViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IPlanStorageService planStorageService,
            IDeviceConnectionManager deviceManager,
            ILogger<PlanEditViewModel> logger)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ⭐ 同步扫描枪初始状态
            IsScannerConnected = _deviceManager.IsScannerConnected;
            ScannerStatusText = _deviceManager.ScannerStatusText;
            ScannerPortName = _deviceManager.IsScannerConnected ? "COM8" : "COM8";  // 从配置读取或使用默认值

            // ⭐ 订阅全局设备管理器事件
            _deviceManager.ScannerConnectionStateChanged += OnScannerConnectionStateChanged;
            _deviceManager.BarcodeScanned += OnScannerBarcodeScanned;

            // 初始化下拉选项
            MachineTypeOptions = new ObservableCollection<string>(PlanStorageService.DefaultMachineTypes);
            PinOptions = new ObservableCollection<string>(PlanStorageService.PinList);
            CheckModeOptions = new ObservableCollection<string> { "Continuity", "Resistance" };
            ContinuityUnitOptions = new ObservableCollection<string> { "OPEN", "SHORT" };

            _logger.LogDebug("PlanEditViewModel 构造完成");
        }

        #endregion

        #region 基本信息属性

        /// <summary>
        /// 机种名称（如 T998248391）
        /// 对应方案保存的文件夹名
        /// </summary>
        [ObservableProperty]
        private string _machineType = string.Empty;

        /// <summary>
        /// 方案名称（如 "方案A"）
        /// 对应方案保存的文件名（不含扩展名）
        /// </summary>
        [ObservableProperty]
        private string _planName = string.Empty;

        /// <summary>
        /// 方案创建时间（只读展示）
        /// </summary>
        [ObservableProperty]
        private DateTime _createdTime = DateTime.Now;

        /// <summary>
        /// 方案最后修改时间（只读展示）
        /// </summary>
        [ObservableProperty]
        private DateTime _lastModifiedTime = DateTime.Now;

        /// <summary>
        /// 机种下拉选项列表
        /// </summary>
        public ObservableCollection<string> MachineTypeOptions { get; }

        /// <summary>
        /// 引脚下拉选项（A1~A20, B1~B20）
        /// </summary>
        public ObservableCollection<string> PinOptions { get; }

        /// <summary>
        /// 检测方式下拉选项
        /// Continuity — 导通检测（检查回路开路/短路）
        /// Resistance — 电阻值检测（检查阻值范围）
        /// </summary>
        public ObservableCollection<string> CheckModeOptions { get; }

        /// <summary>
        /// 导通模式下的期望结果选项
        /// OPEN — 期望开路（回路断开）
        /// SHORT — 期望短路（回路导通）
        /// </summary>
        public ObservableCollection<string> ContinuityUnitOptions { get; }

        #endregion

        #region 项目列表属性

        /// <summary>
        /// 检测项目集合（绑定到DataGrid）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<PlanItemViewModel> _items = new ObservableCollection<PlanItemViewModel>();

        /// <summary>
        /// 当前选中的检测项目行
        /// </summary>
        [ObservableProperty]
        private PlanItemViewModel? _selectedItem;

        /// <summary>
        /// 是否有选中的检测项目（用于控制上移/下移/删除按钮状态）
        /// </summary>
        [ObservableProperty]
        private bool _hasItemSelection;

        partial void OnSelectedItemChanged(PlanItemViewModel? value)
        {
            HasItemSelection = value != null;
        }

        #endregion

        #region 项目操作命令

        /// <summary>
        /// 新增一个检测项目（默认检查条件为 OPEN）
        /// </summary>
        [RelayCommand]
        private void AddItem()
        {
            var newItem = new PlanItemViewModel
            {
                Index = Items.Count + 1,
                PinLeft = string.Empty,
                PinRight = string.Empty,
                CheckMode = "Continuity",
                Unit = "OPEN"
            };
            Items.Add(newItem);
            ReindexItems();
            _logger.LogDebug("新增检测项目，当前共 {Count} 项", Items.Count);
        }

        /// <summary>
        /// 删除选中的检测项目
        /// </summary>
        [RelayCommand]
        private void DeleteItem()
        {
            if (SelectedItem == null) return;
            Items.Remove(SelectedItem);
            ReindexItems();
            _logger.LogDebug("删除检测项目，当前共 {Count} 项", Items.Count);
        }

        /// <summary>
        /// 将选中的检测项目上移一位
        /// </summary>
        [RelayCommand]
        private void MoveItemUp()
        {
            if (SelectedItem == null) return;
            var index = Items.IndexOf(SelectedItem);
            if (index > 0)
            {
                Items.Move(index, index - 1);
                ReindexItems();
            }
        }

        /// <summary>
        /// 将选中的检测项目下移一位
        /// </summary>
        [RelayCommand]
        private void MoveItemDown()
        {
            if (SelectedItem == null) return;
            var index = Items.IndexOf(SelectedItem);
            if (index < Items.Count - 1)
            {
                Items.Move(index, index + 1);
                ReindexItems();
            }
        }

        /// <summary>
        /// 重新编排检测项目的序号（从1开始连续）
        /// </summary>
        private void ReindexItems()
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Index = i + 1;
            }
        }

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
        /// 自动填充机种名称（MachineType）
        /// </summary>
        private void OnScannerBarcodeScanned(object? sender, BarcodeParsedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ScannedBarcode = e.RawBarcode;
                _logger.LogInformation("扫描枪收到条码: {Barcode}, 机种={Model}", e.RawBarcode, e.ModelName);

                // 自动填充机种名称（如果当前为空）
                if (string.IsNullOrWhiteSpace(MachineType))
                {
                    MachineType = e.ModelName;
                }
            });
        }

        #endregion

        #region 保存与返回命令

        /// <summary>
        /// 保存方案
        /// 校验必填项 → 构建PlanModel对象 → 调用存储服务保存 → 返回方案列表页
        /// </summary>
        [RelayCommand]
        private async Task SavePlanAsync()
        {
            try
            {
                // 校验必填项
                if (string.IsNullOrWhiteSpace(MachineType))
                {
                    await _notificationService.ShowWarningAsync("请输入/选择机种名称！", "校验失败");
                    return;
                }
                if (string.IsNullOrWhiteSpace(PlanName))
                {
                    await _notificationService.ShowWarningAsync("请输入方案名称！", "校验失败");
                    return;
                }
                if (Items.Count == 0)
                {
                    await _notificationService.ShowWarningAsync("请至少添加一个检测项目！", "校验失败");
                    return;
                }

                // ========== 校验检测项目引脚（逐个检查，精确定位错误项） ==========
                var pinErrors = new List<string>();

                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];
                    int displayIndex = item.Index;

                    // 校验左引脚
                    string? leftError = ValidateSinglePin(item.PinLeft, "左引脚");
                    if (leftError != null)
                        pinErrors.Add($"第{displayIndex}项 {leftError}");

                    // 校验右引脚
                    string? rightError = ValidateSinglePin(item.PinRight, "右引脚");
                    if (rightError != null)
                        pinErrors.Add($"第{displayIndex}项 {rightError}");
                }

                // 如果有任何引脚错误，一次性汇总提示
                if (pinErrors.Count > 0)
                {
                    string errorMessage = "以下检测项目引脚有问题，请修正后再保存：\n\n"
                                        + string.Join("\n", pinErrors);
                    await _notificationService.ShowWarningAsync(errorMessage, "引脚校验失败");
                    return;
                }

                // 构建方案对象
                var plan = new PlanModel
                {
                    MachineType = MachineType.Trim(),
                    PlanName = PlanName.Trim(),
                    CreatedTime = _isEditMode ? CreatedTime : DateTime.Now,  // 编辑模式保留原创建时间
                    LastModifiedTime = DateTime.Now,
                    Items = Items.Select(item => new PlanItem
                    {
                        Id = Guid.NewGuid().ToString(),  // 为新项目生成唯一标识
                        Index = item.Index,
                        ItemName = $"{item.PinLeft}-{item.PinRight}",
                        CheckMode = item.CheckMode,
                        LowerLimit = item.CheckMode == "Resistance" ? item.LowerLimit : null,
                        UpperLimit = item.CheckMode == "Resistance" ? item.UpperLimit : null,
                        Unit = item.GetEffectiveUnit()
                    }).ToList()
                };

                // 保存：传入原机种名以处理机种变更（移动文件）
                await _planStorageService.SavePlanAsync(plan, _originalPlan?.MachineType);

                _logger.LogInformation("方案保存成功: {MachineType}/{PlanName}", plan.MachineType, plan.PlanName);
                await _notificationService.ShowInfoAsync($"方案 \"{plan.PlanName}\" 保存成功！", "保存成功");

                // 返回方案设定页面
                await _navigationService.NavigateToAsync<PlanSettingView>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存方案失败");
                await _notificationService.ShowErrorAsync($"保存失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 返回方案设定页面
        /// 如有未保存修改，弹窗确认
        /// </summary>
        [RelayCommand]
        private async Task GoBackAsync()
        {
            try
            {
                if (HasUnsavedChanges())
                {
                    var result = await _notificationService.ConfirmAsync(
                        "当前方案有未保存的修改，确定要返回吗？\n未保存的修改将丢失！",
                        "确认返回");
                    if (!result) return;
                }
                _logger.LogInformation("用户返回方案设定页面");
                await _navigationService.NavigateToAsync<PlanSettingView>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "返回方案设定页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 校验单个引脚格式是否合法
        /// 合法格式：大写字母 A 或 B 开头，后接至少一位数字（如 A1、A20、B5、B15）
        /// </summary>
        /// <param name="pin">引脚字符串</param>
        /// <param name="side">引脚位置描述（左引脚/右引脚），用于错误提示</param>
        /// <returns>null 表示合法，否则返回具体错误描述</returns>
        private static string? ValidateSinglePin(string pin, string side)
        {
            if (string.IsNullOrWhiteSpace(pin))
                return $"{side}不能为空";

            // 正则：^[AB]\d+$  —— 必须以大写A或B开头，后接至少一位数字，不含其他字符
            if (!System.Text.RegularExpressions.Regex.IsMatch(pin.Trim(), @"^[AB]\d+$"))
                return $"{side} \"{pin.Trim()}\" 格式错误：必须以大写A或B开头+数字（如A1、B20）";

            return null; // 校验通过
        }

        /// <summary>
        /// 判断是否有未保存的修改
        /// </summary>
        private bool HasUnsavedChanges()
        {
            if (!string.IsNullOrWhiteSpace(MachineType)) return true;
            if (!string.IsNullOrWhiteSpace(PlanName)) return true;
            if (Items.Count > 0) return true;
            return false;
        }

        #endregion

        #region INavigationAware 实现

        /// <summary>
        /// 页面导航进入时触发
        /// 根据参数判断新增/编辑模式，加载已有方案数据
        /// 订阅并自动连接扫描枪
        /// </summary>
        public Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogDebug("进入方案编辑页面");

            // 判断新增/编辑模式
            if (parameter is PlanModel existingPlan)
            {
                // 编辑模式：加载已有方案数据
                _isEditMode = true;
                _originalPlan = existingPlan;

                MachineType = existingPlan.MachineType;
                PlanName = existingPlan.PlanName;
                CreatedTime = existingPlan.CreatedTime;
                LastModifiedTime = existingPlan.LastModifiedTime;

                // 加载检测项目（适配新版方案字段）
                Items.Clear();
                foreach (var item in existingPlan.Items)
                {
                    var parts = item.ItemName.Split('-');
                    Items.Add(new PlanItemViewModel
                    {
                        Index = item.Index,
                        PinLeft = parts.Length > 0 ? parts[0] : string.Empty,
                        PinRight = parts.Length > 1 ? parts[1] : string.Empty,
                        CheckMode = item.CheckMode,
                        LowerLimit = item.LowerLimit,
                        UpperLimit = item.UpperLimit,
                        Unit = item.Unit
                    });
                }

                _logger.LogInformation("编辑模式，加载方案: {MachineType}/{PlanName}",
                    existingPlan.MachineType, existingPlan.PlanName);
            }
            else
            {
                // 新增模式：清空所有字段
                _isEditMode = false;
                _originalPlan = null;

                MachineType = string.Empty;
                PlanName = string.Empty;
                CreatedTime = DateTime.Now;
                LastModifiedTime = DateTime.Now;
                Items.Clear();

                _logger.LogInformation("新增模式");
            }

            // ⭐ 重新订阅扫码事件（确保不重复订阅）
            _deviceManager.BarcodeScanned -= OnScannerBarcodeScanned;
            _deviceManager.BarcodeScanned += OnScannerBarcodeScanned;

            // ⭐ 同步扫描枪连接状态（不再手动连接）
            IsScannerConnected = _deviceManager.IsScannerConnected;
            ScannerStatusText = _deviceManager.ScannerStatusText;

            return Task.CompletedTask;
        }

        /// <summary>
        /// 页面导航离开时触发
        /// 必须取消订阅扫描枪事件，防止在其他页面扫码时误触发本页逻辑
        /// </summary>
        public Task OnNavigatedFromAsync()
        {
            _logger.LogDebug("离开方案编辑页面");

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
    }

    /// <summary>
    /// 检测项目 ViewModel（用于方案编辑 DataGrid 行绑定）
    /// 将 PlanItem 的 ItemName 拆分为左右引脚，便于独立编辑
    ///
    /// 改动说明（方案需求变动）：
    /// CheckCondition 字段删除，改为 CheckMode + LowerLimit + UpperLimit + Unit
    /// 通过 CheckMode 控制下限/上限/单位输入框的可用状态
    /// </summary>
    public partial class PlanItemViewModel : ObservableObject
    {
        /// <summary>序号（从1开始）</summary>
        [ObservableProperty]
        private int _index;

        /// <summary>左引脚（如 A1）</summary>
        [ObservableProperty]
        private string _pinLeft = string.Empty;

        /// <summary>右引脚（如 A2）</summary>
        [ObservableProperty]
        private string _pinRight = string.Empty;

        /// <summary>
        /// 检测方式
        /// "Continuity" — 导通检测
        /// "Resistance" — 电阻值检测
        /// </summary>
        [ObservableProperty]
        private string _checkMode = "Continuity";

        /// <summary>
        /// 电阻值下限（Ω），导通模式为 null
        /// </summary>
        [ObservableProperty]
        private double? _lowerLimit;

        /// <summary>
        /// 电阻值上限（Ω），导通模式为 null
        /// </summary>
        [ObservableProperty]
        private double? _upperLimit;

        /// <summary>
        /// 物理单位或期望结果
        /// 导通模式： "OPEN"（期望开路）或 "SHORT"（期望短路）
        /// 电阻模式：固定 "Ω"
        /// </summary>
        [ObservableProperty]
        private string _unit = "OPEN";

        /// <summary>
        /// 是否为电阻值检测模式
        /// 用于控制下限/上限输入框的 IsEnabled 状态
        /// </summary>
        public bool IsResistanceMode => CheckMode == "Resistance";

        /// <summary>
        /// 完整的检测项目名称（如 A1-A2）
        /// </summary>
        public string FullItemName => $"{PinLeft}-{PinRight}";

        /// <summary>
        /// 获取有效的单位文本
        /// 导通模式返回 Unit（OPEN/SHORT），电阻模式固定返回 "Ω"
        /// </summary>
        public string GetEffectiveUnit()
        {
            return CheckMode switch
            {
                "Resistance" => "Ω",
                _ => Unit // "OPEN" 或 "SHORT"
            };
        }

        /// <summary>
        /// 检查方式变更时联动
        /// 切换到电阻模式时自动设置 Unit="Ω"
        /// 切换到导通模式时清空上下限并恢复 Unit="OPEN"
        /// </summary>
        partial void OnCheckModeChanged(string value)
        {
            if (value == "Resistance")
            {
                Unit = "Ω";
            }
            else // Continuity
            {
                LowerLimit = null;
                UpperLimit = null;
                if (Unit != "OPEN" && Unit != "SHORT")
                {
                    Unit = "OPEN";
                }
            }
            // 通知 IsResistanceMode 已变更（手动触发 UI 刷新）
            OnPropertyChanged(nameof(IsResistanceMode));
        }
    }
}