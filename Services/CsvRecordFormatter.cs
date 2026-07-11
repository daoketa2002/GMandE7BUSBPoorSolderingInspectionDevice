// ============================================================
// 文件: Services/CsvRecordFormatter.cs
// 描述: 正式检测记录 CSV 表头与数据行格式化工具
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System;
using System.Globalization;
using System.Text;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 集中维护正式 CSV 格式，避免正式保存链路和开发数据工具各写一套格式。
    /// </summary>
    public static class CsvRecordFormatter
    {
        public static string BuildHeader(LogRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            var headerBuilder = new StringBuilder();
            headerBuilder.Append("序号,机种名称,序列号,方案名称,检查者,综合判定");

            if (record.PinResults != null)
            {
                foreach (var pinResult in record.PinResults)
                {
                    headerBuilder.Append(',');
                    headerBuilder.Append(EscapeField(pinResult.PinName));
                }
            }

            headerBuilder.Append(",日期,时间,方案版本");
            return headerBuilder.ToString();
        }

        public static string BuildDataRow(LogRecord record, int rowIndex)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            if (rowIndex <= 0)
                throw new ArgumentOutOfRangeException(nameof(rowIndex), "CSV 序号必须大于 0。");

            var dataBuilder = new StringBuilder();

            dataBuilder.Append(rowIndex);
            dataBuilder.Append(',');
            dataBuilder.Append(EscapeField(!string.IsNullOrWhiteSpace(record.MachineType)
                ? record.MachineType
                : record.Series));
            dataBuilder.Append(',');
            dataBuilder.Append(EscapeField(record.SerialNumber));
            dataBuilder.Append(',');
            dataBuilder.Append(EscapeField(record.PlanName));
            dataBuilder.Append(',');
            dataBuilder.Append(EscapeField(record.Operator));
            dataBuilder.Append(',');
            dataBuilder.Append(EscapeField(record.FinalResult));

            if (record.PinResults != null)
            {
                foreach (var pinResult in record.PinResults)
                {
                    dataBuilder.Append(',');
                    dataBuilder.Append(EscapeField(pinResult.Result));
                }
            }

            dataBuilder.Append(',');
            dataBuilder.Append(record.Timestamp.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture));
            dataBuilder.Append(',');
            dataBuilder.Append(record.Timestamp.ToString("HH时mm分ss秒", CultureInfo.InvariantCulture));
            dataBuilder.Append(',');
            dataBuilder.Append($"V{Math.Max(1, record.PlanVersion)}");

            return dataBuilder.ToString();
        }

        public static string EscapeField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            return field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
                ? $"\"{field.Replace("\"", "\"\"")}\""
                : field;
        }
    }
}
