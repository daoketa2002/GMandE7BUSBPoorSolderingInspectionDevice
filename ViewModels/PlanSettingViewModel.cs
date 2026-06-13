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
    public partial class PlanSettingViewModel : ObservableObject, INavigationAware
    {
        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationService;
        private readonly IPlanStorageService _planStorageService;
        private readonly ICurrentPlanService _currentPlanService;
        private readonly Serilog.ILogger _logger;

        // 全部方案列表（原始数据）
        private List<PlanModel> _allPlans = new List<PlanModel>();

        public PlanSettingViewModel(
            INavigationService navigationService,
            INotificationService notificationService,
            IPlanStorageService planStorageService,
            ICurrentPlanService currentPlanService)
        {
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _planStorageService = planStorageService ?? throw new ArgumentNullException(nameof(planStorageService));
            _currentPlanService = currentPlanService ?? throw new ArgumentNullException(nameof(currentPlanService));
            _logger = Log.ForContext<PlanSettingViewModel>();

            // 初始化下拉选项
            SeriesOptions = new ObservableCollection<string>(PlanStorageService.DefaultSeries);
            ModelOptions = new ObservableCollection<string>(PlanStorageService.DefaultModels);

            _logger.Debug("PlanSettingViewModel 构造完成");
        }

        #region 检索条件

        [ObservableProperty]
        private string _filterSeries = string.Empty;

        [ObservableProperty]
        private string _filterModel = string.Empty;

        [ObservableProperty]
        private string _filterPlanName = string.Empty;

        /// <summary>
        /// 系列下拉选项
        /// </summary>
        public ObservableCollection<string> SeriesOptions { get; }

        /// <summary>
        /// 型号下拉选项
        /// </summary>
        public ObservableCollection<string> ModelOptions { get; }

        /// <summary>
        /// 当前方案名称（显示用）
        /// </summary>
        [ObservableProperty]
        private string _currentPlanDisplay = "未选择方案";

        #endregion

        #region 方案列表

        /// <summary>
        /// 显示的方案列表（过滤后）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<PlanModel> _plans = new ObservableCollection<PlanModel>();

        /// <summary>
        /// 表格选中的方案
        /// </summary>
        [ObservableProperty]
        private PlanModel? _selectedPlan;

        /// <summary>
        /// 总方案数显示
        /// </summary>
        [ObservableProperty]
        private string _totalCountText = "共 0 个方案";

        /// <summary>
        /// 是否有选中方案（用于按钮启用/禁用）
        /// </summary>
        [ObservableProperty]
        private bool _hasSelection;

        /// <summary>
        /// 方案总数（内部用）
        /// </summary>
        private int _totalPlanCount;

        partial void OnSelectedPlanChanged(PlanModel? value)
        {
            HasSelection = value != null;
        }

        #endregion

        #region 检索命令

        /// <summary>
        /// 执行检索
        /// </summary>
        [RelayCommand]
        private void Search()
        {
            ApplyFilter();
        }

        /// <summary>
        /// 清空检索条件
        /// </summary>
        [RelayCommand]
        private void ClearFilter()
        {
            FilterSeries = string.Empty;
            FilterModel = string.Empty;
            FilterPlanName = string.Empty;
            ApplyFilter();
        }

        /// <summary>
        /// 应用过滤条件
        /// </summary>
        private void ApplyFilter()
        {
            var filtered = _allPlans.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(FilterSeries))
            {
                var series = FilterSeries.Trim();
                filtered = filtered.Where(p => p.Series.Contains(series, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(FilterModel))
            {
                var model = FilterModel.Trim();
                filtered = filtered.Where(p => p.Model.Contains(model, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(FilterPlanName))
            {
                var planName = FilterPlanName.Trim();
                filtered = filtered.Where(p => p.PlanName.Contains(planName, StringComparison.OrdinalIgnoreCase));
            }

            Plans = new ObservableCollection<PlanModel>(filtered.OrderBy(p => p.Series).ThenBy(p => p.Model).ThenBy(p => p.PlanName));
            TotalCountText = $"共 {Plans.Count} 个方案";
            _totalPlanCount = Plans.Count;
        }

        #endregion

        #region 方案操作命令

        /// <summary>
        /// 新增方案 → 导航到编辑页面
        /// </summary>
        [RelayCommand]
        private async Task AddPlanAsync()
        {
            try
            {
                _logger.Information("用户点击新增方案");
                await _navigationService.NavigateToAsync<PlanEditView>("Main", null);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到方案编辑页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 编辑方案 → 导航到编辑页面，传入选中方案
        /// </summary>
        [RelayCommand]
        private async Task EditPlanAsync()
        {
            try
            {
                if (SelectedPlan == null)
                {
                    await _notificationService.ShowWarningAsync("请先选择要编辑的方案！", "未选择");
                    return;
                }

                _logger.Information("用户点击编辑方案: {Plan}", SelectedPlan.PlanName);
                // 传入选中方案的深拷贝
                var clone = System.Text.Json.JsonSerializer.Deserialize<PlanModel>(
                    System.Text.Json.JsonSerializer.Serialize(SelectedPlan));
                await _navigationService.NavigateToAsync<PlanEditView>("Main", clone);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导航到方案编辑页面失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 删除选中方案
        /// </summary>
        [RelayCommand]
        private async Task DeletePlanAsync()
        {
            try
            {
                if (SelectedPlan == null)
                {
                    await _notificationService.ShowWarningAsync("请先选择要删除的方案！", "未选择");
                    return;
                }

                var plan = SelectedPlan;
                var confirmed = await _notificationService.ConfirmAsync(
                    $"确定要删除方案 \"{plan.PlanName}\"（{plan.Series} - {plan.Model}）吗？\n\n此操作不可恢复！",
                    "确认删除");

                if (!confirmed) return;

                await _planStorageService.DeletePlanAsync(plan.Series, plan.Model, plan.PlanName);

                // 如果删除的是当前方案，清除
                if (_currentPlanService.CurrentPlan?.Series == plan.Series &&
                    _currentPlanService.CurrentPlan?.Model == plan.Model &&
                    _currentPlanService.CurrentPlan?.PlanName == plan.PlanName)
                {
                    _currentPlanService.CurrentPlan = null;
                }

                await RefreshPlansAsync();
                _logger.Information("方案已删除: {Plan}", plan.PlanName);
                await _notificationService.ShowInfoAsync($"方案 \"{plan.PlanName}\" 已删除", "删除成功");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "删除方案失败");
                await _notificationService.ShowErrorAsync($"删除失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 选择当前方案（供运行界面使用）
        /// </summary>
        [RelayCommand]
        private async Task SetAsCurrentPlanAsync()
        {
            try
            {
                if (SelectedPlan == null)
                {
                    await _notificationService.ShowWarningAsync("请先选择方案！", "未选择");
                    return;
                }

                _currentPlanService.CurrentPlan = SelectedPlan;
                CurrentPlanDisplay = _currentPlanService.CurrentPlanName;

                _logger.Information("当前方案已设为: {Plan}", CurrentPlanDisplay);
                await _notificationService.ShowInfoAsync($"当前方案已设为：\n{CurrentPlanDisplay}", "设置成功");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "设置当前方案失败");
                await _notificationService.ShowErrorAsync($"设置失败：{ex.Message}");
            }
        }

        #endregion

        #region 返回命令

        [RelayCommand]
        private async Task GoBackAsync()
        {
            try
            {
                _logger.Information("用户点击返回主菜单");
                await _navigationService.NavigateToAsync<MainMenuView>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "返回主菜单失败");
                await _notificationService.ShowErrorAsync($"导航失败：{ex.Message}");
            }
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 刷新方案列表
        /// </summary>
        private async Task RefreshPlansAsync()
        {
            _allPlans = await _planStorageService.LoadAllPlansAsync();
            ApplyFilter();

            // 刷新下拉选项
            var allSeries = await _planStorageService.GetAllSeriesAsync();
            SeriesOptions.Clear();
            foreach (var s in allSeries)
                SeriesOptions.Add(s);

            var allModels = await _planStorageService.GetModelsBySeriesAsync(FilterSeries);
            if (!string.IsNullOrWhiteSpace(FilterSeries))
            {
                ModelOptions.Clear();
                foreach (var m in allModels)
                    ModelOptions.Add(m);
            }
            else
            {
                ModelOptions.Clear();
                foreach (var m in PlanStorageService.DefaultModels)
                    ModelOptions.Add(m);
            }
        }

        #endregion

        #region INavigationAware 实现

        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.Debug("进入方案设定页面");

            // 刷新当前方案显示
            CurrentPlanDisplay = _currentPlanService.CurrentPlanName;

            // 刷新列表
            await RefreshPlansAsync();
        }

        public Task OnNavigatedFromAsync()
        {
            _logger.Debug("离开方案设定页面");
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion
    }
}