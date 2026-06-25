// ============================================================
// 文件: Models/PinResult.cs
// 描述: Pin 检测结果明细数据模型
//      每条记录对应一个检测项目的判定结果
// 存储: 内嵌在 CSV 日志文件中作为动态列（非独立表）
//      每条 LogRecord 包含多个 PinResult
// 遗留: Id / LogRecordId / LogRecord 导航属性 为 SqliteTestRecordStorage
//      使用（EF Core 需要主键和外键），CSV 实现不关心这些字段
// ============================================================

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// Pin 检测结果明细数据模型
    /// 存储单个检测项目的判定结果（如 "A4-A5" → "2.5Ω"）
    /// 与 LogRecord 通过列表包含关系关联（LogRecord.PinResults）
    /// 写入 CSV 时每个 PinResult 展开为一行中的两列（值列+判定列）
    /// </summary>
    [Table("PinResults")]
    public class PinResult
    {
        /// <summary>
        /// 主键（遗留：SqliteTestRecordStorage EF Core 使用，CSV 实现不关心）
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// 外键 —— 关联的主记录 Id（遗留：SqliteTestRecordStorage EF Core 使用）
        /// </summary>
        [Required]
        public int LogRecordId { get; set; }

        /// <summary>
        /// Pin 名称（如 "A4-A5"、"B3-B7"）
        /// 来源于方案 JSON 中 InspectItems[].ItemName 字段
        /// CSV 文件中作为动态列标题使用
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string PinName { get; set; } = string.Empty;

        /// <summary>
        /// 检测结果
        /// 可能是 "OK"、"NG" 或具体的测量数值（如 "10.5MΩ"）
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Result { get; set; } = string.Empty;

        /// <summary>
        /// 导航属性 —— 所属的主检测记录（遗留：SqliteTestRecordStorage EF Core 使用）
        /// </summary>
        [ForeignKey(nameof(LogRecordId))]
        public LogRecord LogRecord { get; set; } = null!;
    }
}