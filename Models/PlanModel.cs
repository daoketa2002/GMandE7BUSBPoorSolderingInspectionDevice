using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 方案数据模型
    /// </summary>
    public class PlanModel
    {
        /// <summary>
        /// 系列（如 GM5、E78）
        /// </summary>
        [JsonPropertyName("Series")]
        public string Series { get; set; } = string.Empty;

        /// <summary>
        /// 型号（如 T998248391）
        /// </summary>
        [JsonPropertyName("Model")]
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// 方案名称
        /// </summary>
        [JsonPropertyName("Name")]
        public string PlanName { get; set; } = string.Empty;

        /// <summary>
        /// 检测项目列表
        /// </summary>
        [JsonPropertyName("InspectItems")]
        public List<PlanItem> Items { get; set; } = new List<PlanItem>();

        /// <summary>
        /// 串联信息（如 "GM5-T998248391-方案名"）
        /// </summary>
        [JsonPropertyName("ConcatenatedInfo")]
        public string ConcatenatedInfo => $"{Series}-{Model}-{PlanName}";

    }

    /// <summary>
    /// 方案中的单个检测项目
    /// </summary>
    public class PlanItem
    {
        /// <summary>
        /// 项目名称（如 "A4-A5"）
        /// </summary>
        [JsonPropertyName("Name")]
        public string ItemName { get; set; } = string.Empty;

        /// <summary>
        /// 序号
        /// </summary>
        [JsonPropertyName("Order")]
        public int Index { get; set; }

        /// <summary>
        /// 检查条件：OPEN 或 SHORT
        /// </summary>
        [JsonPropertyName("PhysicalUnits")]
        public string CheckCondition { get; set; } = "OPEN";
    }

    /// <summary>
    /// 系列方案集合（用于JSON文件存储）
    /// </summary>
    public class SeriesSchemeCollection
    {
        /// <summary>
        /// 该系列下的所有方案
        /// </summary>
        [JsonPropertyName("Schemes")]
        public List<PlanModel> Schemes { get; set; } = new List<PlanModel>();
    }


}