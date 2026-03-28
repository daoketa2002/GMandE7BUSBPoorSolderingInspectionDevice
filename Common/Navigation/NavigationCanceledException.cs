using System;
using System.Collections.Generic;
using System.Text;

namespace WPFStandardFramework.Common.Navigation
{
    /// <summary>
    /// 导航取消异常
    /// </summary>
    public class NavigationCanceledException : Exception
    {
        public NavigationCanceledException() : base("导航被用户取消") { }

        public NavigationCanceledException(string message) : base(message) { }

        public NavigationCanceledException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
