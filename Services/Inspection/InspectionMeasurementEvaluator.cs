using System.Globalization;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.Inspection;

/// <summary>
/// 检测测量值评估器（纯静态工具类）。
/// 负责原始值解析、特殊值分类、导通/电阻判定。
/// 第一轮不抽接口。
/// </summary>
public static class InspectionMeasurementEvaluator
{
    private const double TemporaryOpenThresholdOhm = 1_000_000.0;

    /// <summary>
    /// 解析万用表原始文本并分类测量结果。
    /// 按无效测量值方案分类，填充 ValueKind / ShouldAbortInspection / DisplayTextOverride。
    /// </summary>
    public static MeasurementResult Parse(string rawText)
    {
        string text = rawText.Trim();

        if (string.Equals(text, "OPEN", StringComparison.OrdinalIgnoreCase))
            return new MeasurementResult { RawValue = rawText, Value = TemporaryOpenThresholdOhm, IsValid = true, ValueKind = MeasurementValueKind.Normal };

        if (string.Equals(text, "SHORT", StringComparison.OrdinalIgnoreCase))
            return new MeasurementResult { RawValue = rawText, Value = 0, IsValid = true, ValueKind = MeasurementValueKind.Normal };

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = 0,
                IsValid = false,
                ValueKind = MeasurementValueKind.ParseFailed,
                ShouldAbortInspection = true,
                ErrorMessage = $"无法解析万用表返回值：{rawText}",
                DisplayTextOverride = rawText
            };
        }

        if (double.IsNaN(value))
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.NaN,
                ShouldAbortInspection = true,
                DisplayTextOverride = "NaN"
            };

        if (double.IsNegativeInfinity(value))
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.NegativeInfinity,
                ShouldAbortInspection = true,
                DisplayTextOverride = "-Infinity"
            };

        if (double.IsPositiveInfinity(value))
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.PositiveInfinityOrOverRange,
                ShouldAbortInspection = false,
                DisplayTextOverride = "+Infinity"
            };

        if (value < 0)
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.NegativeResistance,
                ShouldAbortInspection = true,
                DisplayTextOverride = null
            };

        if (value > InputValidationHelper.ResistanceMaxValue)
            return new MeasurementResult
            {
                RawValue = rawText,
                Value = value,
                IsValid = true,
                ValueKind = MeasurementValueKind.PositiveInfinityOrOverRange,
                ShouldAbortInspection = false,
                DisplayTextOverride = "超量程"
            };

        return new MeasurementResult
        {
            RawValue = rawText,
            Value = value,
            IsValid = true,
            ValueKind = MeasurementValueKind.Normal
        };
    }

    /// <summary>
    /// 判定单个测量结果是 OK 还是 NG。
    /// 超量程大数 / +Infinity 直接判 NG，不进入阈值比较。
    /// </summary>
    public static string Judge(MeasurementResult measurement, TestPointConfig testPoint)
    {
        if (measurement.ValueKind == MeasurementValueKind.PositiveInfinityOrOverRange)
            return "NG";

        string raw = measurement.RawValue.Trim();

        if (testPoint.CheckMode == CheckModeConstants.Resistance)
        {
            if (raw.Equals("OPEN", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("SHORT", StringComparison.OrdinalIgnoreCase))
                return "NG";

            double lower = testPoint.LowerLimit ?? 0;
            double upper = testPoint.UpperLimit ?? double.MaxValue;
            return measurement.Value >= lower && measurement.Value <= upper ? "OK" : "NG";
        }

        bool expectShort = string.Equals(testPoint.ModeValue, "SHORT", StringComparison.OrdinalIgnoreCase);
        if (raw.Equals("SHORT", StringComparison.OrdinalIgnoreCase))
        {
            testPoint.ActualContinuityState = "SHORT";
            return expectShort ? "OK" : "NG";
        }
        if (raw.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
        {
            testPoint.ActualContinuityState = "OPEN";
            return expectShort ? "NG" : "OK";
        }

        testPoint.ActualContinuityState = ResolveContinuityState(
            measurement.Value, testPoint.ContinuityThresholdOhm);
        return JudgeContinuityResult(
            measurement.Value, testPoint.ModeValue, testPoint.ContinuityThresholdOhm);
    }

    /// <summary>根据实测电阻值和阈值判定导通状态</summary>
    public static string ResolveContinuityState(double resistanceOhm, double thresholdOhm)
    {
        if (thresholdOhm < 1.0 || thresholdOhm > 1000.0)
            throw new ArgumentOutOfRangeException(nameof(thresholdOhm), "导通阈值必须在 1~1000Ω 范围内。");

        return resistanceOhm < thresholdOhm ? "SHORT" : "OPEN";
    }

    /// <summary>根据实测电阻值和期望导通状态判定 OK/NG</summary>
    public static string JudgeContinuityResult(double resistanceOhm, string? expectedState, double thresholdOhm)
    {
        string actualState = ResolveContinuityState(resistanceOhm, thresholdOhm);
        string expected = NormalizeContinuityState(expectedState);
        return string.Equals(actualState, expected, StringComparison.OrdinalIgnoreCase) ? "OK" : "NG";
    }

    private static string NormalizeContinuityState(string? state)
        => string.Equals(state, "SHORT", StringComparison.OrdinalIgnoreCase) ? "SHORT" : "OPEN";

    /// <summary>构造失败测量结果</summary>
    public static MeasurementResult Failed(string message, string rawValue = "")
    {
        return new MeasurementResult
        {
            RawValue = rawValue,
            IsValid = false,
            ErrorMessage = message,
            Timestamp = DateTime.Now
        };
    }

    /// <summary>标记测试点为 NG</summary>
    public static void MarkNg(TestPointConfig testPoint, string message)
    {
        testPoint.Judgment = "NG";
        testPoint.ActualValue = 0;
        testPoint.IsTested = true;
        testPoint.ErrorMessage = message;
    }
}
