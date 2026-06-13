using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// 日志数据服务接口
    /// 定义从CSV日志文件加载、检索和解析日志数据的所有能力。
    /// </summary>
    public interface ILogDataService
    {
        /// <summary>
        /// 扫描日志数据文件夹，从所有CSV文件中提取可用的机种名称列表。
        /// </summary>
        /// <returns>去重且排序后的机种名称集合（如 "E78"、"GM5"）</returns>
        Task<List<string>> GetMachineTypesAsync();

        /// <summary>
        /// 加载日志数据文件夹下所有CSV文件的全部记录。
        /// </summary>
        /// <returns>所有日志数据模型的列表（含动态列）</returns>
        Task<List<LogDataModel>> LoadAllLogDataAsync();

        /// <summary>
        /// 根据机种名称、序列号、方案名称进行组合检索（AND 逻辑，模糊匹配）。
        /// </summary>
        /// <param name="source">数据源（通常为已加载的全部数据）</param>
        /// <param name="machineType">机种名称筛选条件（null 或空白表示忽略）</param>
        /// <param name="serialNumber">序列号筛选条件（null 或空白表示忽略）</param>
        /// <param name="planName">方案名称筛选条件（null 或空白表示忽略）</param>
        /// <returns>满足所有筛选条件的记录列表</returns>
        List<LogDataModel> Search(IEnumerable<LogDataModel> source,
            string? machineType, string? serialNumber, string? planName);

        /// <summary>
        /// 从给定数据集中提取所有不重复的动态列名（检测项名称）。
        /// </summary>
        /// <param name="data">已加载的日志数据</param>
        /// <returns>排序后的动态列名列表（如 "A1-A2"、"B4-B5"）</returns>
        List<string> GetAllDynamicHeaders(List<LogDataModel> data);
    }
}