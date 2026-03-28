using System;
using System.Collections.Generic;
using System.Text;

namespace WPFStandardFramework.Common.Navigation
{
    /// <summary>
    /// 导航状态变更事件参数
    /// </summary>
    public class NavigationStateChangedEventArgs : EventArgs
    {
        public bool IsNavigating { get; }
        public bool CanGoBack { get; }
        public int NavigationStackCount { get; }

        public NavigationStateChangedEventArgs(bool isNavigating, bool canGoBack, int navigationStackCount)
        {
            IsNavigating = isNavigating;
            CanGoBack = canGoBack;
            NavigationStackCount = navigationStackCount;
        }
    }
}
