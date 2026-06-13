using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 日志数据模型 - 支持固定列+动态列
    /// </summary>
    public partial class LogDataModel : ObservableObject
    {
        /// <summary>序号</summary>
        public int Index { get; set; }

        /// <summary>机种名称</summary>
        [ObservableProperty]
        private string _machineType = string.Empty;

        /// <summary>序列号</summary>
        [ObservableProperty]
        private string _serialNumber = string.Empty;

        /// <summary>方案名称</summary>
        [ObservableProperty]
        private string _planName = string.Empty;

        /// <summary>检查者</summary>
        [ObservableProperty]
        private string _inspector = string.Empty;

        /// <summary>综合判定 (OK/NG)</summary>
        [ObservableProperty]
        private string _judgment = string.Empty;

        /// <summary>日期</summary>
        [ObservableProperty]
        private string _date = string.Empty;

        /// <summary>时间</summary>
        [ObservableProperty]
        private string _time = string.Empty;

        /// <summary>
        /// 动态检测项数据 (列名 -> 值)
        /// 例如: {"B4-B5": "0.512", "B5-B6": "1.023"}
        /// </summary>
        public Dictionary<string, string> DynamicItems { get; set; } = new();
    }
}