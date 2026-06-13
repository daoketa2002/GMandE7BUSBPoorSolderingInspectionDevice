using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 全局当前方案服务接口
    /// 维护当前选中的方案，供运行界面等模块使用
    /// </summary>
    public interface ICurrentPlanService
    {
        /// <summary>
        /// 当前选中的方案
        /// </summary>
        PlanModel? CurrentPlan { get; set; }

        /// <summary>
        /// 当前方案名称（便捷属性）
        /// </summary>
        string CurrentPlanName { get; }

        /// <summary>
        /// 是否已选择方案
        /// </summary>
        bool HasPlan { get; }

        /// <summary>
        /// 当前方案变更事件
        /// </summary>
        event EventHandler<PlanModel?>? CurrentPlanChanged;
    }
}