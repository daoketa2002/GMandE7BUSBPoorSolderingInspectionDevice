using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
#if DEBUG
using GMandE7BUSBPoorSolderingInspectionDevice.Services.Development;
#endif
using GMandE7BUSBPoorSolderingInspectionDevice.Services.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Fakes;
using Microsoft.Extensions.Configuration;
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
    ("NG 结果保存设置默认开启以兼容旧配置", () =>
    {
        var settings = new DeviceSettings();
        AssertEqual(true, settings.SaveNgInspectionResult);
    }),
    ("CSV 最近记录查询只命中指定机种和一个月内序列号", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-csv-exists-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = CreateCsvStorage(root, maxRowsPerFile: 50000);

            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-001", DateTime.Now.AddDays(-3))).GetAwaiter().GetResult();
            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-OLD", DateTime.Now.AddMonths(-2))).GetAwaiter().GetResult();
            storage.SaveRecordAsync(CreateLogRecord("E78", "P1", "SN-001", DateTime.Now.AddDays(-1))).GetAwaiter().GetResult();

            var start = DateTime.Today.AddMonths(-1);
            var end = DateTime.Now;

            AssertEqual(true, storage.ExistsRecentTestRecordAsync("GM", "SN-001", start, end).GetAwaiter().GetResult());
            AssertEqual(false, storage.ExistsRecentTestRecordAsync("GM", "SN-OLD", start, end).GetAwaiter().GetResult());
            AssertEqual(false, storage.ExistsRecentTestRecordAsync("GM", "SN-NONE", start, end).GetAwaiter().GetResult());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }),
    ("CSV sequential save keeps row index and rolls file", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-csv-row-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = CreateCsvStorage(root, maxRowsPerFile: 3);

            var timestamp = new DateTime(2026, 7, 10, 8, 0, 0);
            for (int i = 1; i <= 5; i++)
            {
                storage.SaveRecordAsync(CreateLogRecord("GM", "P1", $"SN-{i:000}", timestamp.AddSeconds(i)))
                    .GetAwaiter()
                    .GetResult();
            }

            var monthFolder = Path.Combine(root, "数据", "TestLog", "2026-07");
            var files = Directory.GetFiles(monthFolder, "GM_P1*.csv").OrderBy(x => x).ToList();
            AssertEqual(2, files.Count);

            var firstFileRows = ReadCsvDataRows(files[0]);
            var secondFileRows = ReadCsvDataRows(files[1]);
            AssertEqual(3, firstFileRows.Count);
            AssertEqual(2, secondFileRows.Count);

            AssertEqual("1", firstFileRows[0].Split(',')[0]);
            AssertEqual("2", firstFileRows[1].Split(',')[0]);
            AssertEqual("3", firstFileRows[2].Split(',')[0]);
            AssertEqual("1", secondFileRows[0].Split(',')[0]);
            AssertEqual("2", secondFileRows[1].Split(',')[0]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }),
    ("CSV 保存方案版本列且版本变化自动新分卷", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-csv-version-roll-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = CreateCsvStorage(root, maxRowsPerFile: 50000);
            var timestamp = new DateTime(2026, 7, 10, 8, 0, 0);

            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-V1", timestamp, planVersion: 1))
                .GetAwaiter()
                .GetResult();
            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-V2", timestamp.AddSeconds(1), planVersion: 2))
                .GetAwaiter()
                .GetResult();

            var monthFolder = Path.Combine(root, "数据", "TestLog", "2026-07");
            var files = Directory.GetFiles(monthFolder, "GM_P1*.csv").OrderBy(x => x).ToList();
            AssertEqual(2, files.Count);

            var firstLines = File.ReadAllLines(files[0]);
            var secondLines = File.ReadAllLines(files[1]);
            AssertEqual(true, firstLines[0].EndsWith(",方案版本", StringComparison.Ordinal));
            AssertEqual(true, firstLines[1].EndsWith(",V1", StringComparison.Ordinal));
            AssertEqual(true, secondLines[1].EndsWith(",V2", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }),
    ("CSV 格式化工具输出正式表头和数据行", () =>
    {
        var record = CreateLogRecord(
            "ZZTEST-索引机种A",
            "方案1",
            "ZZTEST-SN-202607-000001",
            new DateTime(2026, 7, 10, 8, 9, 10),
            planVersion: 2,
            pins: new[] { "A1-B1", "B2-B3" });

        var header = CsvRecordFormatter.BuildHeader(record);
        var row = CsvRecordFormatter.BuildDataRow(record, 7);

        AssertEqual("序号,机种名称,序列号,方案名称,检查者,综合判定,A1-B1,B2-B3,日期,时间,方案版本", header);
        AssertEqual("7,ZZTEST-索引机种A,ZZTEST-SN-202607-000001,方案1,测试员,OK,SHORT,10.5,2026年07月10日,08时09分10秒,V2", row);
    }),
#if DEBUG
    ("批量测试数据每个分卷保持固定列数版本和真实测量值", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-zztest-bulk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pathManager = CreateCsvPathManager(root, maxRowsPerFile: 50000);
            var indexService = new MonthlyLogIndexService(NullLogger<MonthlyLogIndexService>.Instance);
            var storage = CreateCsvStorageWithIndex(pathManager, indexService);
            var seeder = new TestDataSeeder(pathManager, storage, indexService, NullLogger<TestDataSeeder>.Instance);

            seeder.SeedBulkMonthAsync("2026-07", "ZZTEST-索引机种A", 1, 4500)
                .GetAwaiter()
                .GetResult();

            var monthFolder = Path.Combine(root, "数据", "TestLog", "2026-07");
            var files = Directory.GetFiles(monthFolder, "ZZTEST-索引机种A_方案1*.csv")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            AssertEqual(3, files.Count);

            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                var headerColumnCount = lines[0].Split(',').Length;
                var versions = new HashSet<string>(StringComparer.Ordinal);

                foreach (var line in lines.Skip(1))
                {
                    var columns = line.Split(',');
                    AssertEqual(headerColumnCount, columns.Length);
                    AssertEqual(true, columns[5] is "OK" or "NG");
                    AssertEqual(false, columns.Skip(6).Take(headerColumnCount - 9).Any(value => value is "OK" or "NG"));
                    versions.Add(columns[^1]);
                }

                AssertEqual(1, versions.Count);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }),
    ("开发数据清理只删除 ZZTEST CSV 并重建受影响月份索引", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-dev-clear-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pathManager = CreateCsvPathManager(root, maxRowsPerFile: 50000);
            var indexService = new MonthlyLogIndexService(NullLogger<MonthlyLogIndexService>.Instance);
            var storage = CreateCsvStorageWithIndex(pathManager, indexService);
            var seeder = new TestDataSeeder(
                pathManager,
                storage,
                indexService,
                NullLogger<TestDataSeeder>.Instance);

            var timestamp = new DateTime(2026, 7, 10, 8, 0, 0);
            storage.SaveRecordAsync(CreateLogRecord("ZZTEST-索引机种A", "方案1", "ZZTEST-SN-001", timestamp))
                .GetAwaiter()
                .GetResult();
            storage.SaveRecordAsync(CreateLogRecord("生产机种", "生产方案", "SN-001", timestamp.AddSeconds(1)))
                .GetAwaiter()
                .GetResult();

            seeder.ClearDevTestDataAsync().GetAwaiter().GetResult();

            var monthFolder = Path.Combine(root, "数据", "TestLog", "2026-07");
            AssertEqual(false, File.Exists(Path.Combine(monthFolder, "ZZTEST-索引机种A_方案1.csv")));
            AssertEqual(true, File.Exists(Path.Combine(monthFolder, "生产机种_生产方案.csv")));

            var indexEntries = indexService.ReadMonthIndexAsync(monthFolder).GetAwaiter().GetResult();
            AssertEqual(1, indexEntries.Count);
            AssertEqual("生产机种", indexEntries[0].MachineType);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }),
#endif
    ("机种名称和方案名称拒绝下划线", () =>
    {
        AssertEqual("机种名称不能包含下划线“_”，该字符用于测试数据文件名分隔。", NameValidationHelper.ValidateMachineType("测试_机种"));
        AssertEqual("方案名称不能包含下划线“_”，该字符用于测试数据文件名分隔。", NameValidationHelper.ValidatePlanName("方案_1"));
        AssertEqual<string?>(null, NameValidationHelper.ValidateMachineType(" ZZTEST-索引机种A "));
        AssertEqual<string?>(null, NameValidationHelper.ValidatePlanName(" 方案1 "));
    }),
    ("CSV 表头结构变化自动新分卷且旧记录按 V1 兼容解析", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-csv-header-roll-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = CreateCsvStorage(root, maxRowsPerFile: 50000);
            var timestamp = new DateTime(2026, 7, 10, 8, 0, 0);

            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-OLD", timestamp, planVersion: 1, pins: new[] { "A1-B1" }))
                .GetAwaiter()
                .GetResult();
            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-NEW", timestamp.AddSeconds(1), planVersion: 1, pins: new[] { "B1-B2" }))
                .GetAwaiter()
                .GetResult();

            var monthFolder = Path.Combine(root, "数据", "TestLog", "2026-07");
            var files = Directory.GetFiles(monthFolder, "GM_P1*.csv").OrderBy(x => x).ToList();
            AssertEqual(2, files.Count);

            var (records, total) = storage.QueryRecordsAsync(
                    series: "GM",
                    planName: "P1",
                    startDate: new DateTime(2026, 7, 1),
                    endDate: new DateTime(2026, 7, 31),
                    pageIndex: 1,
                    pageSize: 10)
                .GetAwaiter()
                .GetResult();

            AssertEqual(2, total);
            AssertEqual(2, records.Count);
            AssertEqual(1, records.Single(r => r.SerialNumber == "SN-OLD").PlanVersion);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }),
    ("动态列并集从索引命中文件表头计算", (Action)(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-csv-dynamic-headers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = CreateCsvStorage(root, maxRowsPerFile: 50000);
            var timestamp = new DateTime(2026, 7, 10, 8, 0, 0);

            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-H1", timestamp, planVersion: 1, pins: new[] { "A1-B1", "A2-B2" }))
                .GetAwaiter()
                .GetResult();
            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-H2", timestamp.AddSeconds(1), planVersion: 2, pins: new[] { "B1-B2", "A2-B2" }))
                .GetAwaiter()
                .GetResult();
            storage.SaveRecordAsync(CreateLogRecord("GM", "P2", "SN-H3", timestamp.AddSeconds(2), planVersion: 1, pins: new[] { "Z1-Z2" }))
                .GetAwaiter()
                .GetResult();

            var headers = storage.GetDynamicHeadersAsync(
                    series: "GM",
                    planName: "P1",
                    startDate: new DateTime(2026, 7, 1),
                    endDate: new DateTime(2026, 7, 31))
                .GetAwaiter()
                .GetResult();

            AssertEqual("A1-B1,A2-B2,B1-B2", string.Join(",", headers));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    })),
    ("索引导出查询返回全部命中记录不受 10000 条限制", (Action)(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-csv-export-all-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = CreateCsvStorage(root, maxRowsPerFile: 20000);
            var timestamp = new DateTime(2026, 7, 10, 8, 0, 0);

            for (int i = 1; i <= 10005; i++)
            {
                storage.SaveRecordAsync(CreateLogRecord("GM", "P1", $"SN-EXPORT-{i:00000}", timestamp.AddSeconds(i), planVersion: 1))
                    .GetAwaiter()
                    .GetResult();
            }

            var records = storage.QueryAllRecordsAsync(
                    series: "GM",
                    planName: "P1",
                    startDate: new DateTime(2026, 7, 1),
                    endDate: new DateTime(2026, 7, 31))
                .GetAwaiter()
                .GetResult();

            AssertEqual(10005, records.Count);
            AssertEqual("SN-EXPORT-10005", records[0].SerialNumber);
            AssertEqual("SN-EXPORT-00001", records[^1].SerialNumber);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    })),
    ("月度索引随正式 CSV 追加并可重建", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-csv-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = CreateCsvStorage(root, maxRowsPerFile: 50000);
            var timestamp = new DateTime(2026, 7, 10, 8, 0, 0);

            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-IDX-1", timestamp, planVersion: 3))
                .GetAwaiter()
                .GetResult();

            var monthFolder = Path.Combine(root, "数据", "TestLog", "2026-07");
            var indexPath = Path.Combine(monthFolder, "record-index.csv");
            AssertEqual(true, File.Exists(indexPath));
            var indexLines = File.ReadAllLines(indexPath);
            AssertEqual("Timestamp,MachineType,SerialNumber,PlanName,PlanVersion,FinalResult,FileName,RowNumber", indexLines[0]);
            AssertEqual(true, indexLines[1].Contains(",GM,SN-IDX-1,P1,3,OK,GM_P1.csv,1", StringComparison.Ordinal));

            File.Delete(indexPath);
            var indexService = new MonthlyLogIndexService(NullLogger<MonthlyLogIndexService>.Instance);
            indexService.RebuildMonthIndexAsync(monthFolder).GetAwaiter().GetResult();

            indexLines = File.ReadAllLines(indexPath);
            AssertEqual(2, indexLines.Length);
            AssertEqual(true, indexLines[1].Contains(",GM,SN-IDX-1,P1,3,OK,GM_P1.csv,1", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
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

static List<string> ReadCsvDataRows(string filePath)
{
    return File.ReadAllLines(filePath)
        .Skip(1)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToList();
}

static CsvTestRecordStorage CreateCsvStorage(string root, int maxRowsPerFile)
{
    return CreateCsvStorageWithIndex(
        CreateCsvPathManager(root, maxRowsPerFile),
        new MonthlyLogIndexService(NullLogger<MonthlyLogIndexService>.Instance));
}

static CsvStoragePathManager CreateCsvPathManager(string root, int maxRowsPerFile)
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CsvStorage:RootPath"] = root,
            ["CsvStorage:MaxRowsPerFile"] = maxRowsPerFile.ToString()
        })
        .Build();

    return new CsvStoragePathManager(
        new CsvStorageSettings(configuration),
        NullLogger<CsvStoragePathManager>.Instance);
}

static CsvTestRecordStorage CreateCsvStorageWithIndex(
    CsvStoragePathManager pathManager,
    MonthlyLogIndexService indexService)
{
    return new CsvTestRecordStorage(
        pathManager,
        new EmptyPlanStorageService(),
        indexService,
        NullLogger<CsvTestRecordStorage>.Instance);
}

static LogRecord CreateLogRecord(
    string machineType,
    string planName,
    string serialNumber,
    DateTime timestamp,
    int planVersion = 1,
    string[]? pins = null)
{
    pins ??= new[] { "A1-B1" };

    return new LogRecord
    {
        Timestamp = timestamp,
        Series = machineType,
        MachineType = machineType,
        SerialNumber = serialNumber,
        PlanName = planName,
        PlanVersion = planVersion,
        Operator = "测试员",
        FinalResult = "OK",
        PinResults = pins.Select((pin, index) => new PinResult
        {
            PinName = pin,
            Result = index % 2 == 0 ? "SHORT" : "10.5"
        }).ToList()
    };
}

sealed class EmptyPlanStorageService : IPlanStorageService
{
    public Task DeletePlanAsync(string machineType, string planName) => Task.CompletedTask;

    public Task<List<string>> GetAllMachineTypesAsync() => Task.FromResult(new List<string>());

    public Task<List<string>> GetPlanNamesByMachineTypeAsync(string machineType) => Task.FromResult(new List<string>());

    public Task<List<PlanModel>> LoadAllPlansAsync() => Task.FromResult(new List<PlanModel>());

    public Task SavePlanAsync(PlanModel plan, string? originalMachineType = null, string? originalPlanName = null) => Task.CompletedTask;
}
