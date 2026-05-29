using System;
using System.Collections.Generic;
using System.Text;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation
{

    /// <summary>
    /// 导航事件参数
    /// </summary>
    public class NavigationEventArgs : EventArgs
    {
        public string RegionName { get; }
        public Type ViewType { get; }
        public object? Parameter { get; }
        public DateTime Timestamp { get; }

        public NavigationEventArgs(string regionName, Type viewType, object? parameter)
        {
            RegionName = regionName ?? throw new ArgumentNullException(nameof(regionName));
            ViewType = viewType ?? throw new ArgumentNullException(nameof(viewType));
            Parameter = parameter;
            Timestamp = DateTime.UtcNow;
        }
    }
}
