using System.Collections.Generic;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>
    /// CSV导出服务接口
    /// 定义将内存数据导出为CSV文件并弹出保存对话框的能力。
    /// </summary>
    /// <remarks>
    /// 导出使用带BOM的UTF-8编码，确保Excel等工具打开时中文不出现乱码。
    /// </remarks>
    public interface ICsvExportService
    {
        /// <summary>
        /// 将泛型数据集合导出为CSV文件。
        /// 内部弹出 SaveFileDialog 让用户选择保存位置。
        /// </summary>
        /// <typeparam name="T">数据行类型（如 LogRecord）</typeparam>
        /// <param name="data">要导出的数据行集合</param>
        /// <param name="headers">列头定义列表 — 每一项包含列名（Header）和从数据行取值的函数（ValueSelector）</param>
        /// <param name="defaultFileName">保存对话框中显示的默认文件名</param>
        /// <returns>true = 导出成功, false = 用户取消操作 或 数据为空</returns>
        Task<bool> ExportWithDialogAsync<T>(
            IEnumerable<T> data,
            List<(string Header, System.Func<T, string> ValueSelector)> headers,
            string defaultFileName);
    }
}