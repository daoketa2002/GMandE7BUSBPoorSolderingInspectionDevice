using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 全局作业员状态服务 - 管理当前选中的作业员
    /// 类似"当前登录用户"的概念
    /// </summary>
    public interface IOperatorStateService
    {
        /// <summary>
        /// 当前选中的作业员（null 表示未选择）
        /// </summary>
        OperatorModel? CurrentOperator { get; set; }

        /// <summary>
        /// 当前作业员名字（便捷属性）
        /// </summary>
        string CurrentOperatorName { get; }

        /// <summary>
        /// 是否已选择作业员
        /// </summary>
        bool HasOperator { get; }

        /// <summary>
        /// 默认作业员名字
        /// </summary>
        string DefaultOperatorName { get; }

        /// <summary>
        /// 当前作业员变更事件
        /// </summary>
        event EventHandler<OperatorModel?>? OperatorChanged;
    }
}