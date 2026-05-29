using System;
using System.Collections.Generic;
using System.Text;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 有状态ViewModel接口
    /// 实现此接口的ViewModel可以保存和恢复状态（用于导航历史）
    /// </summary>
    public interface IStatefulViewModel
    {
        /// <summary>
        /// 保存ViewModel状态
        /// </summary>
        /// <param name="state">状态字典</param>
        Task SaveStateAsync(IDictionary<string, object> state);

        /// <summary>
        /// 恢复ViewModel状态
        /// </summary>
        /// <param name="state">状态字典</param>
        Task RestoreStateAsync(IDictionary<string, object> state);
    }
}
