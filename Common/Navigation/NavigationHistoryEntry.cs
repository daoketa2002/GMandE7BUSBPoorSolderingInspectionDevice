using System;
using System.Collections.Generic;
using System.Text;

namespace WPFStandardFramework.Common.Navigation
{
    /// <summary>
    /// 导航历史条目（轻量级，不持有View引用）
    /// 用于内存安全的历史记录
    /// </summary>
    public class NavigationHistoryEntry
    {
        /// <summary>
        /// 区域名称
        /// </summary>
        public string RegionName { get; set; } = string.Empty;

        /// <summary>
        /// 视图类型
        /// </summary>
        public Type ViewType { get; set; } = null!;

        /// <summary>
        /// ViewModel类型
        /// </summary>
        public Type? ViewModelType { get; set; }

        /// <summary>
        /// 导航参数
        /// </summary>
        public object? Parameter { get; set; }

        /// <summary>
        /// 时间戳
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 保存的ViewModel状态
        /// </summary>
        public Dictionary<string, object>? ViewModelState { get; set; }
    }
}
