using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    public partial class PlanEditViewModel : ObservableObject, INavigationAware
    {
        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IPlanStorageService _planStorageService;
        private readonly IScannerBarcodeService? _scannerService;
        private readonly Serilog.ILogger _logger;

        private bool _isEditMode = false;
        private PlanModel? _originalPlan;

        public PlanEditViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IPlanStorageService planStorageService,
            IScannerBarcodeService? scannerService = null)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _scannerService = scannerService;
            _logger = Log.ForContext<PlanEditViewModel>();

            SeriesOptions = new ObservableCollection<string>(PlanStorageService.DefaultSeries);
            ModelOptions = new ObservableCollection<string>(PlanStorageService.DefaultModels);
            PinOptions = new ObservableCollection<string>(PlanStorageService.PinList);
            CheckConditionOptions = new ObservableCollection<string> { "OPEN", "SHORT" };

            _logger.Debug("PlanEditViewModel 构造完成");
        }

        #region 基本信息

        [ObservableProperty]
        private string _series = string.Empty;

        [ObservableProperty]
        private string _model = string.Empty;

        [ObservableProperty]
        private string _planName = string.Empty;

        public ObservableCollection<string> SeriesOptions { get; }
        public ObservableCollection<string> ModelOptions { get; }
        public ObservableCollection<string> PinOptions { get; }
        public ObservableCollection<string> CheckConditionOptions { get; }

        #endregion

        #region 项目列表

        [ObservableProperty]
        private ObservableCollection<PlanItemViewModel> _items = new ObservableCollection<PlanItemViewModel>();

        [ObservableProperty]
        private PlanItemViewModel? _selectedItem;

        [ObservableProperty]
        private bool _hasItemSelection;

        partial void OnSelectedItemChanged(PlanItemViewModel? value)
        {
            HasItemSelection = value != null;
        }

        #endregion

        #region 项目操作命令

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
            _logger.Debug("新增项目，当前共 {Count} 项", Items.Count);
        }

        [RelayCommand]
        private void DeleteItem()
        {
            if (SelectedItem == null) return;
            Items.Remove(SelectedItem);
            ReindexItems();
            _logger.Debug("删除项目，当前共 {Count} 项", Items.Count);
        }

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

        private void ReindexItems()
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Index = i + 1;
            }
        }

        #endregion

        #region 保存与返回

        [RelayCommand]
        private async Task SavePlanAsync()
        {
            try
            {
                // 验证
                if (string.IsNullOrWhiteSpace(Series))
                {
                    await _notificationService.ShowWarningAsync("请输入/选择系列！", "校验失败");
                    return;
                }
                //if (string.IsNullOrWhiteSpace(Model))
                //{
                //    await _notificationService.ShowWarningAsync("请输入/选择型号！", "校验失败");
                //    return;
                //}
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
                    Series = Series.Trim(),
                    Model = Model?.Trim() ?? string.Empty,  // ← 允许为空
                    PlanName = PlanName.Trim(),
                    Items = Items.Select(item => new PlanItem
                    {
                        Index = item.Index,
                        ItemName = $"{item.PinLeft}-{item.PinRight}",
                        CheckCondition = item.CheckCondition
                    }).ToList()
                };

                // 保存：传入原始系列名以处理系列变更
                await _planStorageService.SavePlanAsync(plan, _originalPlan?.Series);

                _logger.Information("方案保存成功: {Plan}", plan.PlanName);
                await _notificationService.ShowInfoAsync($"方案 \"{plan.PlanName}\" 保存成功！", "保存成功");

                await _navigationService.NavigateToAsync<PlanSettingView>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "保存方案失败");
                await _notificationService.ShowErrorAsync($"保存失败：{ex.Message}");
            }
        }

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
                _logger.Information("用户返回方案设定页面");
                await _navigationService.NavigateToAsync<PlanSettingView>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "返回方案设定页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        private bool HasUnsavedChanges()
        {
            if (!string.IsNullOrWhiteSpace(Series)) return true;
            if (!string.IsNullOrWhiteSpace(Model)) return true;
            if (!string.IsNullOrWhiteSpace(PlanName)) return true;
            if (Items.Count > 0) return true;
            return false;
        }

        #endregion

        #region INavigationAware

        public Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.Debug("进入方案编辑页面");

            if (parameter is PlanModel existingPlan)
            {
                _isEditMode = true;
                _originalPlan = existingPlan;

                Series = existingPlan.Series;
                Model = existingPlan.Model;
                PlanName = existingPlan.PlanName;

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

                _logger.Information("编辑模式，加载方案: {Plan}", existingPlan.PlanName);
            }
            else
            {
                _isEditMode = false;
                _originalPlan = null;

                Series = string.Empty;
                Model = string.Empty;
                PlanName = string.Empty;
                Items.Clear();

                _logger.Information("新增模式");
            }

            // 订阅扫描枪（用于自动填充机种名称）
            if (_scannerService != null)
            {
                _scannerService.BarcodeParsed += OnBarcodeParsed;
            }

            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            if (_scannerService != null)
            {
                _scannerService.BarcodeParsed -= OnBarcodeParsed;
            }
            _logger.Debug("离开方案编辑页面");
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion

        private void OnBarcodeParsed(object? sender, BarcodeParsedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 自动填充机种名称
                if (string.IsNullOrWhiteSpace(Model))
                {
                    Model = e.ModelName;
                }

                // 自动推断系列
                if (string.IsNullOrWhiteSpace(Series))
                {
                    if (e.ModelName.StartsWith("T998", StringComparison.OrdinalIgnoreCase))
                        Series = "GM5";
                    else if (e.ModelName.StartsWith("998", StringComparison.OrdinalIgnoreCase))
                        Series = "E78";
                }
            });
        }
    }

    /// <summary>
    /// 项目ViewModel（用于表格绑定）
    /// </summary>
    public partial class PlanItemViewModel : ObservableObject
    {
        [ObservableProperty]
        private int _index;

        [ObservableProperty]
        private string _pinLeft = string.Empty;

        [ObservableProperty]
        private string _pinRight = string.Empty;

        [ObservableProperty]
        private string _checkCondition = "OPEN";

        public string FullItemName => $"{PinLeft}-{PinRight}";
    }
}