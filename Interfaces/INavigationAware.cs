using System;
using System.Collections.Generic;
using System.Text;

namespace WPFStandardFramework.Interfaces
{
    /// <summary>
    /// 导航感知接口（合并了IParameterizedView的功能）
    /// 实现此接口的ViewModel可以接收导航事件
    /// </summary>
    public interface INavigationAware
    {
        /// <summary>
        /// 当导航到该页面时调用
        /// </summary>
        /// <param name="parameter">导航参数</param>
        Task OnNavigatedToAsync(object? parameter = null);

        /// <summary>
        /// 当从该页面导航离开时调用
        /// </summary>
        Task OnNavigatedFromAsync();

        /// <summary>
        /// 是否允许导航离开（可用于提示保存）
        /// </summary>
        Task<bool> CanNavigateFromAsync() => Task.FromResult(true);
    }
}
