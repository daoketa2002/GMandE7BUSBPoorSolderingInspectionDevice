using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

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
        AssertEqual("OPEN", InspectionMeasurementEvaluator.ResolveContinuityState(10.5, 10.0));
        AssertEqual("SHORT", InspectionMeasurementEvaluator.ResolveContinuityState(0.5, 10.0));
        AssertEqual("OPEN", InspectionMeasurementEvaluator.ResolveContinuityState(10.0, 10.0));
    }),
    ("导通判定使用阈值和期望状态", () =>
    {
        AssertEqual("OK", InspectionMeasurementEvaluator.JudgeContinuityResult(10.5, "OPEN", 10.0));
        AssertEqual("NG", InspectionMeasurementEvaluator.JudgeContinuityResult(0.5, "OPEN", 10.0));
        AssertEqual("OK", InspectionMeasurementEvaluator.JudgeContinuityResult(0.5, "SHORT", 10.0));
        AssertEqual("NG", InspectionMeasurementEvaluator.JudgeContinuityResult(10.5, "SHORT", 10.0));
    }),
    ("半实物 DT302 旁路默认关闭且可显式开启（新名 SkipDt302Wait）", () =>
    {
        var config = new InspectionConfig();
        AssertEqual(false, config.SkipDt302Wait);

        config.SkipDt302Wait = true;
        AssertEqual(true, config.SkipDt302Wait);
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

        // 验证 InspectionStopReason 有 RelayTimeout
        var relayTimeout = InspectionStopReason.RelayTimeout;
        AssertEqual("RelayTimeout", relayTimeout.ToString());

        // 验证 InspectionStopReason 有 PlcStop
        var plcStop = InspectionStopReason.PlcStop;
        AssertEqual("PlcStop", plcStop.ToString());

        // 验证 InspectionStopReason 有 Reset
        var reset = InspectionStopReason.Reset;
        AssertEqual("Reset", reset.ToString());

        // 验证 InspectionStopReason 有 EmergencyStop
        var emStop = InspectionStopReason.EmergencyStop;
        AssertEqual("EmergencyStop", emStop.ToString());
    }),
    ("P0 control enums exist", () =>
    {
        AssertEqual("Resetting", InspectionControlAction.Resetting.ToString());
        AssertEqual("Timeout", InspectionStopWaitResult.Timeout.ToString());
    }),
    ("复位最终验证结果枚举存在", () =>
    {
        AssertEqual("Success", ResetCompletionValidationResult.Success.ToString());
        AssertEqual("StopSignalStillActive", ResetCompletionValidationResult.StopSignalStillActive.ToString());
        AssertEqual("PlcReadFailed", ResetCompletionValidationResult.PlcReadFailed.ToString());
    }),
    ("Fake 报警解除调试写入 DT303 后可被输入快照读到", () =>
    {
        var fake = new FakeInspectionHardware(NullLogger<FakeInspectionHardware>.Instance);

        var request = fake.RequestAlarmReleaseAsync().GetAwaiter().GetResult();
        AssertEqual(true, request.IsSuccess);

        var inputs = fake.ReadMachineInputsAsync().GetAwaiter().GetResult();
        AssertEqual(true, inputs.IsSuccess);
        AssertEqual(true, inputs.Value?.IsAlarmReleased);

        var clearEmergency = fake.ClearEmergencyStopRequestAsync().GetAwaiter().GetResult();
        var clearAlarm = fake.ClearAlarmReleasedAsync().GetAwaiter().GetResult();
        AssertEqual(true, clearEmergency.IsSuccess);
        AssertEqual(true, clearAlarm.IsSuccess);

        inputs = fake.ReadMachineInputsAsync().GetAwaiter().GetResult();
        AssertEqual(false, inputs.Value?.IsEmergencyStop);
        AssertEqual(false, inputs.Value?.IsAlarmReleased);
    }),
    ("急停专用停止会把引擎状态标记为急停", () =>
    {
        var fake = new FakeInspectionHardware(NullLogger<FakeInspectionHardware>.Instance);
        var engine = new InspectionEngine(
            NullLogger<InspectionEngine>.Instance,
            fake,
            fake);

        engine.StopForEmergencyStop();

        AssertEqual(InspectionState.PausedByEmergencyStop, engine.CurrentState);
    }),
    ("万用表异常值分类符合运行策略", () =>
    {
        // NaN → 中止，显示 "NaN"
        var nan = InspectionMeasurementEvaluator.Parse("NaN");
        AssertEqual("NaN", nan.DisplayTextOverride);
        AssertEqual(true, nan.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.NaN, nan.ValueKind);

        // -Infinity → 中止，显示 "-Infinity"
        var negInf = InspectionMeasurementEvaluator.Parse("-Infinity");
        AssertEqual("-Infinity", negInf.DisplayTextOverride);
        AssertEqual(true, negInf.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.NegativeInfinity, negInf.ValueKind);

        // 负电阻值 → 中止，无覆写（走数值格式化）
        var neg = InspectionMeasurementEvaluator.Parse("-1");
        AssertEqual(null, neg.DisplayTextOverride);
        AssertEqual(true, neg.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.NegativeResistance, neg.ValueKind);

        // +Infinity → 不中止，显示 "+Infinity"
        var posInf = InspectionMeasurementEvaluator.Parse("+Infinity");
        AssertEqual("+Infinity", posInf.DisplayTextOverride);
        AssertEqual(false, posInf.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.PositiveInfinityOrOverRange, posInf.ValueKind);

        // 超量程大数 → 不中止，显示 "超量程"
        var overRange = InspectionMeasurementEvaluator.Parse("9.9E37");
        AssertEqual("超量程", overRange.DisplayTextOverride);
        AssertEqual(false, overRange.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.PositiveInfinityOrOverRange, overRange.ValueKind);

        // OPEN → 正常，不中止
        var open = InspectionMeasurementEvaluator.Parse("OPEN");
        AssertEqual(MeasurementValueKind.Normal, open.ValueKind);
        AssertEqual(false, open.ShouldAbortInspection);
        AssertEqual(true, open.IsValid);

        // SHORT → 正常，不中止
        var shortVal = InspectionMeasurementEvaluator.Parse("SHORT");
        AssertEqual(MeasurementValueKind.Normal, shortVal.ValueKind);
        AssertEqual(false, shortVal.ShouldAbortInspection);
        AssertEqual(true, shortVal.IsValid);

        // 正常数值 → 正常，不中止
        var normal = InspectionMeasurementEvaluator.Parse("100.5");
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
