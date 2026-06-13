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
        /// 扫描日志数据文件夹，从所有CSV文件名中提取机种名称列表。
        /// 文件名格式：机种名_序列号_方案名称.csv（如 E78_SN20261001_SchemeV1.csv）
        /// </summary>
        /// <returns>去重且排序后的机种名称集合（如 "E78"、"GM5"）</returns>
        Task<List<string>> GetMachineTypesAsync();

        /// <summary>
        /// 根据检索条件加载匹配的CSV文件。
        /// 匹配依据是文件名（格式：机种名_序列号_方案名称.csv），三个条件为 AND 关系（模糊匹配），空白条件视为忽略。
        /// 所有条件均为空时加载全部文件。
        /// </summary>
        /// <param name="machineType">机种名称筛选（null/空白=忽略）</param>
        /// <param name="serialNumber">序列号筛选（null/空白=忽略）</param>
        /// <param name="planName">方案名称筛选（null/空白=忽略）</param>
        /// <returns>匹配文件的全部数据行</returns>
        Task<List<LogDataModel>> LoadFilteredLogDataAsync(
            string? machineType = null,
            string? serialNumber = null,
            string? planName = null);

        /// <summary>
        /// 从给定数据集中提取所有不重复的动态列名（检测项名称）。
        /// 保持首次出现顺序（即CSV文件中的原始列顺序）。
        /// </summary>
        /// <param name="data">已加载的日志数据</param>
        /// <returns>动态列名列表（如 "A1-A2"、"B4-B5"）</returns>
        List<string> GetAllDynamicHeaders(List<LogDataModel> data);
    }
}