// ============================================================
// 文件: Models/PlanModel.cs
// 描述: 方案数据模型
// 改动说明: CheckMode 存储值改为中文 "导通"/"电阻值"
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 方案数据模型
    /// 对应一个独立的检测方案文件（一个方案一个JSON文件）
    /// 新结构：机种→方案 两级，去除系列概念
    /// </summary>
    public class PlanModel
    {
        /// <summary>
        /// 机种名称（如 T998248391、998245664NNHB）
        /// 对应文件夹名，来源于扫描枪扫码或手动输入
        /// </summary>
        [JsonPropertyName("MachineType")]
        public string MachineType { get; set; } = string.Empty;

        /// <summary>
        /// 方案名称（如 "方案A"、"测试方案1"）
        /// 对应文件名（不含扩展名）
        /// </summary>
        [JsonPropertyName("PlanName")]
        public string PlanName { get; set; } = string.Empty;

        /// <summary>
        /// 方案创建时间
        /// </summary>
        [JsonPropertyName("CreatedTime")]
        public DateTime CreatedTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 方案最后修改时间
        /// 每次保存时自动更新
        /// </summary>
        [JsonPropertyName("LastModifiedTime")]
        public DateTime LastModifiedTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 检测项目列表
        /// </summary>
        [JsonPropertyName("InspectItems")]
        public List<PlanItem> Items { get; set; } = new List<PlanItem>();
    }

    /// <summary>
    /// 方案中的单个检测项目
    /// 每个项目有唯一标识（GUID），便于追踪和引用
    ///
    /// 改动说明（方案需求变动）：
    /// 将旧版单一"检查条件"字段升级为结构化检测模式：
    /// - 导通：ModeValue = "OPEN"(开路) 或 "SHORT"(短路)，无上下限
    /// - 电阻值：ModeValue = 万用表实际测量值（运行时填充），需配置 LowerLimit / UpperLimit
    /// - ★ CheckMode 存储中文值 "导通" / "电阻值"（JSON直接可读）
    /// - ★ Unit 字段已删除，由 ModeValue 替代
    /// </summary>
    public class PlanItem
    {
        /// <summary>
        /// 唯一标识（GUID）
        /// 用于精确追踪每个检测项目
        /// </summary>
        [JsonPropertyName("Id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 项目名称（如 "A1-A2"、"B1-GND"）
        /// </summary>
        [JsonPropertyName("Name")]
        public string ItemName { get; set; } = string.Empty;

        /// <summary>
        /// 左引脚极性。
        /// 仅在方案编辑页展示；运行时会随检测配置流转，待 PLC 地址表确认后用于写入当前测试点信息。
        /// </summary>
        [JsonPropertyName("PinLeftPolarity")]
        public string PinLeftPolarity { get; set; } = PinPolarityConstants.Positive;

        /// <summary>
        /// 右引脚极性。
        /// 仅在方案编辑页展示；运行时会随检测配置流转，待 PLC 地址表确认后用于写入当前测试点信息。
        /// </summary>
        [JsonPropertyName("PinRightPolarity")]
        public string PinRightPolarity { get; set; } = PinPolarityConstants.Negative;

        /// <summary>
        /// 序号（从1开始，连续编排）
        /// </summary>
        [JsonPropertyName("Order")]
        public int Index { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // 方案需求变动 — 新增检测方式、上下限、单位字段
        // 替代旧版 PhysicalUnits 字段
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 检测方式（中文存储）
        /// "导通" — 导通检测（检查回路通断状态）
        /// "电阻值" — 电阻值检测（检查阻值是否在预设范围内）
        /// </summary>
        [JsonPropertyName("CheckMode")]
        public string CheckMode { get; set; } = "导通";

        /// <summary>
        /// 电阻值下限（Ω）
        /// 仅 CheckMode = "电阻值" 时有效，导通模式下为 null
        /// </summary>
        [JsonPropertyName("LowerLimit")]
        public double? LowerLimit { get; set; }

        /// <summary>
        /// 电阻值上限（Ω）
        /// 仅 CheckMode = "电阻值" 时有效，导通模式下为 null
        /// </summary>
        [JsonPropertyName("UpperLimit")]
        public double? UpperLimit { get; set; }

                /// <summary>
        /// 模式值（替代旧字段 Unit）
        /// 导通模式： "OPEN"(期望开路) 或 "SHORT"(期望短路)
        /// 电阻值模式： 万用表实际测量值（方案编辑时为 null，运行时由检测引擎填充）
        /// 界面"下限"列根据 CheckMode 条件渲染：
        ///   导通 → ComboBox 绑定 ModeValue（OPEN/SHORT）
        ///   电阻值 → TextBox 绑定 LowerLimit（配置阈值）
        /// </summary>
        [JsonPropertyName("ModeValue")]
        public string? ModeValue { get; set; } = "OPEN";
    }

    /// <summary>
    /// 检测方式常量（中文标准值）
    /// 用于避免字符串硬编码分散在各处
    /// </summary>
    public static class CheckModeConstants
    {
        /// <summary>导通检测</summary>
        public const string Continuity = "导通";

        /// <summary>电阻值检测</summary>
        public const string Resistance = "电阻值";
    }

    /// <summary>
    /// 引脚极性常量。
    /// 方案编辑页使用中文值保存，PLC 编码规则为：正极=1、负极=0。
    /// </summary>
    public static class PinPolarityConstants
    {
        /// <summary>正极</summary>
        public const string Positive = "正极";

        /// <summary>负极</summary>
        public const string Negative = "负极";

        /// <summary>
        /// 将方案中的中文极性转换为 PLC 数值编码。
        /// </summary>
        public static ushort ToPlcCode(string? polarity)
        {
            return polarity == Positive ? (ushort)1 : (ushort)0;
        }

        /// <summary>
        /// 规范化极性值，兼容旧方案缺失字段或异常空值。
        /// </summary>
        public static string Normalize(string? polarity, string defaultValue)
        {
            return polarity == Positive || polarity == Negative ? polarity : defaultValue;
        }
    }
}
