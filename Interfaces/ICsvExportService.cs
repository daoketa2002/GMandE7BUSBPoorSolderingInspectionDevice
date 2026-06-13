using System.Collections.Generic;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// CSV导出服务接口
    /// </summary>
    public interface ICsvExportService
    {
        /// <summary>
        /// 将数据导出为CSV文件（弹出保存对话框）
        /// </summary>
        /// <typeparam name="T">数据类型</typeparam>
        /// <param name="data">要导出的数据</param>
        /// <param name="headers">列头定义（列名, 值选择器）</param>
        /// <param name="defaultFileName">默认文件名</param>
        /// <returns>true=导出成功, false=用户取消或无数据</returns>
        Task<bool> ExportWithDialogAsync<T>(
            IEnumerable<T> data,
            List<(string Header, System.Func<T, string> ValueSelector)> headers,
            string defaultFileName);
    }
}