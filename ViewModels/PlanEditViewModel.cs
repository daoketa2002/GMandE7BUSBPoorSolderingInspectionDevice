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
        private readonly ILogger<PlanEditViewModel> _logger;

        /// <summary>
        /// 扫描枪集成帮助类（封装扫描枪事件，降低ViewModel与硬件的耦合）
        /// </summary>
        private readonly ScannerIntegrationHelper _scannerHelper;

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
            IScannerBarcodeService? scannerService,
            ILogger<PlanEditViewModel> logger)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // 初始化扫描枪帮助类
            _scannerHelper = new ScannerIntegrationHelper(scannerService, _logger);

            // 同步扫描枪初始状态
            IsScannerConnected = _scannerHelper.IsScannerConnected;
            ScannerStatusText = _scannerHelper.ScannerStatusText;
            ScannerPortName = _scannerHelper.PortName;

            // 订阅扫描枪事件
            _scannerHelper.ScannerConnected += OnScannerConnected;
            _scannerHelper.ScannerDisconnected += OnScannerDisconnected;
            _scannerHelper.BarcodeScanned += OnScannerBarcodeScanned;

            // 初始化下拉选项
            MachineTypeOptions = new ObservableCollection<string>(PlanStorageService.DefaultMachineTypes);
            PinOptions = new ObservableCollection<string>(PlanStorageService.PinList);
            CheckConditionOptions = new ObservableCollection<string> { "OPEN", "SHORT" };

            _logger.LogDebug("PlanEditViewModel 构造完成，扫描枪: {HasScanner}", scannerService != null);
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
        /// 检查条件下拉选项（OPEN / SHORT）
        /// </summary>
        public ObservableCollection<string> CheckConditionOptions { get; }

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
                CheckCondition = "OPEN"
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

        /// <summary>
        /// 扫描枪连接成功回调
        /// </summary>
        private void OnScannerConnected()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsScannerConnected = true;
                ScannerStatusText = _scannerHelper.ScannerStatusText;
                _logger.LogInformation("扫描枪已连接: {Port}", ScannerPortName);
            });
        }

        /// <summary>
        /// 扫描枪断开连接回调
        /// </summary>
        private void OnScannerDisconnected()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsScannerConnected = false;
                ScannerStatusText = _scannerHelper.ScannerStatusText;
                _logger.LogInformation("扫描枪已断开");
            });
        }

        /// <summary>
        /// 扫描枪条码接收回调
        /// 自动填充机种名称（MachineType）
        /// </summary>
        private void OnScannerBarcodeScanned(BarcodeParsedEventArgs e)
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

        /// <summary>
        /// 手动连接扫描枪
        /// </summary>
        [RelayCommand]
        private void ConnectScanner()
        {
            try
            {
                if (_scannerHelper == null)
                {
                    _logger.LogWarning("扫描枪帮助类未初始化");
                    _ = _notificationService.ShowWarningAsync("扫描枪服务未初始化", "提示");
                    return;
                }

                var connected = _scannerHelper.Connect(ScannerPortName);
                if (connected)
                {
                    IsScannerConnected = true;
                    ScannerStatusText = _scannerHelper.ScannerStatusText;
                    _ = _notificationService.ShowInfoAsync($"扫描枪连接成功: {ScannerPortName}", "连接成功");
                }
                else
                {
                    _ = _notificationService.ShowWarningAsync(
                        $"扫描枪连接失败，请检查:\n1. 端口 {ScannerPortName} 是否存在\n2. 扫描枪是否已切换为USB串口模式",
                        "连接失败");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "连接扫描枪失败");
                _ = _notificationService.ShowErrorAsync($"扫描枪连接失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 断开扫描枪连接
        /// </summary>
        [RelayCommand]
        private void DisconnectScanner()
        {
            try
            {
                _scannerHelper?.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "断开扫描枪失败");
            }
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

                // 校验每个检测项目的引脚是否完整
                foreach (var item in Items)
                {
                    if (string.IsNullOrWhiteSpace(item.PinLeft) || string.IsNullOrWhiteSpace(item.PinRight))
                    {
                        await _notificationService.ShowWarningAsync(
                            $"项目 {item.Index} 的引脚不能为空，请完善后再保存！", "校验失败");
                        return;
                    }
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
                        CheckCondition = item.CheckCondition
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

                // 加载检测项目
                Items.Clear();
                foreach (var item in existingPlan.Items)
                {
                    var parts = item.ItemName.Split('-');
                    Items.Add(new PlanItemViewModel
                    {
                        Index = item.Index,
                        PinLeft = parts.Length > 0 ? parts[0] : string.Empty,
                        PinRight = parts.Length > 1 ? parts[1] : string.Empty,
                        CheckCondition = item.CheckCondition
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

            // 订阅扫描枪事件
            _scannerHelper.Subscribe();

            // 同步连接状态
            IsScannerConnected = _scannerHelper.IsScannerConnected;
            ScannerStatusText = _scannerHelper.ScannerStatusText;

            // 自动连接扫描枪（如果尚未连接）
            if (_scannerHelper != null && !_scannerHelper.IsScannerConnected)
            {
                Task.Run(() => _scannerHelper.Connect(ScannerPortName));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 页面导航离开时触发
        /// 必须取消订阅扫描枪事件，防止在其他页面扫码时误触发本页逻辑
        /// </summary>
        public Task OnNavigatedFromAsync()
        {
            _logger.LogDebug("离开方案编辑页面");

            _scannerHelper.Unsubscribe();

            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion
    }

    /// <summary>
    /// 检测项目 ViewModel（用于DataGrid行绑定）
    /// 将PlanItem的ItemName拆分为左右引脚，便于独立编辑
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

        /// <summary>检查条件（OPEN / SHORT）</summary>
        [ObservableProperty]
        private string _checkCondition = "OPEN";

        /// <summary>完整的检测项目名称（如 A1-A2）</summary>
        public string FullItemName => $"{PinLeft}-{PinRight}";
    }
}