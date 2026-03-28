using System;
using System.Collections.Generic;
using System.Text;

namespace WPFStandardFramework.Common.Navigation
{
    /// <summary>
    /// 指定视图对应的ViewModel类型
    /// 用于导航服务自动绑定ViewModel
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class NavigationViewModelAttribute : Attribute
    {
        /// <summary>
        /// ViewModel类型
        /// </summary>
        public Type ViewModelType { get; }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="viewModelType">ViewModel类型</param>
        public NavigationViewModelAttribute(Type viewModelType)
        {
            ViewModelType = viewModelType ?? throw new ArgumentNullException(nameof(viewModelType));
        }
    }

    /// <summary>
    /// 指定视图的缓存策略
    /// 用于优化视图创建性能
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ViewCachePolicyAttribute : Attribute
    {
        /// <summary>
        /// 缓存策略
        /// </summary>
        public ViewCachePolicy Policy { get; }

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="policy">缓存策略</param>
        public ViewCachePolicyAttribute(ViewCachePolicy policy)
        {
            Policy = policy;
        }
    }

    /// <summary>
    /// 视图缓存策略枚举
    /// </summary>
    public enum ViewCachePolicy
    {
        /// <summary>
        /// 每次都创建新实例（默认）
        /// 适用于频繁变化或包含临时数据的视图
        /// </summary>
        Transient,

        /// <summary>
        /// 缓存视图实例
        /// 适用于静态内容或需要保持状态的视图
        /// </summary>
        Cached,

        /// <summary>
        /// 懒加载（首次使用时创建）
        /// 适用于初始化开销大但不一定立即使用的视图
        /// </summary>
        Lazy
    }
}
