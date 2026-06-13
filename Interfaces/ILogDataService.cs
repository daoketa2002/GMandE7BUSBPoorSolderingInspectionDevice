using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 日志数据服务接口
    /// </summary>
    public interface ILogDataService
    {
        /// <summary>
        /// 获取所有可用的机种名称
        /// </summary>
        Task<List<string>> GetMachineTypesAsync();

        /// <summary>
        /// 加载所有日志数据
        /// </summary>
        Task<List<LogDataModel>> LoadAllLogDataAsync();

        /// <summary>
        /// 组合检索
        /// </summary>
        List<LogDataModel> Search(IEnumerable<LogDataModel> source,
            string? machineType, string? serialNumber, string? planName);

        /// <summary>
        /// 获取所有不重复的动态列名
        /// </summary>
        List<string> GetAllDynamicHeaders(List<LogDataModel> data);
    }
}