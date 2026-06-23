// ============================================================
// 文件: Models/LogRecord.cs
// 描述: EF Core 实体 —— 检测主记录表（LogRecords）
//      每次完整检测流程产生一条记录
// 数据库: SQLite
// 改动说明: 新增 MachineType 字段，保留原有 Series 字段兼容旧数据
// ============================================================

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 检测主记录表 —— 对应 SQLite LogRecords 表
    /// 存储每次完整检测流程的元数据（系列、机种、序列号、方案、操作员、综合判定等）
    /// </summary>
    [Table("LogRecords")]
    public class LogRecord
    {
        /// <summary>
        /// 主键，自增整数
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// 检测时间戳 —— 精确到毫秒
        /// 用于高频测试场景下的精确排序
        /// SQLite 使用 TEXT 类型存储（ISO 8601 格式）
        /// </summary>
        [Required]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 系列名称（如 "GM5"、"E78"）
        /// 来源于旧方案JSON中的 Series 字段
        /// 保留用于兼容旧数据，新数据可为空
        /// </summary>
        [Required]
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
        /// 记录创建时间（数据库自动填充）
        /// 与 Timestamp 的区别：Timestamp 是检测发生的实际时间，
        /// CreatedAt 是记录写入数据库的时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // ============================================================
        // 导航属性
        // ============================================================

        /// <summary>
        /// 导航属性 —— 本记录包含的所有 Pin 检测明细
        /// EF Core 通过 LogRecordId 外键自动关联
        /// 级联删除：删除主记录时自动删除所有明细
        /// </summary>
        public List<PinResult> PinResults { get; set; } = new();
    }
}