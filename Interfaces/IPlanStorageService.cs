using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 方案持久化存储服务接口
    /// </summary>
    public interface IPlanStorageService
    {
        /// <summary>
        /// 加载所有方案（从所有系列文件中）
        /// </summary>
        Task<List<PlanModel>> LoadAllPlansAsync();

        /// <summary>
        /// 保存方案（按系列合并到一个JSON文件）
        /// 如果是编辑模式，传入原系列名以处理系列变更
        /// </summary>
        Task SavePlanAsync(PlanModel plan, string? originalSeries = null);

        /// <summary>
        /// 删除方案
        /// </summary>
        Task DeletePlanAsync(string series, string model, string planName);

        /// <summary>
        /// 获取所有系列名称（去重）
        /// </summary>
        Task<List<string>> GetAllSeriesAsync();

        /// <summary>
        /// 根据系列获取型号列表（去重）
        /// </summary>
        Task<List<string>> GetModelsBySeriesAsync(string series);
    }
}