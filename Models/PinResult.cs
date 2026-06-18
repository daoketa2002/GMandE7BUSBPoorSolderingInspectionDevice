// ============================================================
// 文件: Models/PinResult.cs
// 描述: EF Core 实体 —— Pin结果明细表（PinResults）
//      每条记录对应一个检测Pin的判定结果
// 数据库: SQLite
// ============================================================

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// Pin结果明细表 —— 对应 SQLite PinResults 表
    /// 存储单个检测项目的判定结果（如 "A4-A5" → "OK"）
    /// 与 LogRecord 通过外键 LogRecordId 关联
    /// </summary>
    [Table("PinResults")]
    public class PinResult
    {
        /// <summary>
        /// 主键，自增整数
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// 外键 —— 关联的主记录 Id
        /// 对应 LogRecords 表的 Id 字段
        /// </summary>
        [Required]
        public int LogRecordId { get; set; }

        /// <summary>
        /// Pin 名称（如 "A4-A5"、"B3-B7"）
        /// 来源于方案 JSON 中 InspectItems[].Name 字段
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

        // ============================================================
        // 导航属性
        // ============================================================

        /// <summary>
        /// 导航属性 —— 所属的主检测记录
        /// EF Core 通过 LogRecordId 外键自动建立关联
        /// </summary>
        public LogRecord LogRecord { get; set; } = null!;
    }
}