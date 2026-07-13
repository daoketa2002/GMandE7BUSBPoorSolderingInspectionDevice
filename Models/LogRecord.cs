using System;
using System.Collections.Generic;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models;

/// <summary>
/// 正式检测记录模型，对应 CSV 日志中的一条完整检测结果。
/// </summary>
public class LogRecord
{
    /// <summary>检测发生时间。</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>旧 CSV 兼容使用的系列名称。</summary>
    public string Series { get; set; } = string.Empty;

    /// <summary>机种名称。</summary>
    public string MachineType { get; set; } = string.Empty;

    /// <summary>产品序列号。</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>方案名称。</summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>检测时实际使用的方案版本。</summary>
    public int PlanVersion { get; set; } = 1;

    /// <summary>用于界面显示的方案版本文本。</summary>
    public string PlanVersionText => $"V{Math.Max(1, PlanVersion)}";

    /// <summary>作业员名称。</summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>综合判定结果，取值为 OK 或 NG。</summary>
    public string FinalResult { get; set; } = string.Empty;

    /// <summary>记录写入时间。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>本次检测的引脚明细，保存 CSV 时展开为动态列。</summary>
    public List<PinResult> PinResults { get; set; } = new();
}
