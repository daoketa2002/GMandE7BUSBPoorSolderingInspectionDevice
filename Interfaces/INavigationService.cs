
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WPFStandardFramework.Common.Navigation;

namespace WPFStandardFramework.Interfaces
{
    /// <summary>
    /// 导航服务接口，提供视图导航功能
    /// </summary>
    public interface INavigationService
    {
        /// <summary>
        /// 导航到指定视图（默认区域 "Main"）
        /// </summary>
        Task NavigateToAsync<TView>(object? parameter = null) where TView : FrameworkElement;

        /// <summary>
        /// 导航到指定区域中的视图
        /// </summary>
        Task NavigateToAsync<TView>(string regionName, object? parameter = null) where TView : FrameworkElement;

        /// <summary>
        /// 导航到指定视图类型
        /// </summary>
        Task NavigateToAsync(Type viewType, string regionName, object? parameter = null);

        /// <summary>
        /// 注册一个导航区域（如主内容区、侧边栏等）
        /// </summary>
        void RegisterRegion(string regionName, ContentControl contentControl);

        /// <summary>
        /// 返回上一视图
        /// </summary>
        Task<bool> GoBackAsync();

        /// <summary>
        /// 返回指定区域的上一视图
        /// </summary>
        Task<bool> GoBackAsync(string regionName);

        /// <summary>
        /// 是否可以返回上一视图（任何区域）
        /// </summary>
        bool CanGoBackAny { get; }

        /// <summary>
        /// 指定区域是否可以返回上一视图
        /// </summary>
        bool CanGoBack(string regionName);

        /// <summary>
        /// 清除所有导航历史记录
        /// </summary>
        void ClearAllNavigationHistory();

        /// <summary>
        /// 清除指定区域的导航历史
        /// </summary>
        void ClearNavigationHistory(string regionName);

        /// <summary>
        /// 注册视图-ViewModel映射（手动配置）
        /// </summary>
        void RegisterViewMapping<TView, TViewModel>() where TView : FrameworkElement;

        /// <summary>
        /// 注册导航拦截器
        /// </summary>
        void RegisterInterceptor(INavigationInterceptor interceptor);

        /// <summary>
        /// 移除导航拦截器
        /// </summary>
        void RemoveInterceptor(INavigationInterceptor interceptor);

        /// <summary>
        /// 导航事件
        /// </summary>
        event EventHandler<NavigationEventArgs> Navigated;

        /// <summary>
        /// 导航状态变更事件
        /// </summary>
        event EventHandler<NavigationStateChangedEventArgs> NavigationStateChanged;
    }








}
