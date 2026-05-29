using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation
{
    /// <summary>
    /// 应用中所有逻辑导航区域的标准名称。
    /// 命名规范：语义化、无前缀/后缀污染、全大写常量。
    /// </summary>
    public static class RegionNames
    {
        /// <summary>
        /// 应用外壳区域（根布局容器）
        /// </summary>
        public const string Shell = "Shell";

        /// <summary>
        /// 主工作内容区（位于 BaseLayoutView 内部）
        /// </summary>
        public const string Main = "Main"; // 简洁！比 "MainContent" 更通用

        /// <summary>
        /// 全局模态层（如弹窗、通知浮层）—— 未来扩展用
        /// </summary>
        public const string Modal = "Modal";

        /// <summary>
        /// 侧边栏区域（如导航菜单）—— 未来扩展用
        /// </summary>
        public const string Sidebar = "Sidebar";
    }
}
