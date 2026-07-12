using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
#if DEBUG
using GMandE7BUSBPoorSolderingInspectionDevice.Services.Development;
#endif
using GMandE7BUSBPoorSolderingInspectionDevice.Services.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Fakes;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Multimeter;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.DeviceConnections;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Navigation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

var tests = new List<(string Name, Action Body)>
{
    ("启动拒绝记录来源原因和是否清除启动请求", () =>
    {
        var source = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "ViewModels",
            "TestPageViewModel.cs"));

        AssertEqual(true, source.Contains("bool clearStartRequest = true", StringComparison.Ordinal));
        AssertEqual(true, source.Contains(
            "Source={Source}, Reason={Reason}, ClearStartRequest={ClearStartRequest}, DiagnosticMessage={DiagnosticMessage}",
            StringComparison.Ordinal));
        AssertEqual(true, source.Contains("if (clearStartRequest)", StringComparison.Ordinal));
    }),
    ("调试启动在写入 DT120 前校验 PLC 离线并统一拒绝", () =>
    {
        var source = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "ViewModels",
            "TestPageViewModel.cs"));
        const string validation = "var validationResult = BuildStartValidationResult();";
        const string request = "var result = await _plcDevice.RequestStartAsync";

        AssertEqual(true, source.Contains(
            "await RejectStartAsync(validationResult, InspectionActionSource.DebugPanel, clearStartRequest: false);",
            StringComparison.Ordinal));
        AssertEqual(true, source.IndexOf(validation, StringComparison.Ordinal) < source.IndexOf(request, StringComparison.Ordinal));
    }),
    ("启动失败均回到统一拒绝出口且操作员提示不暴露 DT234", () =>
    {
        var source = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "ViewModels",
            "TestPageViewModel.cs"));

        AssertEqual(true, source.Contains(
            "CreateStartFailureResult(\"万用表无法通信，请检查网络连接后重试。\"", StringComparison.Ordinal));
        AssertEqual(true, source.Contains(
            "CreateStartFailureResult(\"启动允许信号发送失败，请检查 PLC 通信状态后重试。\"", StringComparison.Ordinal));
        AssertEqual(true, source.Contains(
            "await RejectStartAsync(CreateStartFailureResult(operatorMessage", StringComparison.Ordinal));
        AssertEqual(true, source.Contains(
            "启动请求发送失败，请检查 PLC 通信状态后重试。", StringComparison.Ordinal));
        AssertEqual(false, source.Contains("ShowWarningAsync($\"写 DT234 失败", StringComparison.Ordinal));
    }),
    ("设备连接失败计数区分健康检查和自动重连", () =>
    {
        var sourcePath = Path.Combine(
            Environment.CurrentDirectory,
            "Services",
            "DeviceConnectionService.cs");
        var source = File.ReadAllText(sourcePath);

        AssertEqual(true, source.Contains("ConsecutiveHealthFailures", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("ConsecutiveReconnectFailures", StringComparison.Ordinal));
        var retiredCounterName = string.Concat("Consecutive", "Failures");
        AssertEqual(false, source.Contains(retiredCounterName, StringComparison.Ordinal));
    }),
    ("DMM connection business state does not depend on TcpClient.Connected", () =>
    {
        using var driver = new GwInstekGDM9060Driver(NullLogger<GwInstekGDM9060Driver>.Instance);
        SetPrivateField(driver, "_isConnected", true);

        // TcpClient.Connected 不是实时健康探针；实际 I/O 异常会负责把业务状态改为断开。
        AssertEqual(true, driver.IsConnected);
    }),
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
    ("导通模式将正无穷和超量程按 OPEN 参与判定", () =>
    {
        foreach (var rawValue in new[] { "+Infinity", "9.9E37" })
        {
            var expectedOpen = new TestPointConfig
            {
                CheckMode = CheckModeConstants.Continuity,
                ModeValue = "OPEN"
            };
            AssertEqual("OK", InspectionMeasurementEvaluator.Judge(
                InspectionMeasurementEvaluator.Parse(rawValue), expectedOpen));
            AssertEqual("OPEN", expectedOpen.ActualContinuityState);

            var expectedShort = new TestPointConfig
            {
                CheckMode = CheckModeConstants.Continuity,
                ModeValue = "SHORT"
            };
            AssertEqual("NG", InspectionMeasurementEvaluator.Judge(
                InspectionMeasurementEvaluator.Parse(rawValue), expectedShort));
            AssertEqual("OPEN", expectedShort.ActualContinuityState);
        }
    }),
    ("电阻模式将正无穷和超量程保持判定 NG", () =>
    {
        foreach (var rawValue in new[] { "+Infinity", "9.9E37" })
        {
            var testPoint = new TestPointConfig
            {
                CheckMode = CheckModeConstants.Resistance,
                LowerLimit = 0,
                UpperLimit = 100
            };
            AssertEqual("NG", InspectionMeasurementEvaluator.Judge(
                InspectionMeasurementEvaluator.Parse(rawValue), testPoint));
        }
    }),
    ("运行页导通模式超量程显示 OPEN 而不是原始超量程文本", () =>
    {
        foreach (var rawValue in new[] { "+Infinity", "9.9E37" })
        {
            var testPoint = new TestPointConfig
            {
                CheckMode = CheckModeConstants.Continuity,
                ModeValue = "OPEN"
            };
            var measurement = InspectionMeasurementEvaluator.Parse(rawValue);

            AssertEqual("OK", InspectionMeasurementEvaluator.Judge(measurement, testPoint));
            AssertEqual("OPEN", InvokeFormatMeasurementResult(measurement, testPoint));
        }
    }),
    ("DMM Ping 失败时发布断线并清理业务连接状态", () =>
    {
        using var driver = new GwInstekGDM9060Driver(NullLogger<GwInstekGDM9060Driver>.Instance);
        SetPrivateField(driver, "_isConnected", true);

        bool eventRaised = false;
        bool? eventValue = null;
        driver.ConnectionStateChanged += (_, connected) =>
        {
            eventRaised = true;
            eventValue = connected;
        };

        AssertEqual(false, driver.PingAsync().GetAwaiter().GetResult());
        AssertEqual(false, driver.IsConnected);
        AssertEqual(true, eventRaised);
        AssertEqual(false, eventValue);
    }),
    ("启动校验允许任意顺序的一正一负极性", () =>
    {
        AssertEqual(true, ValidateStartPolarity(
            PinPolarityConstants.Positive, PinPolarityConstants.Negative).IsValid);
        AssertEqual(true, ValidateStartPolarity(
            PinPolarityConstants.Negative, PinPolarityConstants.Positive).IsValid);
    }),
    ("启动校验拒绝同极性和非法极性", () =>
    {
        AssertEqual(false, ValidateStartPolarity(
            PinPolarityConstants.Positive, PinPolarityConstants.Positive).IsValid);
        AssertEqual(false, ValidateStartPolarity(
            PinPolarityConstants.Negative, PinPolarityConstants.Negative).IsValid);
        AssertEqual(false, ValidateStartPolarity("未知", PinPolarityConstants.Negative).IsValid);
    }),
    ("方案仍在加载时启动校验必须拒绝", () =>
    {
        var config = new InspectionConfig
        {
            TestPoints = { CreateOpenContinuityTestPoint() }
        };

        var result = InspectionStartValidator.Validate(new InspectionStartValidationRequest
        {
            UiState = TestUIState.Ready,
            ModelName = "GM",
            SerialNumber = "SN-TEST",
            SchemeName = "启动状态方案",
            OperatorName = "测试员",
            IsPlcConnected = true,
            IsDmmConnected = true,
            IsPlanLoading = true,
            Config = config,
            UiItemCount = config.TestPoints.Count
        });

        AssertEqual(false, result.IsValid);
    }),
    ("真实模式运行日志隐藏 PLC 地址而调试模式保留", () =>
    {
        const string message = "DT120=1，等待 DT302，随后写入 DT304";

        string realModeText = InvokeToOperatorText(message, showTechnicalDetails: false);
        AssertEqual(false, realModeText.Contains("DT120", StringComparison.Ordinal));
        AssertEqual(false, realModeText.Contains("DT302", StringComparison.Ordinal));
        AssertEqual(false, realModeText.Contains("DT304", StringComparison.Ordinal));

        string debugModeText = InvokeToOperatorText(message, showTechnicalDetails: true);
        AssertEqual(message, debugModeText);
    }),
    ("阶段 C 主菜单和真实模式主操作按钮复用工业样式", () =>
    {
        var mainMenuXaml = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "Views",
            "MainMenuView.xaml"));
        var testPageXaml = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "Views",
            "TestPageView.xaml"));

        AssertEqual(true, mainMenuXaml.Contains("x:Key=\"IndustrialMenuButtonStyle\"", StringComparison.Ordinal));
        AssertEqual(true, mainMenuXaml.Contains("VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal));
        AssertEqual(false, mainMenuXaml.Contains("Width=\"350\"", StringComparison.Ordinal));

        AssertEqual(true, testPageXaml.Contains("x:Key=\"MainOperationButtonStyle\"", StringComparison.Ordinal));
        AssertEqual(true, testPageXaml.Contains("Style=\"{StaticResource MainOperationButtonStyle}\"", StringComparison.Ordinal));
        AssertEqual(true, testPageXaml.Contains("Content=\"复位(DT121)\"", StringComparison.Ordinal));
        AssertEqual(true, testPageXaml.Contains("Style=\"{StaticResource DebugButtonStyle}\"", StringComparison.Ordinal));
    }),
    ("主菜单按钮具有与终了按钮一致的软拟物光影层次", () =>
    {
        var mainMenuXaml = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "Views",
            "MainMenuView.xaml"));

        AssertEqual(true, mainMenuXaml.Contains("<LinearGradientBrush", StringComparison.Ordinal));
        AssertEqual(true, mainMenuXaml.Contains("x:Name=\"buttonHighlight\"", StringComparison.Ordinal));
        AssertEqual(true, mainMenuXaml.Contains("x:Name=\"buttonInnerStroke\"", StringComparison.Ordinal));
        AssertEqual(true, mainMenuXaml.Contains("x:Name=\"buttonShadow\"", StringComparison.Ordinal));
        AssertEqual(true, mainMenuXaml.Contains("TargetName=\"buttonContent\" Property=\"Margin\" Value=\"0,2,0,0\"", StringComparison.Ordinal));
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
    ("Settings page PLC test validates edited input even when production is connected", () =>
    {
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(new DeviceSettings()),
            new TestDeviceConnectionManager { PlcConnected = true });

        vm.Fp0hConfig.IpAddress = "999.999.999.999";

        InvokePrivateAsync(vm, "TestPlcConnectionAsync").GetAwaiter().GetResult();

        AssertEqual("Red", vm.PlcTestStatus);
    }),
    ("Settings page PLC test reuses production connection when target is unchanged", () =>
    {
        var savedSettings = new DeviceSettings
        {
            FP0HCommunication = new FP0HCommunicationConfig
            {
                IpAddress = "192.168.1.5",
                Port = 502,
                SlaveId = 1
            }
        };
        var deviceMgr = new TestDeviceConnectionManager { PlcConnected = true };
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(savedSettings),
            deviceMgr);

        InvokePrivateAsync(vm, "TestPlcConnectionAsync").GetAwaiter().GetResult();

        AssertEqual("Green", vm.PlcTestStatus);
        AssertEqual(0, deviceMgr.TestPlcConfigurationCallCount);
    }),
    ("Settings page PLC test uses temporary connection when IP changes", () =>
    {
        var savedSettings = new DeviceSettings
        {
            FP0HCommunication = new FP0HCommunicationConfig
            {
                IpAddress = "192.168.1.5",
                Port = 502,
                SlaveId = 1
            }
        };
        var deviceMgr = new TestDeviceConnectionManager { PlcConnected = true, TestPlcConfigurationResult = false };
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(savedSettings),
            deviceMgr);

        vm.Fp0hConfig.IpAddress = "192.168.1.99";

        InvokePrivateAsync(vm, "TestPlcConnectionAsync").GetAwaiter().GetResult();

        AssertEqual(1, deviceMgr.TestPlcConfigurationCallCount);
    }),
    ("Settings page PLC test uses temporary connection when SlaveId changes", () =>
    {
        var savedSettings = new DeviceSettings
        {
            FP0HCommunication = new FP0HCommunicationConfig
            {
                IpAddress = "192.168.1.5",
                Port = 502,
                SlaveId = 1
            }
        };
        var deviceMgr = new TestDeviceConnectionManager { PlcConnected = true, TestPlcConfigurationResult = false };
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(savedSettings),
            deviceMgr);

        vm.Fp0hConfig.SlaveId = 2;

        InvokePrivateAsync(vm, "TestPlcConnectionAsync").GetAwaiter().GetResult();

        AssertEqual(1, deviceMgr.TestPlcConfigurationCallCount);
    }),
    ("Settings page PLC test success message contains 输入配置测试成功", () =>
    {
        var savedSettings = new DeviceSettings
        {
            FP0HCommunication = new FP0HCommunicationConfig
            {
                IpAddress = "192.168.1.5",
                Port = 502,
                SlaveId = 1
            }
        };
        var deviceMgr = new TestDeviceConnectionManager { PlcConnected = true, TestPlcConfigurationResult = true };
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(savedSettings),
            deviceMgr);

        vm.Fp0hConfig.IpAddress = "192.168.1.99";

        InvokePrivateAsync(vm, "TestPlcConnectionAsync").GetAwaiter().GetResult();

        AssertEqual("Green", vm.PlcTestStatus);
        AssertEqual(true, vm.PlcTestMessage.Contains("输入配置测试成功"));
    }),
    ("Settings page DMM test validates edited input even when production is connected", () =>
    {
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(new DeviceSettings()),
            new TestDeviceConnectionManager { DmmConnected = true });

        vm.Gdm9060Config.IpAddress = "999.999.999.999";

        InvokePrivateAsync(vm, "TestDmmConnectionAsync").GetAwaiter().GetResult();

        AssertEqual("Red", vm.DmmTestStatus);
    }),
    ("Settings page DMM test reuses production connection when target is unchanged", () =>
    {
        var savedSettings = new DeviceSettings
        {
            GDM9060Communication = new GDM9060CommunicationConfig
            {
                IpAddress = "127.0.0.1",
                Port = 1
            }
        };
        var deviceMgr = new TestDeviceConnectionManager { DmmConnected = true };
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(savedSettings),
            deviceMgr);

        InvokePrivateAsync(vm, "TestDmmConnectionAsync").GetAwaiter().GetResult();

        AssertEqual("Green", vm.DmmTestStatus);
        AssertEqual(0, deviceMgr.TestDmmConfigurationCallCount);
    }),
    ("Settings page DMM test uses temp connection via DeviceConnectionManager when IP changes", () =>
    {
        var savedSettings = new DeviceSettings
        {
            GDM9060Communication = new GDM9060CommunicationConfig
            {
                IpAddress = "127.0.0.1",
                Port = 1
            }
        };
        var deviceMgr = new TestDeviceConnectionManager { DmmConnected = true, TestDmmConfigurationResult = "GDM-9060" };
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(savedSettings),
            deviceMgr);

        vm.Gdm9060Config.IpAddress = "192.168.1.99";

        InvokePrivateAsync(vm, "TestDmmConnectionAsync").GetAwaiter().GetResult();

        AssertEqual(1, deviceMgr.TestDmmConfigurationCallCount);
    }),
    ("Settings page DMM test success message contains 输入配置测试成功", () =>
    {
        var savedSettings = new DeviceSettings
        {
            GDM9060Communication = new GDM9060CommunicationConfig
            {
                IpAddress = "127.0.0.1",
                Port = 1
            }
        };
        var deviceMgr = new TestDeviceConnectionManager { DmmConnected = true, TestDmmConfigurationResult = "GDM-9060" };
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(savedSettings),
            deviceMgr);

        vm.Gdm9060Config.IpAddress = "192.168.1.99";

        InvokePrivateAsync(vm, "TestDmmConnectionAsync").GetAwaiter().GetResult();

        AssertEqual("Green", vm.DmmTestStatus);
        AssertEqual(true, vm.DmmTestMessage.Contains("输入配置测试成功"));
    }),
    ("Settings page scanner test validates edited input even when production is connected", () =>
    {
        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(new DeviceSettings()),
            new TestDeviceConnectionManager { ScannerConnected = true });

        vm.ScannerConfig.SerialNumber = "COM999";

        InvokePrivateAsync(vm, "TestScannerConnectionAsync").GetAwaiter().GetResult();

        AssertEqual("Red", vm.ScannerTestStatus);
    }),
    ("Settings page editable configs do not mutate saved settings snapshot", () =>
    {
        var savedSettings = new DeviceSettings
        {
            FP0HCommunication = new FP0HCommunicationConfig { IpAddress = "192.168.1.3" },
            GDM9060Communication = new GDM9060CommunicationConfig { IpAddress = "192.168.1.4" },
            ScannerSerialCommunication = new ScannerSerialCommunicationConfig { SerialNumber = "COM3" }
        };

        var vm = CreateSystemSettingsViewModel(
            new MemoryDeviceSettingsService(savedSettings),
            new TestDeviceConnectionManager());

        vm.Fp0hConfig.IpAddress = "192.168.99.99";
        vm.Gdm9060Config.IpAddress = "192.168.99.98";
        vm.ScannerConfig.SerialNumber = "COM8";

        AssertEqual("192.168.1.3", savedSettings.FP0HCommunication.IpAddress);
        AssertEqual("192.168.1.4", savedSettings.GDM9060Communication.IpAddress);
        AssertEqual("COM3", savedSettings.ScannerSerialCommunication.SerialNumber);
    }),
    ("P0 control enums exist", () =>
    {
        AssertEqual("Resetting", InspectionControlAction.Resetting.ToString());
        AssertEqual("Timeout", InspectionStopWaitResult.Timeout.ToString());
    }),
    ("设备健康检查结果能区分四种状态", () =>
    {
        AssertEqual(true, DeviceHealthCheckResult.Healthy().IsHealthy);
        AssertEqual(false, DeviceHealthCheckResult.Unhealthy("通信失败").IsHealthy);
        AssertEqual(DeviceHealthCheckStatus.SkippedBusy,
            DeviceHealthCheckResult.SkippedBusy("业务通信繁忙").Status);
        AssertEqual(DeviceHealthCheckStatus.Disabled,
            DeviceHealthCheckResult.Disabled("设备不主动心跳").Status);
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
        AssertEqual("NG", InspectionMeasurementEvaluator.Judge(nan, CreateOpenContinuityTestPoint()));

        // -Infinity → 中止，显示 "-Infinity"
        var negInf = InspectionMeasurementEvaluator.Parse("-Infinity");
        AssertEqual("-Infinity", negInf.DisplayTextOverride);
        AssertEqual(true, negInf.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.NegativeInfinity, negInf.ValueKind);
        AssertEqual("NG", InspectionMeasurementEvaluator.Judge(negInf, CreateOpenContinuityTestPoint()));

        // 负电阻值 → 中止，无覆写（走数值格式化）
        var neg = InspectionMeasurementEvaluator.Parse("-1");
        AssertEqual(null, neg.DisplayTextOverride);
        AssertEqual(true, neg.ShouldAbortInspection);
        AssertEqual(MeasurementValueKind.NegativeResistance, neg.ValueKind);
        AssertEqual("NG", InspectionMeasurementEvaluator.Judge(neg, CreateOpenContinuityTestPoint()));

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
    }),
    ("PLC 临时测试收到完整响应后，即使服务端保持连接，也立即成功返回", () =>
        RunPlcTesterKeepAliveTest()),
    ("PLC 临时测试处理分包响应", () =>
        RunPlcTesterPacketSplitTest()),
    ("PLC 临时测试异常响应返回 false", () =>
        RunPlcTesterExceptionResponseTest()),
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
    Environment.Exit(1);
}

Environment.Exit(0);

static InspectionValidationResult ValidateStartPolarity(string leftPolarity, string rightPolarity)
{
    // 仅构造满足其他启动条件的请求，使断言只覆盖极性规则。
    var config = new InspectionConfig
    {
        TestPoints =
        {
            new TestPointConfig
            {
                Name = "极性测试点",
                PinLeft = "A1",
                PinRight = "B1",
                PinLeftPolarity = leftPolarity,
                PinRightPolarity = rightPolarity,
                CheckMode = CheckModeConstants.Continuity,
                ModeValue = "OPEN"
            }
        }
    };

    return InspectionStartValidator.Validate(new InspectionStartValidationRequest
    {
        UiState = TestUIState.Ready,
        ModelName = "GM",
        SerialNumber = "SN-TEST",
        SchemeName = "极性校验方案",
        OperatorName = "测试员",
        IsPlcConnected = true,
        IsDmmConnected = true,
        Config = config,
        UiItemCount = config.TestPoints.Count
    });
}

static TestPointConfig CreateOpenContinuityTestPoint()
{
    return new TestPointConfig
    {
        CheckMode = CheckModeConstants.Continuity,
        ModeValue = "OPEN"
    };
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"expected {expected}, actual {actual}");
    }
}

static void SetPrivateField<TValue>(object target, string fieldName, TValue value)
{
    var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"找不到私有字段: {fieldName}");
    field.SetValue(target, value);
}

static string InvokeFormatMeasurementResult(MeasurementResult measurement, TestPointConfig testPoint)
{
    var method = typeof(TestPageViewModel).GetMethod(
        "FormatMeasurementResult",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 FormatMeasurementResult 方法");

    return (string)(method.Invoke(null, new object[] { measurement, testPoint })
        ?? throw new InvalidOperationException("FormatMeasurementResult 返回空值"));
}

static string InvokeToOperatorText(string message, bool showTechnicalDetails)
{
    var method = typeof(TestPageViewModel).GetMethod(
        "ToOperatorText",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 ToOperatorText 方法");

    return (string)(method.Invoke(null, new object[] { message, showTechnicalDetails })
        ?? throw new InvalidOperationException("ToOperatorText 返回空值"));
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

static SystemSettingsViewModel CreateSystemSettingsViewModel(
    IDeviceSettingsService settingsService,
    IDeviceConnectionManager deviceManager)
{
    var root = Path.Combine(Path.GetTempPath(), "gm-e78-settings-vm-" + Guid.NewGuid().ToString("N"));
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CsvStorage:RootPath"] = root,
            ["CsvStorage:MaxRowsPerFile"] = "50000"
        })
        .Build();

    var csvSettings = new CsvStorageSettings(configuration);
    var pathManager = new CsvStoragePathManager(
        csvSettings,
        NullLogger<CsvStoragePathManager>.Instance);

    return new SystemSettingsViewModel(
        new TestNavigationService(),
        new TestNotificationService(),
        settingsService,
        csvSettings,
        pathManager,
        configuration,
        new TestServiceProvider(),
        NullLoggerFactory.Instance,
        deviceManager);
}

static async Task InvokePrivateAsync(object target, string methodName)
{
    var method = target.GetType().GetMethod(
        methodName,
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"missing method {methodName}");

    if (method.Invoke(target, Array.Empty<object>()) is not Task task)
        throw new InvalidOperationException($"{methodName} did not return Task");

    await task.ConfigureAwait(false);
}

static int GetAvailableTcpPort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static void RunPlcTesterKeepAliveTest()
{
    var port = GetAvailableTcpPort();
    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();

    _ = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        using var stream = client.GetStream();
        var reqBuf = new byte[12];
        int total = 0;
        while (total < 12)
        {
            int read = await stream.ReadAsync(reqBuf.AsMemory(total)).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
        }

        ushort reqTid = (ushort)((reqBuf[0] << 8) | reqBuf[1]);
        byte reqUnitId = reqBuf[6];

        var resp = new byte[11];
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(0), reqTid);
        resp[2] = 0; resp[3] = 0;
        resp[4] = 0; resp[5] = 5;
        resp[6] = reqUnitId;
        resp[7] = 0x03;
        resp[8] = 0x02;
        resp[9] = 0; resp[10] = 0;

        await stream.WriteAsync(resp).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);

        await Task.Delay(2000).ConfigureAwait(false);
    });

    try
    {
        var tester = new PlcConnectionTester(NullLogger<PlcConnectionTester>.Instance);
        var sw = Stopwatch.StartNew();
        bool result = tester.TestAsync("127.0.0.1", port, 1, 5000).GetAwaiter().GetResult();
        sw.Stop();

        AssertEqual(true, result);
        AssertEqual(true, sw.ElapsedMilliseconds < 1000);
    }
    finally
    {
        listener.Stop();
    }
}

static void RunPlcTesterPacketSplitTest()
{
    var port = GetAvailableTcpPort();
    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();

    _ = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        using var stream = client.GetStream();
        var reqBuf = new byte[12];
        int total = 0;
        while (total < 12)
        {
            int read = await stream.ReadAsync(reqBuf.AsMemory(total)).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
        }

        ushort reqTid = (ushort)((reqBuf[0] << 8) | reqBuf[1]);
        byte reqUnitId = reqBuf[6];

        var mbap = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(mbap.AsSpan(0), reqTid);
        mbap[2] = 0; mbap[3] = 0;
        mbap[4] = 0; mbap[5] = 5;
        await stream.WriteAsync(mbap).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false);

        var body = new byte[5];
        body[0] = reqUnitId;
        body[1] = 0x03;
        body[2] = 0x02;
        body[3] = 0; body[4] = 0;
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);

        await Task.Delay(2000).ConfigureAwait(false);
    });

    try
    {
        var tester = new PlcConnectionTester(NullLogger<PlcConnectionTester>.Instance);
        bool result = tester.TestAsync("127.0.0.1", port, 1, 5000).GetAwaiter().GetResult();
        AssertEqual(true, result);
    }
    finally
    {
        listener.Stop();
    }
}

static void RunPlcTesterExceptionResponseTest()
{
    var port = GetAvailableTcpPort();
    using var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();

    _ = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        using var stream = client.GetStream();
        var reqBuf = new byte[12];
        int total = 0;
        while (total < 12)
        {
            int read = await stream.ReadAsync(reqBuf.AsMemory(total)).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
        }

        ushort reqTid = (ushort)((reqBuf[0] << 8) | reqBuf[1]);
        byte reqUnitId = reqBuf[6];

        var resp = new byte[9];
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(0), reqTid);
        resp[2] = 0; resp[3] = 0;
        resp[4] = 0; resp[5] = 3;
        resp[6] = reqUnitId;
        resp[7] = 0x83;
        resp[8] = 0x02;

        await stream.WriteAsync(resp).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    });

    try
    {
        var tester = new PlcConnectionTester(NullLogger<PlcConnectionTester>.Instance);
        bool result = tester.TestAsync("127.0.0.1", port, 1, 5000).GetAwaiter().GetResult();
        AssertEqual(false, result);
    }
    finally
    {
        listener.Stop();
    }
}

sealed class MemoryDeviceSettingsService : IDeviceSettingsService
{
    public MemoryDeviceSettingsService(DeviceSettings settings)
    {
        Settings = settings;
    }

    public DeviceSettings Settings { get; private set; }

    public DeviceSettings LoadSettings() => Settings;

    public void SaveSettings(DeviceSettings settings) => Settings = settings;
}

sealed class TestDeviceConnectionManager : IDeviceConnectionManager
{
    public bool PlcConnected { get; set; }
    public bool DmmConnected { get; set; }
    public bool ScannerConnected { get; set; }

    public bool IsPlcConnected => PlcConnected;
    public bool IsDmmConnected => DmmConnected;
    public bool IsScannerConnected => ScannerConnected;
    public bool AreAllDevicesReady => PlcConnected && DmmConnected && ScannerConnected;
    public string PlcStatusText => PlcConnected ? "已连接" : "未连接";
    public string DmmStatusText => DmmConnected ? "已连接" : "未连接";
    public string ScannerStatusText => ScannerConnected ? "已连接" : "未连接";
    public DeviceConnectionStatus PlcStatus => PlcConnected ? DeviceConnectionStatus.Connected : DeviceConnectionStatus.Disconnected;
    public DeviceConnectionStatus DmmStatus => DmmConnected ? DeviceConnectionStatus.Connected : DeviceConnectionStatus.Disconnected;
    public DeviceConnectionStatus ScannerStatus => ScannerConnected ? DeviceConnectionStatus.Connected : DeviceConnectionStatus.Disconnected;

    public int TestPlcConfigurationCallCount { get; private set; }
    public int TestDmmConfigurationCallCount { get; private set; }
    public bool TestPlcConfigurationResult { get; set; }
    public string? TestDmmConfigurationResult { get; set; }

    public event EventHandler<DeviceConnectionStateChangedEventArgs>? PlcConnectionStateChanged { add { } remove { } }
    public event EventHandler<DeviceConnectionStateChangedEventArgs>? DmmConnectionStateChanged { add { } remove { } }
    public event EventHandler<DeviceConnectionStateChangedEventArgs>? ScannerConnectionStateChanged { add { } remove { } }
    public event EventHandler<BarcodeParsedEventArgs>? BarcodeScanned { add { } remove { } }
    public event EventHandler<bool>? AllDevicesReadyChanged { add { } remove { } }

    public Task StartAllAsync() => Task.CompletedTask;
    public Task StopAllAsync() => Task.CompletedTask;
    public Task ReconnectDeviceAsync(string deviceType) => Task.CompletedTask;
    public Task ConnectDeviceAsync(string deviceType) => Task.CompletedTask;
    public Task DisconnectDeviceAsync(string deviceType) => Task.CompletedTask;
    public Task<DeviceReconnectSummary> ApplySettingsAndReconnectAsync(bool reconnectPlc, bool reconnectDmm, bool reconnectScanner)
        => Task.FromResult(new DeviceReconnectSummary(
            new DeviceReconnectResult(DeviceTypeNames.Plc, reconnectPlc, PlcConnected, PlcStatusText),
            new DeviceReconnectResult(DeviceTypeNames.Dmm, reconnectDmm, DmmConnected, DmmStatusText),
            new DeviceReconnectResult(DeviceTypeNames.Scanner, reconnectScanner, ScannerConnected, ScannerStatusText)));

    public Task<bool> TestPlcConfigurationAsync(FP0HCommunicationConfig config, CancellationToken ct = default)
    {
        TestPlcConfigurationCallCount++;
        return Task.FromResult(TestPlcConfigurationResult);
    }

    public Task<string?> TestDmmConfigurationAsync(GDM9060CommunicationConfig config, CancellationToken ct = default)
    {
        TestDmmConfigurationCallCount++;
        return Task.FromResult(TestDmmConfigurationResult);
    }
}

sealed class TestNotificationService : INotificationService
{
    public string? LastInfo { get; private set; }
    public string? LastWarning { get; private set; }
    public string? LastError { get; private set; }

    public void ShowInfo(string message, string title = "信息") => LastInfo = message;
    public void ShowWarning(string message, string title = "警告") => LastWarning = message;
    public void ShowError(string message, string title = "错误") => LastError = message;
    public Task<bool> ConfirmAsync(string message, string title = "确认") => Task.FromResult(true);
    public Task ShowInfoAsync(string message, string title = "信息") { LastInfo = message; return Task.CompletedTask; }
    public Task ShowWarningAsync(string message, string title = "警告") { LastWarning = message; return Task.CompletedTask; }
    public Task ShowErrorAsync(string message, string title = "错误") { LastError = message; return Task.CompletedTask; }
}

sealed class TestNavigationService : INavigationService
{
    public bool CanGoBackAny => false;
    public event EventHandler<NavigationEventArgs>? Navigated { add { } remove { } }
    public event EventHandler<NavigationStateChangedEventArgs>? NavigationStateChanged { add { } remove { } }

    public Task NavigateToAsync<TView>(object? parameter = null) where TView : FrameworkElement => Task.CompletedTask;
    public Task NavigateToAsync<TView>(string regionName, object? parameter = null) where TView : FrameworkElement => Task.CompletedTask;
    public Task NavigateToAsync(Type viewType, string regionName, object? parameter = null) => Task.CompletedTask;
    public void RegisterRegion(string regionName, ContentControl contentControl) { }
    public Task<bool> GoBackAsync() => Task.FromResult(false);
    public Task<bool> GoBackAsync(string regionName) => Task.FromResult(false);
    public bool CanGoBack(string regionName) => false;
    public void ClearAllNavigationHistory() { }
    public void ClearNavigationHistory(string regionName) { }
    public void RegisterViewMapping<TView, TViewModel>() where TView : FrameworkElement { }
    public void RegisterInterceptor(INavigationInterceptor interceptor) { }
    public void RemoveInterceptor(INavigationInterceptor interceptor) { }
}

sealed class TestServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

sealed class EmptyPlanStorageService : IPlanStorageService
{
    public Task DeletePlanAsync(string machineType, string planName) => Task.CompletedTask;

    public Task<List<string>> GetAllMachineTypesAsync() => Task.FromResult(new List<string>());

    public Task<List<string>> GetPlanNamesByMachineTypeAsync(string machineType) => Task.FromResult(new List<string>());

    public Task<List<PlanModel>> LoadAllPlansAsync() => Task.FromResult(new List<PlanModel>());

    public Task SavePlanAsync(PlanModel plan, string? originalMachineType = null, string? originalPlanName = null) => Task.CompletedTask;
}
