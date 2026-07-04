using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;

var tests = new List<(string Name, Action Body)>
{
    ("A组引脚映射为 1 到 12", () =>
    {
        AssertEqual((ushort)1, PlcAddressMap.ConvertPinNameToNumber("A1"));
        AssertEqual((ushort)12, PlcAddressMap.ConvertPinNameToNumber("A12"));
    }),
    ("B组引脚映射为 21 到 32", () =>
    {
        AssertEqual((ushort)21, PlcAddressMap.ConvertPinNameToNumber("B1"));
        AssertEqual((ushort)32, PlcAddressMap.ConvertPinNameToNumber("B12"));
    }),
    ("非法引脚抛出明确异常", () =>
    {
        AssertThrows<ArgumentException>(() => PlcAddressMap.ConvertPinNameToNumber("C1"));
        AssertThrows<ArgumentException>(() => PlcAddressMap.ConvertPinNameToNumber("A13"));
    }),
    ("极性按最小闭环文档映射", () =>
    {
        AssertEqual((ushort)0, PlcAddressMap.ConvertPolarityToValue(PinPolarityConstants.Positive));
        AssertEqual((ushort)1, PlcAddressMap.ConvertPolarityToValue(PinPolarityConstants.Negative));
        AssertThrows<ArgumentException>(() => PlcAddressMap.ConvertPolarityToValue("未知"));
    }),
    ("导通阈值按实测电阻转换 OPEN 或 SHORT", () =>
    {
        AssertEqual("OPEN", InspectionEngine.ResolveContinuityState(10.5, 10.0));
        AssertEqual("SHORT", InspectionEngine.ResolveContinuityState(0.5, 10.0));
        AssertEqual("OPEN", InspectionEngine.ResolveContinuityState(10.0, 10.0));
    }),
    ("导通判定使用阈值和期望状态", () =>
    {
        AssertEqual("OK", InspectionEngine.JudgeContinuityResult(10.5, "OPEN", 10.0));
        AssertEqual("NG", InspectionEngine.JudgeContinuityResult(0.5, "OPEN", 10.0));
        AssertEqual("OK", InspectionEngine.JudgeContinuityResult(0.5, "SHORT", 10.0));
        AssertEqual("NG", InspectionEngine.JudgeContinuityResult(10.5, "SHORT", 10.0));
    }),
    ("半实物 DT302 旁路默认关闭且可显式开启（新名 SkipDt302Wait）", () =>
    {
        var config = new InspectionConfig();
        AssertEqual(false, config.SkipDt302Wait);

        config.SkipDt302Wait = true;
        AssertEqual(true, config.SkipDt302Wait);
        AssertEqual(false, config.BypassRelayActionCompletedForSemiPhysicalTest); // 旧属性不受影响
    }),
    ("半实物 DT302 旁路配置优先读取新名并兼容旧名", () =>
    {
        AssertEqual(false, InspectionConfig.ResolveSkipDt302Wait(false, false));
        AssertEqual(true, InspectionConfig.ResolveSkipDt302Wait(true, false));
        AssertEqual(true, InspectionConfig.ResolveSkipDt302Wait(false, true));
        AssertEqual(true, InspectionConfig.ResolveSkipDt302Wait(true, true));
    }),
    ("单项 NG 后续默认继续测试", () =>
    {
        var config = new InspectionConfig();
        AssertEqual(true, config.ContinueTestingAfterNg);

        config.ContinueTestingAfterNg = false;
        AssertEqual(false, config.ContinueTestingAfterNg);
    }),
    ("StoppedBySingleItemNg 状态和停止原因枚举存在", () =>
    {
        // 验证 InspectionState 有 StoppedBySingleItemNg
        var state = InspectionState.StoppedBySingleItemNg;
        AssertEqual("StoppedBySingleItemNg", state.ToString());

        // 验证 InspectionStopReason 有 SingleItemNg
        var reason = InspectionStopReason.SingleItemNg;
        AssertEqual("SingleItemNg", reason.ToString());
    }),
    ("万用表异常值分类符合运行策略", () =>
    {
        // NaN → 中止，显示 NG
        var nan = InspectionEngine.ParseMeasurementForInspection("NaN");
        AssertEqual("NG", nan.DisplayTextOverride);
        AssertEqual(true, nan.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.NaN, nan.ValueKind);

        // -Infinity → 中止，显示 NG
        var negInf = InspectionEngine.ParseMeasurementForInspection("-Infinity");
        AssertEqual("NG", negInf.DisplayTextOverride);
        AssertEqual(true, negInf.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.NegativeInfinity, negInf.ValueKind);

        // 负电阻值 → 中止，显示 NG
        var neg = InspectionEngine.ParseMeasurementForInspection("-1");
        AssertEqual("NG", neg.DisplayTextOverride);
        AssertEqual(true, neg.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.NegativeResistance, neg.ValueKind);

        // +Infinity → 不中止，显示 NG
        var posInf = InspectionEngine.ParseMeasurementForInspection("+Infinity");
        AssertEqual("NG", posInf.DisplayTextOverride);
        AssertEqual(false, posInf.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.PositiveInfinityOrOverRange, posInf.ValueKind);

        // 超量程大数 → 不中止，显示 NG
        var overRange = InspectionEngine.ParseMeasurementForInspection("9.9E37");
        AssertEqual("NG", overRange.DisplayTextOverride);
        AssertEqual(false, overRange.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.PositiveInfinityOrOverRange, overRange.ValueKind);

        // OPEN → 正常，不中止
        var open = InspectionEngine.ParseMeasurementForInspection("OPEN");
        AssertEqual(MeasurementValueKind.Normal, open.ValueKind);
        AssertEqual(false, open.ShouldAbortInspection);
        AssertEqual(true, open.IsValid);

        // SHORT → 正常，不中止
        var shortVal = InspectionEngine.ParseMeasurementForInspection("SHORT");
        AssertEqual(MeasurementValueKind.Normal, shortVal.ValueKind);
        AssertEqual(false, shortVal.ShouldAbortInspection);
        AssertEqual(true, shortVal.IsValid);

        // 正常数值 → 正常，不中止
        var normal = InspectionEngine.ParseMeasurementForInspection("100.5");
        AssertEqual(MeasurementValueKind.Normal, normal.ValueKind);
        AssertEqual(false, normal.ShouldAbortInspection);
        AssertEqual(true, normal.IsValid);
    })
};

var failures = 0;

foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failures > 0)
{
    Environment.ExitCode = 1;
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"expected {expected}, actual {actual}");
    }
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"expected {typeof(TException).Name}, actual {ex.GetType().Name}");
    }

    throw new InvalidOperationException($"expected {typeof(TException).Name}, no exception");
}
