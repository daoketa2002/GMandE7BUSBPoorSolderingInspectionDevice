using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 方案持久化存储服务接口
    /// 新结构：机种→方案 两级，一个方案一个独立JSON文件
    /// 目录：{方案根目录}/{机种}/{方案名}.json
    /// </summary>
    public interface IPlanStorageService
    {
        /// <summary>
        /// 加载所有方案（遍历所有机种文件夹）
        /// </summary>
        /// <returns>全部方案列表</returns>
        Task<List<PlanModel>> LoadAllPlansAsync();

        /// <summary>
        /// 保存方案（新增或更新）
        /// 如果是编辑模式且机种发生了变更，传入原机种名以处理文件移动
        /// </summary>
        /// <param name="plan">要保存的方案对象</param>
        /// <param name="originalMachineType">编辑前的原机种名（新增时为null）</param>
        Task SavePlanAsync(PlanModel plan, string? originalMachineType = null);

        /// <summary>
        /// 删除方案
        /// </summary>
        /// <param name="machineType">机种名称</param>
        /// <param name="planName">方案名称</param>
        Task DeletePlanAsync(string machineType, string planName);

        /// <summary>
        /// 获取所有机种名称（去重，来自文件夹名）
        /// </summary>
        /// <returns>机种名称列表</returns>
        Task<List<string>> GetAllMachineTypesAsync();

        /// <summary>
        /// 根据机种获取该机种下的所有方案列表
        /// </summary>
        /// <param name="machineType">机种名称</param>
        /// <returns>方案名称列表</returns>
        Task<List<string>> GetPlanNamesByMachineTypeAsync(string machineType);
    }
}