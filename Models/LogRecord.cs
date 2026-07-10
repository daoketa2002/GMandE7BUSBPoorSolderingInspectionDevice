// ============================================================
// 文件: Models/LogRecord.cs
// 描述: 检测记录数据模型
//      每次完整检测流程产生一条记录，写入 CSV 日志文件
// 存储: CSV 文件（由 CsvTestRecordStorage 处理读写）
//       SqliteTestRecordStorage 为遗留实现，当前未注入使用
// 改动说明: 新增 MachineType 字段，保留原有 Series 字段兼容旧数据
// 遗留: [Key] / [Required] / [MaxLength] 数据注解为 SqliteTestRecordStorage
//      使用（EF Core 需要），CSV 实现不关心这些注解
// ============================================================

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 检测记录数据模型
    /// 对应 CSV 日志文件中的一行主记录
    /// 存储每次完整检测流程的元数据（机种、序列号、方案、操作员、综合判定等）
    /// </summary>
    [Table("LogRecords")]
    public class LogRecord
    {
        /// <summary>
        /// 主键（遗留：SqliteTestRecordStorage EF Core 使用，CSV 实现不关心）
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// 检测时间戳 —— 精确到毫秒
        /// 用于高频测试场景下的精确排序
        /// </summary>
        [Required]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 系列名称（如 "GM5"、"E78"）
        /// 来源于旧方案JSON中的 Series 字段
        /// 保留用于兼容旧数据，新数据可为空
        /// </summary>
        [MaxLength(50)]
        public string Series { get; set; } = string.Empty;

        /// <summary>
        /// 机种名称（如 "T998248391"、"998245664NNHB"）
        /// 新方案结构中的机种标识，对应方案文件夹名
        /// 新增字段，兼容旧数据可为空字符串
        /// </summary>
        [MaxLength(100)]
        public string MachineType { get; set; } = string.Empty;

        /// <summary>
        /// 产品序列号（如 "SN20261001"）
        /// 来源于扫描枪扫码或手动输入
        /// </summary>
        [Required]
        [MaxLength(100)]
        public string SerialNumber { get; set; } = string.Empty;

        /// <summary>
        /// 方案名称（如 "测试1"）
        /// 对应 JSON 文件名中的方案名部分
        /// </summary>
        [Required]
        [MaxLength(100)]
        public string PlanName { get; set; } = string.Empty;

        /// <summary>
        /// 检测时实际使用的方案版本。旧 CSV 缺失该列时按 V1 处理。
        /// </summary>
        public int PlanVersion { get; set; } = 1;

        /// <summary>
        /// UI 显示用版本号。
        /// </summary>
        [NotMapped]
        public string PlanVersionText => $"V{Math.Max(1, PlanVersion)}";

        /// <summary>
        /// 操作员名称
        /// 来源于全局 OperatorStateService 的当前作业员
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Operator { get; set; } = string.Empty;

        /// <summary>
        /// 综合判定结果：OK 或 NG
        /// 所有 Pin 结果均为 OK → OK，否则 → NG
        /// </summary>
        [Required]
        [MaxLength(10)]
        public string FinalResult { get; set; } = string.Empty;

        /// <summary>
        /// 记录创建时间
        /// 与 Timestamp 的区别：Timestamp 是检测发生的实际时间，
        /// CreatedAt 是记录写入 CSV 文件的时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 本记录包含的所有 Pin 检测明细
        /// 写入 CSV 文件时作为动态列（如 A4-A5、B3-B7）展开
        /// EF Core 通过 LogRecordId 外键自动关联（级联删除）
        /// </summary>
        public List<PinResult> PinResults { get; set; } = new();
    }
}
