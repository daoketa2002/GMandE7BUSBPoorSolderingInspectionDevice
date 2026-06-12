using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 作业员持久化存储服务接口
    /// </summary>
    public interface IOperatorStorageService
    {
        /// <summary>
        /// 从文件加载所有作业员
        /// </summary>
        Task<List<OperatorModel>> LoadOperatorsAsync();

        /// <summary>
        /// 保存作业员列表到文件（完全覆写）
        /// </summary>
        Task SaveOperatorsAsync(List<OperatorModel> operators);

        /// <summary>
        /// 获取下一个可用 ID
        /// </summary>
        Task<int> GetNextIdAsync();
    }
}