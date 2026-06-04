using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Windows.UI.ViewManagement;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels
{
    public partial class OperatorSelectionViewModel : ObservableObject
    {
        #region 字段

        private readonly ILogger _logger;
        private readonly Window _dialogWindow;

        // TODO 模拟数据库（实际项目应替换为 DbContext），或者文件保存
        private static readonly ObservableCollection<OperatorModel> _globalOperators = new()
        {
            new OperatorModel { Id = 1, Name = "张三", CreatedAt = DateTime.Now.AddDays(-30) },
            new OperatorModel { Id = 2, Name = "李四", CreatedAt = DateTime.Now.AddDays(-25) },
            new OperatorModel { Id = 3, Name = "王五", CreatedAt = DateTime.Now.AddDays(-20) },
            new OperatorModel { Id = 4, Name = "赵六", CreatedAt = DateTime.Now.AddDays(-15) },
            new OperatorModel { Id = 5, Name = "陈七", CreatedAt = DateTime.Now.AddDays(-10) },
        };

        private static int _nextId = 6;

        #endregion

        #region 构造函数

        public OperatorSelectionViewModel(Window dialogWindow)
        {
            _dialogWindow = dialogWindow ?? throw new ArgumentNullException(nameof(dialogWindow));
            _logger = Log.ForContext<OperatorSelectionViewModel>();

            // 从全局列表加载
            Operators = new ObservableCollection<OperatorModel>(_globalOperators);

            _logger.Debug("作业员选择窗口初始化完成，当前共 {Count} 名作业员", Operators.Count);
        }

        #endregion

        #region 属性

        /// <summary>
        /// 作业员列表（绑定左侧 ListBox）
        /// </summary>
        public ObservableCollection<OperatorModel> Operators { get; }

        /// <summary>
        /// 当前选中的作业员
        /// </summary>
        [ObservableProperty]
        private OperatorModel? _selectedOperator;

        /// <summary>
        /// 输入框内容（绑定右侧 TextBox）
        /// </summary>
        [ObservableProperty]
        private string _inputName = string.Empty;

        /// <summary>
        /// 智能提示文本
        /// </summary>
        [ObservableProperty]
        private string _hintText = "若作业员名字不在列表中，请先输入名字";

        /// <summary>
        /// 提示文本颜色（正常=灰色，警告=橙色）
        /// </summary>
        [ObservableProperty]
        private string _hintColor = "#6C757D";

        /// <summary>
        /// 删除按钮是否可用
        /// </summary>
        [ObservableProperty]
        private bool _canDelete = false;

        /// <summary>
        /// 选中的作业员（返回值）
        /// </summary>
        public OperatorModel? ResultOperator { get; private set; }

        #endregion

        #region 属性变更回调

        /// <summary>
        /// 左侧列表选中项变更时
        /// </summary>
        partial void OnSelectedOperatorChanged(OperatorModel? value)
        {
            if (value != null)
            {
                // 列表选中 → 回显到输入框
                InputName = value.Name;
                HintText = $"已选择：{value.Name}";
                HintColor = "#27AE60"; // 绿色提示
                CanDelete = true;
            }
            else
            {
                // 取消选中 → 清空输入框
                if (!string.IsNullOrEmpty(InputName) && Operators.Any(o => o.Name == InputName))
                {
                    InputName = string.Empty;
                }
                HintText = "若作业员名字不在列表中，请先输入名字";
                HintColor = "#6C757D";
                CanDelete = false;
            }
        }

        /// <summary>
        /// 输入框内容变更时
        /// </summary>
        partial void OnInputNameChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                HintText = "请输入作业员名字";
                HintColor = "#E67E22"; // 橙色警告
                CanDelete = false;
                return;
            }

            // 检查输入内容是否匹配列表中的某个人
            var matched = Operators.FirstOrDefault(o =>
                string.Equals(o.Name, value.Trim(), StringComparison.OrdinalIgnoreCase));

            if (matched != null)
            {
                // 匹配到列表中的作业员 → 自动选中
                if (SelectedOperator != matched)
                {
                    SelectedOperator = matched;
                }
                HintText = $"已匹配：{matched.Name}";
                HintColor = "#27AE60"; // 绿色
                CanDelete = true;
            }
            else
            {
                // 未匹配到 → 新增模式
                if (SelectedOperator != null)
                {
                    SelectedOperator = null;
                }
                HintText = $"将新增作业员：\"{value.Trim()}\"";
                HintColor = "#F39C12"; // 黄色
                CanDelete = false;
            }
        }

        #endregion

        #region 命令

        /// <summary>
        /// 确认按钮
        /// </summary>
        [RelayCommand]
        private void Confirm()
        {
            var name = InputName?.Trim();

            // 非空校验
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请输入或选择作业员名字！", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 查找是否已存在
            var existing = Operators.FirstOrDefault(o =>
                string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // 逻辑分支 A：选择已有作业员
                ResultOperator = existing;
                _logger.Information("用户选择了已有作业员: {Operator}", existing.Name);
            }
            else
            {
                // 逻辑分支 B：新增作业员
                var newOperator = new OperatorModel
                {
                    Id = _nextId++,
                    Name = name,
                    CreatedAt = DateTime.Now
                };

                Operators.Add(newOperator);
                _globalOperators.Add(newOperator);
                ResultOperator = newOperator;

                _logger.Information("新增作业员: {Operator}, 当前共 {Count} 名", newOperator.Name, Operators.Count);
            }

            _dialogWindow.DialogResult = true;
            _dialogWindow.Close();
        }

        /// <summary>
        /// 退出按钮
        /// </summary>
        [RelayCommand]
        private void Cancel()
        {
            _logger.Debug("用户取消了作业员选择");
            ResultOperator = null;
            _dialogWindow.DialogResult = false;
            _dialogWindow.Close();
        }

        /// <summary>
        /// 删除按钮
        /// </summary>
        [RelayCommand]
        private void Delete()
        {
            if (SelectedOperator == null)
            {
                MessageBox.Show("请先从列表中选择要删除的作业员！", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var name = SelectedOperator.Name;

            // 二次确认
            var result = MessageBox.Show(
                $"确定要删除作业员 \"{name}\" 吗？\n\n此操作不可恢复！",
                "删除确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            // 从列表和数据源中移除
            var toRemove = Operators.FirstOrDefault(o => o.Id == SelectedOperator.Id);
            if (toRemove != null)
            {
                Operators.Remove(toRemove);
                _globalOperators.Remove(toRemove);
            }

            // 清空选择
            SelectedOperator = null;
            InputName = string.Empty;
            HintText = "若作业员名字不在列表中，请先输入名字";
            HintColor = "#6C757D";
            CanDelete = false;

            _logger.Information("已删除作业员: {Operator}, 剩余 {Count} 名", name, Operators.Count);
        }

        #endregion
    }
}