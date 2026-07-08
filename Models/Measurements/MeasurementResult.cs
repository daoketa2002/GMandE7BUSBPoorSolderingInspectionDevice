using GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;

/// <summary>万用表单次测量结果</summary>
public class MeasurementResult
{
    public double Value { get; set; }
    public string RawValue { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>测量值分类，默认 Normal</summary>
    public MeasurementValueKind ValueKind { get; set; } = MeasurementValueKind.Normal;
    /// <summary>
    /// 是否应中止检测。
    /// true：NaN / -Infinity / 负电阻值 / 解析失败 → 当前项置 NG 后中止整轮。
    /// false：超量程大数 / +Infinity → 正常判 NG，按 ContinueTestingAfterNg 决定是否继续。
    /// </summary>
    public bool ShouldAbortInspection { get; set; }
    /// <summary>
    /// 显示文本覆写。
    /// 不为空时运行页检查结果列直接显示此值（如 "NG"），避免超长数字进入格式化。
    /// </summary>
    public string DisplayTextOverride { get; set; } = string.Empty;

    public override string ToString() => IsValid
        ? InputValidationHelper.FormatResistanceValue(Value)
        : $"Error: {ErrorMessage}";
}

/// <summary>测量事件参数</summary>
public class MeasurementEventArgs : EventArgs
{
    public MeasurementResult Result { get; }

    public MeasurementEventArgs(MeasurementResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }
}
