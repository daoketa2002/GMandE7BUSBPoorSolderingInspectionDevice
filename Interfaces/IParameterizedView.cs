using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WPFStandardFramework.Interfaces
{
    /// <summary>
    /// 支持参数传递的视图接口
    /// </summary>
    public interface IParameterizedView
    {
        /// <summary>
        /// 当导航到此视图时调用
        /// </summary>
        /// <param name="parameter">导航参数</param>
        Task OnNavigatedToAsync(object? parameter = null);
    }
}
