// ============================================================
// 文件: Models/CheckMode.cs
// 描述: 检测方式枚举
//       Continuity — 导通检测（含开路OPEN/短路SHORT两种期望）
//       Resistance — 电阻值检测（需配置上下限范围）
// ============================================================

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 检测方式枚举
    /// </summary>
    public enum CheckMode
    {
        /// <summary>
        /// 导通检测 —— 判断回路是否开路或短路
        /// 仪器返回极大值 → 开路(OPEN)，仪器返回极小值 → 短路(SHORT)
        /// </summary>
        Continuity,

        /// <summary>
        /// 电阻值检测 —— 判断实测值是否在预设范围内
        /// OK = LowerLimit ≤ 实测值 ≤ UpperLimit
        /// </summary>
        Resistance
    }
}
