using CommunityToolkit.Mvvm.ComponentModel;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 测试项目数据模型
    /// </summary>
    public partial class TestItemModel : ObservableObject
    {
        /// <summary>
        /// 序号
        /// </summary>
        [ObservableProperty]
        private int _index;

        /// <summary>
        /// 项目名称（如 A4-A5）
        /// </summary>
        [ObservableProperty]
        private string _itemName = string.Empty;

        /// <summary>
        /// 检查条件（如 OPEN 开路）
        /// </summary>
        [ObservableProperty]
        private string _checkCondition = string.Empty;

        /// <summary>
        /// 检查结果（实时反馈数值）
        /// </summary>
        [ObservableProperty]
        private string _checkResult = string.Empty;

        /// <summary>
        /// 判定结果：OK / NG / (空=未测试)
        /// </summary>
        [ObservableProperty]
        private string _judgment = string.Empty;

        /// <summary>
        /// 判定是否完成（用于样式绑定）
        /// </summary>
        public bool IsJudged => !string.IsNullOrEmpty(Judgment);
    }
}