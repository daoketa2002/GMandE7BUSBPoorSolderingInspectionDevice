using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// CSV导出服务 — 将内存中的数据集合导出为CSV文件。
    /// 弹出「另存为」对话框让用户选择保存位置，导出使用带BOM的UTF-8编码。
    /// </summary>
    /// <remarks>
    /// <para><b>编码说明：</b>导出时使用 UTF-8 with BOM（new UTF8Encoding(true)），
    /// 确保 Excel / WPS 等工具直接打开时能正确识别中文，不会出现乱码。</para>
    /// <para><b>CSV格式：</b>遵循 RFC 4180 标准 — 逗号分隔，含特殊字符的字段用双引号包裹，引号本身用 "" 转义。</para>
    /// </remarks>
    public class CsvExportService : ICsvExportService
    {
        private readonly ILogger<CsvExportService> _logger;

        /// <summary>
        /// 初始化CSV导出服务。
        /// </summary>
        /// <param name="logger">日志记录器</param>
        public CsvExportService(ILogger<CsvExportService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        /// <remarks>
        /// 流程：检查数据 → 弹出保存对话框 → 构建CSV内容 → 写入文件。
        /// 写入操作在后台线程执行（Task.Run），避免阻塞UI。
        /// </remarks>
        public async Task<bool> ExportWithDialogAsync<T>(
            IEnumerable<T> data,
            List<(string Header, Func<T, string> ValueSelector)> headers,
            string defaultFileName)
        {
            // 数据列表化并检查是否为空
            var dataList = data?.ToList();
            if (dataList == null || dataList.Count == 0)
            {
                MessageBox.Show("当前没有数据可导出！", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            // 弹出保存对话框
            var dialog = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = defaultFileName,
                Title = "选择导出位置"
            };

            // 用户取消
            if (dialog.ShowDialog() != true)
            {
                _logger.LogInformation("用户取消了导出对话框");
                return false;
            }

            try
            {
                // 构建CSV文本内容
                var csvContent = BuildCsv(dataList, headers);

                // 在后台线程写入文件，使用带BOM的UTF-8编码
                await Task.Run(() => {
                    File.WriteAllText(dialog.FileName, csvContent, new UTF8Encoding(true));
                });

                _logger.LogInformation("成功导出 {Count} 条记录到: {Path}", dataList.Count, dialog.FileName);

                MessageBox.Show(
                    $"导出成功！\n共 {dataList.Count} 条记录\n保存至：{dialog.FileName}",
                    "导出完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出CSV失败");
                MessageBox.Show($"导出失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// 构建CSV文本内容（表头行 + 数据行）。
        /// </summary>
        /// <typeparam name="T">数据行类型</typeparam>
        /// <param name="data">数据行列表</param>
        /// <param name="headers">列头定义（列名 + 取值函数）</param>
        /// <returns>完整的CSV文本</returns>
        private string BuildCsv<T>(List<T> data, List<(string Header, Func<T, string> ValueSelector)> headers)
        {
            var sb = new StringBuilder();

            // 写入表头行 — 每个列名经过转义处理
            sb.AppendLine(string.Join(",", headers.Select(h => EscapeCsvField(h.Header))));

            // 写入数据行 — 逐行逐列取值并转义
            foreach (var item in data)
            {
                var row = headers.Select(h => EscapeCsvField(h.ValueSelector(item)));
                sb.AppendLine(string.Join(",", row));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 对CSV字段进行转义处理（RFC 4180）。
        /// 如果字段含逗号、双引号、换行符，则用双引号包裹，内部引号用 "" 转义。
        /// </summary>
        /// <param name="field">原始字段值</param>
        /// <returns>转义后的字段值</returns>
        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return string.Empty;

            // 仅当字段含特殊字符时才包裹引号
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return field;
        }
    }
}