// ============================================================
// 文件: Services/CsvRecordFormatter.cs
// 描述: 正式检测记录 CSV 表头与数据行格式化工具
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System.Globalization;
using System.Text;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services;

/// <summary>
/// 集中维护正式 CSV 格式，确保表头和数据行始终使用同一列顺序。
/// </summary>
public static class CsvRecordFormatter
{
    /// <summary>
    /// 构建正式 CSV 表头。
    /// 方案版本必须紧跟方案名称，日期和时间保持在动态检测项目之后。
    /// </summary>
    public static string BuildHeader(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var headerBuilder = new StringBuilder();
        headerBuilder.Append("序号,系列,机种名称,序列号,方案名称,方案版本,检查者,综合判定");

        if (record.PinResults != null)
        {
            foreach (var pinResult in record.PinResults)
            {
                headerBuilder.Append(',');
                headerBuilder.Append(EscapeField(pinResult.PinName));
            }
        }

        headerBuilder.Append(",日期,时间");
        return headerBuilder.ToString();
    }

    /// <summary>
    /// 构建与 <see cref="BuildHeader"/> 完全对应的数据行。
    /// 电气测量值已经在上游转换为业务记录值，这里只负责列顺序和 CSV 转义。
    /// </summary>
    public static string BuildDataRow(LogRecord record, int rowIndex)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (rowIndex <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowIndex), "CSV 序号必须大于 0。");

        var dataBuilder = new StringBuilder();
        dataBuilder.Append(rowIndex);
        dataBuilder.Append(',');
        dataBuilder.Append(EscapeField(record.Series));
        dataBuilder.Append(',');
        dataBuilder.Append(EscapeField(record.MachineType));
        dataBuilder.Append(',');
        dataBuilder.Append(EscapeField(record.SerialNumber));
        dataBuilder.Append(',');
        dataBuilder.Append(EscapeField(record.PlanName));
        dataBuilder.Append(',');
        dataBuilder.Append(EscapeField(record.PlanVersionText));
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
        dataBuilder.Append(record.Timestamp.ToString("yyyy年M月d日", CultureInfo.InvariantCulture));
        dataBuilder.Append(',');
        dataBuilder.Append(record.Timestamp.ToString("HH时mm分ss秒", CultureInfo.InvariantCulture));

        return dataBuilder.ToString();
    }

    /// <summary>
    /// 对 CSV 字段执行最小必要转义，防止用户输入破坏列结构。
    /// </summary>
    public static string EscapeField(string? field)
    {
        if (string.IsNullOrEmpty(field))
            return string.Empty;

        return field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;
    }
}
