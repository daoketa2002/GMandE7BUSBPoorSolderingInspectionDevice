using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Views;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    public partial class OperatorSettingsViewModel : ObservableObject, INavigationAware
    {
        private readonly IOperatorStorageService _storageService;
        private readonly IOperatorStateService _operatorStateService;  // 🆕 全局状态
        private readonly INavigationService _navigationService;
        private readonly ILogger<OperatorSettingsViewModel> _logger;

        public OperatorSettingsViewModel(
            IOperatorStorageService storageService,
            IOperatorStateService operatorStateService,
            INavigationService navigationService,
            ILogger<OperatorSettingsViewModel> logger)
        {
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _operatorStateService = operatorStateService ?? throw new ArgumentNullException(nameof(operatorStateService));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            Operators = new ObservableCollection<OperatorModel>();
        }

        #region 属性

        public ObservableCollection<OperatorModel> Operators { get; }

        [ObservableProperty]
        private OperatorModel? _selectedOperator;

        [ObservableProperty]
        private string _inputName = string.Empty;

        [ObservableProperty]
        private string _hintText = "输入新作业员名字后点击「新增」";

        [ObservableProperty]
        private string _hintColor = "#6C757D";

        [ObservableProperty]
        private bool _canDelete;

        [ObservableProperty]
        private string _totalCountText = "共 0 名作业员";

        /// <summary>
        /// 🆕 当前生效的作业员名字（显示在顶部）
        /// </summary>
        [ObservableProperty]
        private string _currentOperatorDisplay = "未选择";

        /// <summary>
        /// 🆕 当前作业员是否有效（用于高亮显示）
        /// </summary>
        [ObservableProperty]
        private bool _hasCurrentOperator;

        #endregion

        #region 属性变更回调

        partial void OnSelectedOperatorChanged(OperatorModel? value)
        {
            if (value != null)
            {
                InputName = value.Name;
                HintText = $"已选中：{value.Name}（可修改后点击新增覆盖）";
                HintColor = "#27AE60";
                CanDelete = true;
            }
            else
            {
                CanDelete = false;
            }
        }

        partial void OnInputNameChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                HintText = "请输入作业员名字";
                HintColor = "#E67E22";
                CanDelete = false;
                return;
            }

            var matched = Operators.FirstOrDefault(o =>
                string.Equals(o.Name, value.Trim(), StringComparison.OrdinalIgnoreCase));

            if (matched != null && SelectedOperator != matched)
            {
                SelectedOperator = matched;
                HintText = $"已匹配：{matched.Name}（点击新增将覆盖）";
                HintColor = "#F39C12";
                CanDelete = true;
            }
            else if (matched == null)
            {
                if (SelectedOperator != null)
                    SelectedOperator = null;
                HintText = $"将新增：\"{value.Trim()}\"";
                HintColor = "#F39C12";
                CanDelete = false;
            }
        }

        #endregion

        #region 命令

        /// <summary>
        /// 新增 / 更新
        /// </summary>
        [RelayCommand]
        private async Task AddOrUpdateAsync()
        {
            // ★ 自动去首尾空格
            var name = InputName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请输入有效的作业员名字！", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ★ 重名检查（忽略大小写 + 去空格）
            var existing = Operators.FirstOrDefault(o =>
                string.Equals(o.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // 已有同名作业员 → 提示用户并询问是否覆盖
                var confirmMsg = $"作业员「{existing.Name}」已存在，是否更新其信息？";
                var result = MessageBox.Show(confirmMsg, "重名确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    _logger.LogInformation("用户取消重复作业员添加: {Operator}", name);
                    return;
                }
                existing.Name = name;
                existing.CreatedAt = DateTime.Now;
                _logger.LogInformation("作业员已更新（重名覆盖）: {Operator}", name);
            }
            else
            {
                var newOperator = new OperatorModel
                {
                    Id = await _storageService.GetNextIdAsync(),
                    Name = name,
                    CreatedAt = DateTime.Now
                };
                Operators.Add(newOperator);
                _logger.LogInformation("作业员已新增: {Operator}", name);
            }

            await _storageService.SaveOperatorsAsync(Operators.ToList());
            UpdateTotalCount();

            InputName = string.Empty;
            SelectedOperator = null;
            HintText = "✅ 操作成功！可继续输入新名字";
            HintColor = "#27AE60";
        }

        /// <summary>
        /// 🆕 设为当前作业员
        /// </summary>
        [RelayCommand]
        private void SetAsCurrentOperator()
        {
            if (SelectedOperator == null)
            {
                MessageBox.Show("请先从左侧列表中选择作业员！", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _operatorStateService.CurrentOperator = SelectedOperator;
            UpdateCurrentOperatorDisplay();

            HintText = $"✅ 当前作业员已设为：{SelectedOperator.Name}";
            HintColor = "#27AE60";

            _logger.LogInformation("当前作业员已设为: {Operator}", SelectedOperator.Name);
        }

        /// <summary>
        /// 删除
        /// </summary>
        [RelayCommand]
        private async Task DeleteAsync()
        {
            if (SelectedOperator == null)
            {
                MessageBox.Show("请先从列表中选择要删除的作业员！", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 🆕 检查是否正在删除当前作业员
            if (_operatorStateService.CurrentOperator?.Id == SelectedOperator.Id)
            {
                var confirmMsg = $"作业员 \"{SelectedOperator.Name}\" 当前正在使用中！\n\n" +
                                 "删除后将自动使用默认作业员，确定继续吗？";
                var result = MessageBox.Show(confirmMsg, "警告",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }
            else
            {
                var result = MessageBox.Show(
                    $"确定要删除作业员 \"{SelectedOperator.Name}\" 吗？\n\n此操作不可恢复！",
                    "删除确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }

            var toRemove = Operators.FirstOrDefault(o => o.Id == SelectedOperator.Id);
            if (toRemove != null)
            {
                Operators.Remove(toRemove);

                // 🆕 如果删除的是当前作业员，清除状态
                if (_operatorStateService.CurrentOperator?.Id == toRemove.Id)
                {
                    _operatorStateService.CurrentOperator = null;
                }
            }

            await _storageService.SaveOperatorsAsync(Operators.ToList());
            UpdateTotalCount();
            UpdateCurrentOperatorDisplay();

            SelectedOperator = null;
            InputName = string.Empty;
            HintText = "已删除，请选择或输入新名字";
            HintColor = "#6C757D";
            CanDelete = false;

            _logger.LogInformation("作业员已删除: {Operator}", toRemove?.Name);
        }

        /// <summary>
        /// 清空输入框
        /// </summary>
        [RelayCommand]
        private void ClearInput()
        {
            SelectedOperator = null;
            InputName = string.Empty;
            HintText = "输入新作业员名字后点击「新增」";
            HintColor = "#6C757D";
            CanDelete = false;
        }

        /// <summary>
        /// 返回主菜单
        /// </summary>
        [RelayCommand]
        private async Task GoBackAsync()
        {
            _logger.LogInformation("返回主菜单");
            await _navigationService.NavigateToAsync<MainMenuView>();
        }

        #endregion

        #region INavigationAware

        public async Task OnNavigatedToAsync(object? parameter = null)
        {
            _logger.LogInformation("进入作业员设定页面");
            await LoadOperatorsAsync();
            UpdateCurrentOperatorDisplay();
        }

        public Task OnNavigatedFromAsync()
        {
            _logger.LogInformation("离开作业员设定页面");
            return Task.CompletedTask;
        }

        public Task<bool> CanNavigateFromAsync()
        {
            return Task.FromResult(true);
        }

        #endregion

        #region 私有方法

        private async Task LoadOperatorsAsync()
        {
            try
            {
                var list = await _storageService.LoadOperatorsAsync();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Operators.Clear();
                    foreach (var op in list)
                    {
                        Operators.Add(op);
                    }
                    UpdateTotalCount();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载作业员列表失败");
            }
        }

        private void UpdateTotalCount()
        {
            TotalCountText = $"共 {Operators.Count} 名作业员";
        }

        /// <summary>
        /// 🆕 更新当前作业员显示
        /// </summary>
        private void UpdateCurrentOperatorDisplay()
        {
            if (_operatorStateService.HasOperator)
            {
                CurrentOperatorDisplay = $"当前：{_operatorStateService.CurrentOperator!.Name}";
                HasCurrentOperator = true;
            }
            else
            {
                CurrentOperatorDisplay = $"当前：{_operatorStateService.DefaultOperatorName}（默认）";
                HasCurrentOperator = false;
            }
        }

        #endregion
    }
}