using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 日志数据模型 — 代表CSV文件中单条检测记录。
    /// 每行包含固定字段（序号、机种名称、序列号等）以及按文件变化的动态检测项（如 B4-B5、A1-A2 等）。
    /// </summary>
    public partial class LogDataModel : ObservableObject
    {
        /// <summary>序号（表格行号）</summary>
        public int Index { get; set; }

        /// <summary>机种名称（如 E78、GM5），来自CSV第2列</summary>
        [ObservableProperty]
        private string _machineType = string.Empty;

        /// <summary>序列号（如 SN20261001），来自CSV第3列</summary>
        [ObservableProperty]
        private string _serialNumber = string.Empty;

        /// <summary>方案名称（如 SchemeV1），来自CSV第4列</summary>
        [ObservableProperty]
        private string _planName = string.Empty;

        /// <summary>检查者（操作员姓名），来自CSV第5列</summary>
        [ObservableProperty]
        private string _inspector = string.Empty;

        /// <summary>综合判定结果（PASS / FAIL / NG / WARNING），来自CSV第6列</summary>
        [ObservableProperty]
        private string _judgment = string.Empty;

        /// <summary>检测日期（格式 yyyy/MM/dd）</summary>
        [ObservableProperty]
        private string _date = string.Empty;

        /// <summary>检测时间（格式 HH:mm:ss）</summary>
        [ObservableProperty]
        private string _time = string.Empty;

        /// <summary>
        /// 动态检测项数据 — 键为检测项名称（如 "B4-B5"、"A1-A2"），值为该项的判定结果（OPEN/SHORT/NG/HIGH/LOW）。
        /// 不同CSV文件可能有不同的检测项集合，使用 TryAdd 确保缺失的键以空字符串填充。
        /// </summary>
        public Dictionary<string, string> DynamicItems { get; set; } = new();
    }
}