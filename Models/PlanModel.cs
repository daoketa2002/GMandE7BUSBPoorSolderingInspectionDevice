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
    /// - Continuity（导通）：Unit = "OPEN"(开路) 或 "SHORT"(短路)，无上下限
    /// - Resistance（电阻值）：Unit = "Ω"，需配置 LowerLimit / UpperLimit
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
        /// 序号（从1开始，连续编排）
        /// </summary>
        [JsonPropertyName("Order")]
        public int Index { get; set; }

        // ═══════════════════════════════════════════════════════════════
        // 方案需求变动 — 新增检测方式、上下限、单位字段
        // 替代旧版 PhysicalUnits 字段
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 检测方式
        /// "Continuity" — 导通检测（检查回路通断状态）
        /// "Resistance" — 电阻值检测（检查阻值是否在预设范围内）
        /// </summary>
        [JsonPropertyName("CheckMode")]
        public string CheckMode { get; set; } = "Continuity";

        /// <summary>
        /// 电阻值下限（Ω）
        /// 仅 CheckMode = "Resistance" 时有效，导通模式下为 null
        /// </summary>
        [JsonPropertyName("LowerLimit")]
        public double? LowerLimit { get; set; }

        /// <summary>
        /// 电阻值上限（Ω）
        /// 仅 CheckMode = "Resistance" 时有效，导通模式下为 null
        /// </summary>
        [JsonPropertyName("UpperLimit")]
        public double? UpperLimit { get; set; }

        /// <summary>
        /// 物理单位或期望结果
        /// 导通模式： "OPEN"(期望开路) 或 "SHORT"(期望短路)
        /// 电阻模式： "Ω"
        /// </summary>
        [JsonPropertyName("Unit")]
        public string Unit { get; set; } = "OPEN";
    }
}