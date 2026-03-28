using System;
using System.Collections.Generic;
using System.Text;

namespace WPFStandardFramework.Interfaces
{
    /// <summary>
    /// 导航拦截器接口
    /// 用于在导航过程中插入自定义逻辑（如权限验证、日志记录等）
    /// </summary>
    public interface INavigationInterceptor
    {
        /// <summary>
        /// 是否可以导航到目标视图
        /// </summary>
        /// <param name="regionName">区域名称</param>
        /// <param name="targetViewType">目标视图类型</param>
        /// <param name="parameter">导航参数</param>
        /// <returns>true 允许导航，false 阻止导航</returns>
        Task<bool> CanNavigateAsync(string regionName, Type targetViewType, object? parameter);

        /// <summary>
        /// 导航即将开始
        /// </summary>
        /// <param name="regionName">区域名称</param>
        /// <param name="targetViewType">目标视图类型</param>
        /// <param name="parameter">导航参数</param>
        Task OnNavigatingAsync(string regionName, Type targetViewType, object? parameter);

        /// <summary>
        /// 导航完成
        /// </summary>
        /// <param name="regionName">区域名称</param>
        /// <param name="targetViewType">目标视图类型</param>
        /// <param name="parameter">导航参数</param>
        /// <param name="success">导航是否成功</param>
        Task OnNavigatedAsync(string regionName, Type targetViewType, object? parameter, bool success);
    }
}
