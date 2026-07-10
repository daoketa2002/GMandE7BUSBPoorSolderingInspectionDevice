// ============================================================
// 文件: Models/LogIndexEntry.cs
// 描述: 月度日志索引条目，record-index.csv 使用
// ============================================================

using System;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// record-index.csv 的轻量索引行。
    /// 正式 CSV 仍是事实源，索引只用于快速筛选和定位行号。
    /// </summary>
    public class LogIndexEntry
    {
        public DateTime Timestamp { get; set; }

        public string MachineType { get; set; } = string.Empty;

        public string SerialNumber { get; set; } = string.Empty;

        public string PlanName { get; set; } = string.Empty;

        public int PlanVersion { get; set; } = 1;

        public string FinalResult { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public int RowNumber { get; set; }
    }
}
