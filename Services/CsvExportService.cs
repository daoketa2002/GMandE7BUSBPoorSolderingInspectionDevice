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
    public class CsvExportService : ICsvExportService
    {
        private readonly ILogger<CsvExportService> _logger;

        public CsvExportService(ILogger<CsvExportService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> ExportWithDialogAsync<T>(
            IEnumerable<T> data,
            List<(string Header, Func<T, string> ValueSelector)> headers,
            string defaultFileName)
        {
            var dataList = data?.ToList();
            if (dataList == null || dataList.Count == 0)
            {
                MessageBox.Show("当前没有数据可导出！", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = defaultFileName,
                Title = "选择导出位置"
            };

            if (dialog.ShowDialog() != true)
            {
                _logger.LogInformation("用户取消了导出对话框");
                return false;
            }

            try
            {
                var csvContent = BuildCsv(dataList, headers);

                // 在写入文件时指定带BOM的UTF-8编码
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

        private string BuildCsv<T>(List<T> data, List<(string Header, Func<T, string> ValueSelector)> headers)
        {
            var sb = new StringBuilder();

            // 写入表头
            sb.AppendLine(string.Join(",", headers.Select(h => EscapeCsvField(h.Header))));

            // 写入数据行
            foreach (var item in data)
            {
                var row = headers.Select(h => EscapeCsvField(h.ValueSelector(item)));
                sb.AppendLine(string.Join(",", row));
            }

            return sb.ToString();
        }

        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return string.Empty;
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return field;
        }
    }
}