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
    ("半实物 DT302 旁路默认关闭且可显式开启", () =>
    {
        var config = new InspectionConfig();
        AssertEqual(false, config.BypassRelayActionCompletedForSemiPhysicalTest);

        config.BypassRelayActionCompletedForSemiPhysicalTest = true;
        AssertEqual(true, config.BypassRelayActionCompletedForSemiPhysicalTest);
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
