// ============================================================
// 文件: Models/TestItemModel.cs
// 描述: 运行界面检测项目列表的 DataGrid 行绑定模型
// 改动说明（方案需求变动）：
//   "检查条件"列拆分为"检查方式"+"下限"+"上限"三列
//   CheckCondition 字段删除，新增 CheckMode / LowerLimitText / UpperLimitText
// ============================================================

using CommunityToolkit.Mvvm.ComponentModel;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 检测项目数据模型（绑定到运行界面 DataGrid 行）
    /// 每个属性通过 [ObservableProperty] 自动生成通知，驱动 UI 刷新
    /// </summary>
    public partial class TestItemModel : ObservableObject
    {
        /// <summary>
        /// 序号（从1开始）
        /// </summary>
        [ObservableProperty]
        private int _index;

        /// <summary>
        /// 项目名称（如 A4-A5）
        /// </summary>
        [ObservableProperty]
        private string _itemName = string.Empty;

        // ═══════════════════════════════════════════════════════════════
        // 方案需求变动 —— "检查条件" 拆分为 3 列
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 检查方式显示文本
        /// "导通" — 导通检测（Continuity）
        /// "电阻值" — 电阻值检测（Resistance）
        /// </summary>
        [ObservableProperty]
        private string _checkMode = string.Empty;

        /// <summary>
        /// 下限显示文本（Ω）
        /// 导通模式：显示期望状态如 "OPEN(开路)" 或 "SHORT(短路)"
        /// 电阻模式：显示数值如 "0.5"
        /// </summary>
        [ObservableProperty]
        private string _lowerLimitText = string.Empty;

        /// <summary>
        /// 上限显示文本（Ω）
        /// 导通模式：显示 "-"（无对应参数）
        /// 电阻模式：显示数值如 "3.5"
        /// </summary>
        [ObservableProperty]
        private string _upperLimitText = string.Empty;

        /// <summary>
        /// 检查结果（实时反馈的实际测量值，如 "2.5"、"OPEN"）
        /// </summary>
        [ObservableProperty]
        private string _checkResult = string.Empty;

        /// <summary>
        /// 正式 CSV 记录值，与界面显示值分离。
        /// </summary>
        [ObservableProperty]
        private string _recordResult = string.Empty;

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
