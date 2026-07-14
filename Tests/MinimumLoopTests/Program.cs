using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig;
using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Common.Logging;
using GMandE7BUSBPoorSolderingInspectionDevice.Services;
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
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

// 测试程序可能运行在系统默认代码页下，显式使用 UTF-8 避免中文测试结果乱码。
Console.OutputEncoding = new UTF8Encoding(false);

var tests = new List<(string Name, Action Body)>
{
    ("REF-CLEAN formal source contract", TestRefClean01ForbiddenSourceContract),
    ("测试控制台使用 UTF-8 输出编码", TestConsoleOutputEncodingIsUtf8),
    ("Stage D semi-physical logging contract", TestStageDSemiPhysicalLogContract),
    ("阶段 E Fake 日志前缀和模拟返回契约", TestStageEFakeLogContract),
    ("阶段 F 全局异常处理器只注册一次", TestStageFGlobalExceptionContract),
    ("日志运行模式按 Fake 优先、半实物次之、默认真实解析", TestRunModeResolution),
    ("阶段 A 正常日志不再使用 Warning", TestStageALogLevels),
    ("DMM 空响应中止测量且不返回完成结果", TestDmmEmptyResponseThrows),
    ("DMM 主动取消向调用方继续抛出取消", TestDmmCancellationIsNotTimeout),
    ("DMM P1 Starting 复核将取消令牌传给 Ping", TestP1StartingPingUsesStartingToken),
    ("DMM P1 查询写入后取消会标记接收缓冲区污染", TestP1CancellationAfterWriteMarksBufferDirty),
    ("DMM P2 正常查询完成后污染状态保持干净", TestP2NormalResponseKeepsBufferClean),
    ("DMM P1 断开连接会清除接收缓冲区污染标记", TestP1DisconnectClearsBufferDirty),
    ("DMM P2 污染状态下查询前清理迟到响应", TestP2DirtyBufferIsDrainedBeforeMeasurement),
    ("DMM P2 模式缓存命中时仍清理迟到响应", TestP2ModeCacheHitStillDrainsDirtyBuffer),
    ("DMM P2 测量收到身份响应后最多重试一次", TestP2MeasurementIdentityResponseRetriesOnce),
    ("DMM P2 连续身份响应会安全中止", TestP2PersistentIdentityResponseAborts),
    ("DMM P2 非法测量文本不会交给业务解析", TestP2InvalidMeasurementResponseAborts),
    ("DMM P2 身份和 OPC 响应类型必须匹配查询", TestP2QueryResponseTypeValidation),
    ("DMM P2 缓冲区清理使用有限静默确认", TestP2DrainUsesBoundedQuietChecks),
    ("DMM P3 Debug 串台注入钩子默认关闭且仅限 Debug", TestP3DebugHookIsScopedAndOffByDefault),
    ("DMM P3 Debug 串台注入后可确定性恢复", TestP3InjectedIdentityResponseRecovers),
    ("DMM P3 Debug 连续串台注入会安全中止", TestP3InjectedPersistentIdentityAborts),
    ("DMM 测试 Logger 输出到控制台", TestDmmTestLoggerIsVisible),
    ("DMM 阶段 B 异常保留堆栈并收口断线", TestDmmStageBSourceContract),
    ("DMM 模式初始化连接异常会标记断线", TestDmmModeFailureMarksDisconnected),
    ("阶段 C Modbus 日志包含拒绝、堆栈、慢请求和释放上下文", TestStageCModbusLogContract),
    ("CTRL-FIX-01 复位停止请求门禁与复位高电平轮询契约", TestCtrlFix01SourceContract),
    ("RESULT-UI-01 PLC 最终结果和系统设置源码契约", TestResultUi01SourceContract),
    ("CSV 日志根目录统一为根目录 TestLog", TestCsvLogRootPathContract),
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
        using var driver = new GwInstekGDM9060Driver(ConsoleTestLogger<GwInstekGDM9060Driver>.Instance);
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
    ("APP-OPT-01 引脚输入只允许 A1-A12 和 B1-B12", () =>
    {
        foreach (var pin in new[] { "A1", "A12", "B1", "B12" })
            AssertEqual(true, InputValidationHelper.IsValidPinName(pin));

        foreach (var pin in new[] { "A0", "A13", "A20", "B0", "B13", "B20", "C1", "" })
            AssertEqual(false, InputValidationHelper.IsValidPinName(pin));
    }),
    ("APP-OPT-01 方案引脚下拉列表为 A1-A12 后接 B1-B12", () =>
    {
        AssertEqual(24, PlanStorageService.PinList.Count);
        AssertEqual("A1", PlanStorageService.PinList[0]);
        AssertEqual("A12", PlanStorageService.PinList[11]);
        AssertEqual("B1", PlanStorageService.PinList[12]);
        AssertEqual("B12", PlanStorageService.PinList[23]);
    }),
    ("APP-OPT-01 serial/operator input validation", () =>
    {
        AssertEqual(true, InputValidationHelper.IsValidSerialNumber(" SN-001 "));
        AssertEqual(false, InputValidationHelper.IsValidSerialNumber(new string('X', InputValidationHelper.MaxSerialNumberLength + 1)));
        AssertEqual(false, InputValidationHelper.IsValidSerialNumber("SN\r\n001"));
        AssertEqual(true, InputValidationHelper.IsValidOperatorName(" operator "));
        AssertEqual(false, InputValidationHelper.IsValidOperatorName(new string('O', InputValidationHelper.MaxOperatorNameLength + 1)));
        AssertEqual(false, InputValidationHelper.IsValidOperatorName("test\toperator"));
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
    ("APP-OPT-01 电阻模式正无穷界面显示超量程", () =>
    {
        var testPoint = new TestPointConfig
        {
            CheckMode = CheckModeConstants.Resistance,
            LowerLimit = 0,
            UpperLimit = 100
        };

        AssertEqual("超量程", InvokeFormatMeasurementResult(
            InspectionMeasurementEvaluator.Parse("+Infinity"), testPoint));
    }),
    ("APP-OPT-01 电阻值界面和 CSV 记录使用不同格式", () =>
    {
        var testPoint = new TestPointConfig
        {
            CheckMode = CheckModeConstants.Resistance,
            LowerLimit = 0,
            UpperLimit = 100
        };

        AssertEqual("10.5000 Ω", InvokeFormatMeasurementResult(
            InspectionMeasurementEvaluator.Parse("10.5"), testPoint));
        AssertEqual("10.5000", InvokeFormatMeasurementRecordResult(
            InspectionMeasurementEvaluator.Parse("10.5"), testPoint));
        AssertEqual("9.90000000E+37", InvokeFormatMeasurementRecordResult(
            InspectionMeasurementEvaluator.Parse("+9.90000000E+37"), testPoint));
        AssertEqual("Infinity", InvokeFormatMeasurementRecordResult(
            InspectionMeasurementEvaluator.Parse("+Infinity"), testPoint));
    }),
    ("APP-OPT-01 CSV 固定列将方案版本放在检查者之前", () =>
    {
        var record = CreateLogRecord(
            "ZZTEST-APP-OPT-01",
            "P1",
            "SN-APP-OPT-01",
            new DateTime(2026, 7, 13, 8, 9, 10),
            planVersion: 2,
            pins: new[] { "A1-B1" });

        var header = CsvRecordFormatter.BuildHeader(record);
        var row = CsvRecordFormatter.BuildDataRow(record, 1);

        AssertEqual(true, header.Contains("方案名称,方案版本,检查者,综合判定", StringComparison.Ordinal));
        AssertEqual(true, row.Contains(",P1,V2,测试员,OK,", StringComparison.Ordinal));
    }),
    ("APP-OPT-01 测试项目保留独立 CSV 记录值", () =>
    {
        var source = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "Models",
            "TestItemModel.cs"));

        AssertEqual(true, source.Contains("private string _recordResult", StringComparison.Ordinal));
        var item = new TestItemModel { RecordResult = "10.5000" };
        AssertEqual("10.5000", item.RecordResult);
    }),
    ("APP-OPT-01 新 CSV 表头不会追加到旧表头文件", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "gm-e78-app-opt-01-csv-header-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = CreateCsvStorage(root, maxRowsPerFile: 50000);
            var record = CreateLogRecord(
                "GM",
                "P1",
                "SN-APP-OPT-01-ROLL",
                new DateTime(2026, 7, 13, 8, 0, 0),
                planVersion: 1,
                pins: new[] { "A1-B1" });

            var monthFolder = Path.Combine(root, "TestLog", "2026-07");
            Directory.CreateDirectory(monthFolder);
            var oldFile = Path.Combine(monthFolder, "GM_P1.csv");
            File.WriteAllLines(oldFile, new[]
            {
                "序号,机种名称,序列号,方案名称,检查者,综合判定,A1-B1,日期,时间,方案版本",
                "1,GM,SN-OLD,P1,测试员,OK,SHORT,2026年7月13日,08时00分00秒,V1"
            }, new UTF8Encoding(true));

            storage.SaveRecordAsync(record).GetAwaiter().GetResult();

            var files = Directory.GetFiles(monthFolder, "GM_P1*.csv");
            AssertEqual(2, files.Length);
            AssertEqual(true, File.ReadAllLines(oldFile).Length == 2);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }),
    ("DMM Ping 失败时发布断线并清理业务连接状态", () =>
    {
        using var driver = new GwInstekGDM9060Driver(ConsoleTestLogger<GwInstekGDM9060Driver>.Instance);
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

            var monthFolder = Path.Combine(root, "TestLog", "2026-07");
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

            var monthFolder = Path.Combine(root, "TestLog", "2026-07");
            var files = Directory.GetFiles(monthFolder, "GM_P1*.csv").OrderBy(x => x).ToList();
            AssertEqual(2, files.Count);

            var firstLines = File.ReadAllLines(files[0]);
            var secondLines = File.ReadAllLines(files[1]);
            if (firstLines[0].Contains("方案名称,方案版本,检查者,综合判定", StringComparison.Ordinal))
            {
                AssertEqual(true, firstLines[1].Contains(",P1,V1,测试员,OK,", StringComparison.Ordinal));
                AssertEqual(true, secondLines[1].Contains(",P1,V2,测试员,OK,", StringComparison.Ordinal));
                return;
            }
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

        // 兼容阶段切换：新格式验证通过后结束该兼容测试；未切换时保留旧基线断言。
        if (header.Contains("方案名称,方案版本,检查者,综合判定", StringComparison.Ordinal))
        {
            AssertEqual(true, row.Contains(",方案1,V2,测试员,OK,", StringComparison.Ordinal));
            return;
        }

        AssertEqual("序号,机种名称,序列号,方案名称,检查者,综合判定,A1-B1,B2-B3,日期,时间,方案版本", header);
        AssertEqual("7,ZZTEST-索引机种A,ZZTEST-SN-202607-000001,方案1,测试员,OK,SHORT,10.5,2026年07月10日,08时09分10秒,V2", row);
    }),

    ("机种名称和方案名称拒绝下划线", () =>
    {
        AssertEqual("机种名称不能包含下划线“_”，该字符用于测试数据文件名分隔。", NameValidationHelper.ValidateMachineType("测试_机种"));
        AssertEqual("方案名称不能包含下划线“_”，该字符用于测试数据文件名分隔。", NameValidationHelper.ValidatePlanName("方案_1"));
        AssertEqual<string?>(null, NameValidationHelper.ValidateMachineType(" ZZTEST-索引机种A "));
        AssertEqual<string?>(null, NameValidationHelper.ValidatePlanName(" 方案1 "));
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
    ("APP-OPT-01 C1 removes non-effective settings without removing PLC UnitId", () =>
    {
        var fp0h = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "AppConfig", "DeviceConfigs", "FP0HCommunicationConfig.cs"));
        var dmm = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "AppConfig", "DeviceConfigs", "GDM9060CommunicationConfig.cs"));
        var scanner = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "AppConfig", "DeviceConfigs", "ScannerSerialCommunicationConfig.cs"));
        var settingsView = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Views", "SystemSettingsView.xaml"));

        AssertEqual(true, fp0h.Contains("SlaveId", StringComparison.Ordinal));
        AssertEqual(false, fp0h.Contains("ReceiveTimeoutMs", StringComparison.Ordinal));
        AssertEqual(false, fp0h.Contains("HealthCheckMode", StringComparison.Ordinal));
        AssertEqual(false, dmm.Contains("SendTimeoutMs", StringComparison.Ordinal));
        AssertEqual(false, dmm.Contains("HealthCheckMode", StringComparison.Ordinal));
        AssertEqual(true, scanner.Contains("Parity", StringComparison.Ordinal));
        AssertEqual(true, scanner.Contains("DataBits", StringComparison.Ordinal));
        AssertEqual(false, scanner.Contains("HealthCheckMode", StringComparison.Ordinal));
        AssertEqual(false, settingsView.Contains("Fp0hConfig.ReceiveTimeoutMs", StringComparison.Ordinal));
        AssertEqual(false, settingsView.Contains("Gdm9060Config.SendTimeoutMs", StringComparison.Ordinal));
        AssertEqual(false, settingsView.Contains("ScannerConfig.HealthCheckMode", StringComparison.Ordinal));
    }),
    ("APP-OPT-01 C1 unifies configured PLC UnitId and scanner serial parameters", () =>
    {
        var plc = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Devices", "Plc", "Fp0hPlcDevice.cs"));
        var scanner = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Devices", "HoneywellH1900Scanner.cs"));
        var connection = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Services", "DeviceConnectionService.cs"));
        var settingsViewModel = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "ViewModels", "SystemSettingsViewModel.cs"));

        AssertEqual(false, plc.Contains("DefaultUnitId", StringComparison.Ordinal));
        AssertEqual(true, plc.Contains("ConfiguredUnitId", StringComparison.Ordinal));
        AssertEqual(true, scanner.Contains("ParseParity", StringComparison.Ordinal));
        AssertEqual(true, scanner.Contains("ParseStopBits", StringComparison.Ordinal));
        AssertEqual(true, scanner.Contains("ParseFlowControl", StringComparison.Ordinal));
        AssertEqual(true, connection.Contains("scanner.Parity", StringComparison.Ordinal));
        AssertEqual(true, settingsViewModel.Contains("tempScanner.Parity", StringComparison.Ordinal));
        AssertEqual(true, settingsViewModel.Contains("IsSavingConfig", StringComparison.Ordinal));
    }),
    ("APP-OPT-01 C2/C3 save overlay and CSV path failure are explicit", () =>
    {
        var source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "ViewModels", "SystemSettingsViewModel.cs"));
        var view = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Views", "SystemSettingsView.xaml"));

        AssertEqual(true, source.Contains("SaveStatusText", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("finally", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("SaveConfigurationAsync(updates)", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("if (!saved)", StringComparison.Ordinal));
        AssertEqual(true, view.Contains("IsSavingConfig", StringComparison.Ordinal));
        AssertEqual(true, view.Contains("正在保存并应用配置，请稍候", StringComparison.Ordinal));
    }),
    ("APP-OPT-01-PATCH-01 保存结果必须在 Loading 关闭后显示", () =>
    {
        var source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "ViewModels", "SystemSettingsViewModel.cs"));
        int saveMethodStart = source.IndexOf("private async Task SaveAllConfigAsync()", StringComparison.Ordinal);
        int finallyIndex = source.IndexOf("finally", saveMethodStart, StringComparison.Ordinal);
        int loadingClosedIndex = source.IndexOf("IsSavingConfig = false", finallyIndex, StringComparison.Ordinal);
        int resultInfoIndex = source.IndexOf("ShowInfoAsync", loadingClosedIndex, StringComparison.Ordinal);
        int resultWarningIndex = source.IndexOf("ShowWarningAsync", loadingClosedIndex, StringComparison.Ordinal);
        int resultErrorIndex = source.IndexOf("ShowErrorAsync", loadingClosedIndex, StringComparison.Ordinal);

        AssertEqual(true, source.Contains("[系统设置][保存] 开始", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("[系统设置][保存] 配置处理完成", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("[系统设置][保存] Loading 已关闭", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("[系统设置][保存] 显示结果提示", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("配置已保存成功。", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("配置已保存，但设备连接失败，请检查设备。", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("配置保存失败：", StringComparison.Ordinal));
        AssertEqual(false, source.Contains("连接参数未变化", StringComparison.Ordinal));
        AssertEqual(false, source.Contains("连接参数已变更", StringComparison.Ordinal));
        AssertEqual(false, source.Contains("无需重新连接", StringComparison.Ordinal));
        AssertEqual(true, saveMethodStart >= 0 && finallyIndex > saveMethodStart);
        AssertEqual(true, loadingClosedIndex > finallyIndex);
        AssertEqual(true, resultInfoIndex > loadingClosedIndex);
        AssertEqual(true, resultWarningIndex > loadingClosedIndex);
        AssertEqual(true, resultErrorIndex > loadingClosedIndex);
    }),
    ("APP-OPT-01-PATCH-01 CSV 运行时切换只写入新路径", () =>
    {
        var rootA = Path.Combine(Path.GetTempPath(), "gm-e78-csv-patch-a-" + Guid.NewGuid().ToString("N"));
        var rootB = Path.Combine(Path.GetTempPath(), "gm-e78-csv-patch-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CsvStorage:RootPath"] = rootA,
                    ["CsvStorage:MaxRowsPerFile"] = "50000"
                })
                .Build();
            var csvSettings = new CsvStorageSettings(configuration);
            var pathManager = new CsvStoragePathManager(
                csvSettings,
                NullLogger<CsvStoragePathManager>.Instance);
            var storage = CreateCsvStorageWithIndex(
                pathManager,
                new MonthlyLogIndexService(NullLogger<MonthlyLogIndexService>.Instance));

            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-A", DateTime.Now)).GetAwaiter().GetResult();

            var applyRuntimePath = typeof(CsvStorageSettings).GetMethod("ApplyRuntimeRootPath")
                ?? throw new InvalidOperationException("缺少 CsvStorageSettings.ApplyRuntimeRootPath");
            applyRuntimePath.Invoke(csvSettings, new object[] { rootB });

            storage.SaveRecordAsync(CreateLogRecord("GM", "P1", "SN-B", DateTime.Now.AddSeconds(1))).GetAwaiter().GetResult();

            var filesA = Directory.GetFiles(Path.Combine(rootA, "TestLog"), "GM_P1*.csv", SearchOption.AllDirectories);
            var filesB = Directory.GetFiles(Path.Combine(rootB, "TestLog"), "GM_P1*.csv", SearchOption.AllDirectories);
            AssertEqual(true, filesA.Length > 0);
            AssertEqual(true, filesB.Length > 0);
            AssertEqual(true, filesA.All(file => File.ReadAllText(file).Contains("SN-A", StringComparison.Ordinal)));
            AssertEqual(false, filesA.Any(file => File.ReadAllText(file).Contains("SN-B", StringComparison.Ordinal)));
            AssertEqual(true, filesB.All(file => File.ReadAllText(file).Contains("SN-B", StringComparison.Ordinal)));
            AssertEqual(false, filesB.Any(file => File.ReadAllText(file).Contains("SN-A", StringComparison.Ordinal)));
        }
        finally
        {
            TryDeleteDirectory(rootA);
            TryDeleteDirectory(rootB);
        }
    }),
    ("APP-OPT-01-PATCH-01 默认路径和非法路径校验契约", () =>
    {
        var defaultConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CsvStorage:RootPath"] = "Default"
            })
            .Build();
        var defaultSettings = new CsvStorageSettings(defaultConfiguration);
        AssertEqual(AppDomain.CurrentDomain.BaseDirectory, defaultSettings.GetEffectiveRootPath());

        var validator = typeof(SystemSettingsViewModel).GetMethod(
            "ValidateAndNormalizeCustomStoragePath",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到自定义 CSV 路径校验方法");
        AssertThrows<TargetInvocationException>(() => validator.Invoke(null, new object[] { " " }));

        var filePath = Path.Combine(Path.GetTempPath(), "gm-e78-csv-file-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(filePath, "not-a-directory");
            AssertThrows<TargetInvocationException>(() => validator.Invoke(null, new object[] { filePath }));
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

        var source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "ViewModels", "SystemSettingsViewModel.cs"));
        AssertEqual(true, source.Contains("RestoreCsvPathPreview", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("previousTestLogPath", StringComparison.Ordinal));
        AssertEqual(true, source.Contains("if (!csvPathApplied)", StringComparison.Ordinal));
    }),
    ("APP-OPT-01 D1/D2 keeps bounded log rolling and DT302 timing contract", () =>
    {
        var program = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Program.cs"));
        var plc = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Devices", "Plc", "Fp0hPlcDevice.cs"));
        var inspectionConfig = new InspectionConfig();
        var timeout = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Services", "TcpModbus", "ModbusTimeoutConstants.cs"));
        var appSettings = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "appsettings.json"));

        AssertEqual(true, program.Contains("fileSizeLimitBytes: 10 * 1024 * 1024", StringComparison.Ordinal));
        AssertEqual(true, program.Contains("rollOnFileSizeLimit: true", StringComparison.Ordinal));
        AssertEqual(true, program.Contains("retainedFileCountLimit: 30", StringComparison.Ordinal));
        AssertEqual(true, program.Contains("shared: true", StringComparison.Ordinal));
        AssertEqual(true, appSettings.Contains("\"fileSizeLimitBytes\": 10485760", StringComparison.Ordinal));
        AssertEqual(true, appSettings.Contains("\"rollOnFileSizeLimit\": true", StringComparison.Ordinal));
        AssertEqual(true, appSettings.Contains("\"retainedFileCountLimit\": 30", StringComparison.Ordinal));
        AssertEqual(true, plc.Contains("Stopwatch.StartNew", StringComparison.Ordinal));
        AssertEqual(true, timeout.Contains("RelayBusinessWaitMs = 3000", StringComparison.Ordinal));
        AssertEqual(ModbusTimeoutConstants.RelayBusinessWaitMs, inspectionConfig.RelaySwitchTimeoutMs);
        AssertEqual(true, plc.Contains("elapsedMilliseconds > 1000", StringComparison.Ordinal));
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

static void TestRunModeResolution()
{
    var fakeConfiguration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hardware:UseFakeInspectionHardware"] = "true",
            ["Hardware:SemiPhysicalDebug"] = "true"
        })
        .Build();
    var semiPhysicalConfiguration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hardware:UseFakeInspectionHardware"] = "false",
            ["Hardware:SemiPhysicalDebug"] = "true"
        })
        .Build();
    var realConfiguration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hardware:UseFakeInspectionHardware"] = "false",
            ["Hardware:SemiPhysicalDebug"] = "false"
        })
        .Build();

    AssertEqual("Fake", ApplicationRunModeResolver.Resolve(fakeConfiguration));
    AssertEqual("SemiPhysical", ApplicationRunModeResolver.Resolve(semiPhysicalConfiguration));
    AssertEqual("Real", ApplicationRunModeResolver.Resolve(realConfiguration));
}

static void TestStageALogLevels()
{
    var program = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Program.cs"));
    var plc = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Devices", "Plc", "Fp0hPlcDevice.cs"));
    var dmm = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Devices", "GwInstekGDM9060Driver.cs"));
    var fake = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Devices", "Fakes", "FakeInspectionHardware.cs"));
    var inspection = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Services", "InspectionEngine.cs"));
    var testPage = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "ViewModels", "TestPageViewModel.cs"));

    AssertEqual(true, program.Contains("[RunMode={RunMode}]", StringComparison.Ordinal));
    AssertEqual(false, plc.Contains("_logger.LogWarning(\"[设备连接][PLC] 连接成功", StringComparison.Ordinal));
    AssertEqual(true, plc.Contains("_logger.LogInformation(\"[设备连接][PLC] 连接成功", StringComparison.Ordinal));
    AssertEqual(false, plc.Contains("_logger.LogWarning(\"[设备连接][PLC] 断开连接", StringComparison.Ordinal));
    AssertEqual(true, plc.Contains("_logger.LogInformation(\"[设备连接][PLC] 断开连接", StringComparison.Ordinal));
    AssertEqual(false, dmm.Contains("_logger.LogWarning(\"[万用表][审计] 万用表已切换", StringComparison.Ordinal));
    AssertEqual(true, dmm.Contains("_logger.LogInformation(\"[DMM模式][配置完成] 万用表已切换", StringComparison.Ordinal));
    AssertEqual(false, fake.Contains("_logger.LogWarning(\"[Fake硬件]", StringComparison.Ordinal));
    AssertEqual(false, fake.Contains("_logger.LogWarning(\"[Fake][审计]", StringComparison.Ordinal));
    AssertEqual(true, fake.Contains("_logger.LogInformation(\"[Fake][连接]", StringComparison.Ordinal));
    var normalizedInspection = inspection.Replace("\r\n", "\n", StringComparison.Ordinal);
    AssertEqual(true, normalizedInspection.Contains(
        "_logger.LogInformation(\n                        \"[DMM性能][点位完成]",
        StringComparison.Ordinal));
    AssertEqual(false, testPage.Contains("_logger.LogWarning(\"[PLC动作][审计] 本轮正常完成收口结束", StringComparison.Ordinal));
    AssertEqual(true, testPage.Contains("_logger.LogInformation(\"[PLC动作][审计] 本轮正常完成收口结束", StringComparison.Ordinal));
    AssertEqual(false, testPage.Contains("_logger.LogWarning(\"[停止流程][审计] 停止收口完成", StringComparison.Ordinal));
    AssertEqual(true, testPage.Contains("_logger.LogInformation(\"[停止流程][审计] 停止收口完成", StringComparison.Ordinal));
    AssertEqual(false, testPage.Contains("_logger.LogWarning(\"[调试按钮][启动][请求]", StringComparison.Ordinal));
    AssertEqual(true, testPage.Contains("_logger.LogInformation(\"[调试按钮][启动][请求]", StringComparison.Ordinal));
}

static void TestDmmEmptyResponseThrows()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;

    var readTask = driver.ReadResistanceRawAsync();
    var requestBuffer = new byte[64];
    int requestBytes = server.GetStream().Read(requestBuffer, 0, requestBuffer.Length);
    AssertEqual(true, requestBytes > 0);

    server.Close();
    AssertThrows<TimeoutException>(() => readTask.GetAwaiter().GetResult());
}

static void TestDmmCancellationIsNotTimeout()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    using var cancellation = new CancellationTokenSource();

    var readTask = driver.ReadResistanceRawAsync(cancellation.Token);
    var requestBuffer = new byte[64];
    int requestBytes = server.GetStream().Read(requestBuffer, 0, requestBuffer.Length);
    AssertEqual(true, requestBytes > 0);

    cancellation.Cancel();
    AssertThrows<OperationCanceledException>(() => readTask.GetAwaiter().GetResult());
}

static void TestP1StartingPingUsesStartingToken()
{
    var source = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "ViewModels",
        "TestPageViewModel.cs"));

    AssertEqual(true, source.Contains("PingAsync(startingToken)", StringComparison.Ordinal));
    AssertEqual(false, source.Contains("PingAsync(CancellationToken.None)", StringComparison.Ordinal));
}

static void TestP1CancellationAfterWriteMarksBufferDirty()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    using var cancellation = new CancellationTokenSource();

    var readTask = driver.ReadResistanceRawAsync(cancellation.Token);
    var requestBuffer = new byte[64];
    int requestBytes = server.GetStream().Read(requestBuffer, 0, requestBuffer.Length);
    AssertEqual(true, requestBytes > 0);

    cancellation.Cancel();
    AssertThrows<OperationCanceledException>(() => readTask.GetAwaiter().GetResult());
    AssertEqual(true, GetPrivateField<bool>(driver, "_receiveBufferPossiblyDirty"));
}

static void TestP2NormalResponseKeepsBufferClean()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    SetPrivateField(driver, "_receiveBufferPossiblyDirty", true);

    var readTask = driver.ReadResistanceRawAsync();
    var requestBuffer = new byte[64];
    int requestBytes = server.GetStream().Read(requestBuffer, 0, requestBuffer.Length);
    AssertEqual(true, requestBytes > 0);

    var responseBytes = Encoding.ASCII.GetBytes("12.5\r\n");
    server.GetStream().Write(responseBytes, 0, responseBytes.Length);
    server.GetStream().Flush();

    AssertEqual("12.5", readTask.GetAwaiter().GetResult());
    AssertEqual(false, GetPrivateField<bool>(driver, "_receiveBufferPossiblyDirty"));
}

static void TestP1DisconnectClearsBufferDirty()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    SetPrivateField(driver, "_receiveBufferPossiblyDirty", true);

    driver.DisconnectAsync().GetAwaiter().GetResult();

    AssertEqual(false, GetPrivateField<bool>(driver, "_receiveBufferPossiblyDirty"));
}

static void TestP2DirtyBufferIsDrainedBeforeMeasurement()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    SetPrivateField(driver, "_receiveBufferPossiblyDirty", true);
    WriteDmmResponse(server.GetStream(), "GWInstek,GDM9060,STALE,TEST");

    var readTask = driver.ReadResistanceRawAsync();
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "12.5");

    AssertEqual("12.5", readTask.GetAwaiter().GetResult());
    AssertEqual(false, GetPrivateField<bool>(driver, "_receiveBufferPossiblyDirty"));
}

static void TestP2ModeCacheHitStillDrainsDirtyBuffer()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    SetPrivateField(driver, "_receiveBufferPossiblyDirty", true);
    SetPrivateField(driver, "_cachedMode", GetDmmCachedMode("Resistance"));
    WriteDmmResponse(server.GetStream(), "GWInstek,GDM9060,STALE,CACHE");

    AssertEqual(true, driver.InitializeResistanceModeAsync().GetAwaiter().GetResult());

    var readTask = driver.ReadResistanceRawAsync();
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "8.25");

    AssertEqual("8.25", readTask.GetAwaiter().GetResult());
}

static void TestP2MeasurementIdentityResponseRetriesOnce()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    server.GetStream().ReadTimeout = 2000;

    var readTask = driver.ReadResistanceRawAsync();
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "GWInstek,GDM9060,STALE,RETRY");
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "15.75");

    AssertEqual("15.75", readTask.GetAwaiter().GetResult());
}

static void TestP2PersistentIdentityResponseAborts()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    server.GetStream().ReadTimeout = 2000;

    var readTask = driver.ReadResistanceRawAsync();
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "GWInstek,GDM9060,STALE,FIRST");
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "GWInstek,GDM9060,STALE,SECOND");

    AssertThrows<InvalidOperationException>(() => readTask.GetAwaiter().GetResult());
}

static void TestP2InvalidMeasurementResponseAborts()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;

    var readTask = driver.ReadContinuityRawAsync();
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "NOT_A_MEASUREMENT");

    AssertThrows<InvalidOperationException>(() => readTask.GetAwaiter().GetResult());
}

static void TestP2QueryResponseTypeValidation()
{
    var idnPair = CreateDmmSocketPair();
    using (idnPair.Driver)
    using (idnPair.Client)
    using (idnPair.Server)
    using (idnPair.Listener)
    {
        var queryTask = idnPair.Driver.SendQueryAsync("*IDN?");
        ReadDmmRequest(idnPair.Server.GetStream());
        WriteDmmResponse(idnPair.Server.GetStream(), "1");
        AssertThrows<InvalidOperationException>(() => queryTask.GetAwaiter().GetResult());
    }

    var opcPair = CreateDmmSocketPair();
    using (opcPair.Driver)
    using (opcPair.Client)
    using (opcPair.Server)
    using (opcPair.Listener)
    {
        var queryTask = opcPair.Driver.SendQueryAsync("*OPC?");
        ReadDmmRequest(opcPair.Server.GetStream());
        WriteDmmResponse(opcPair.Server.GetStream(), "GWInstek,GDM9060,WRONG,TYPE");
        AssertThrows<InvalidOperationException>(() => queryTask.GetAwaiter().GetResult());
    }
}

static void TestP2DrainUsesBoundedQuietChecks()
{
    var source = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Devices",
        "GwInstekGDM9060Driver.cs"));

    AssertEqual(true, source.Contains("DrainMaxDurationMs = 250", StringComparison.Ordinal));
    AssertEqual(true, source.Contains("DrainQuietIntervalMs = 20", StringComparison.Ordinal));
    AssertEqual(true, source.Contains("DrainRequiredQuietChecks = 3", StringComparison.Ordinal));
    AssertEqual(true, source.Contains("_receiveBufferPossiblyDirty", StringComparison.Ordinal));
}

static void TestP3DebugHookIsScopedAndOffByDefault()
{
    var source = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Devices",
        "GwInstekGDM9060Driver.cs"));

    AssertEqual(true, source.Contains("#if DEBUG", StringComparison.Ordinal));
    AssertEqual(true, source.Contains(
        "DebugInjectIdentityResponseBeforeNextMeasurement",
        StringComparison.Ordinal));
    AssertEqual(true, source.Contains("= false", StringComparison.Ordinal));
}

static void TestP3InjectedIdentityResponseRecovers()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    server.GetStream().ReadTimeout = 2000;
    SetDebugIdentityInjection(driver, true);
    AssertEqual(true, GetDebugIdentityInjection(driver));

    var readTask = driver.ReadResistanceRawAsync();
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "10.5");
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "11.5");

    AssertEqual("11.5", readTask.GetAwaiter().GetResult());
}

static void TestP3InjectedPersistentIdentityAborts()
{
    var pair = CreateDmmSocketPair();
    using var driver = pair.Driver;
    using var client = pair.Client;
    using var server = pair.Server;
    using var listener = pair.Listener;
    server.GetStream().ReadTimeout = 2000;
    SetDebugIdentityInjection(driver, true);
    AssertEqual(true, GetDebugIdentityInjection(driver));

    var readTask = driver.ReadResistanceRawAsync();
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "10.5");
    ReadDmmRequest(server.GetStream());
    WriteDmmResponse(server.GetStream(), "GWInstek,GDM9060,DEBUG,SECOND");

    AssertThrows<InvalidOperationException>(() => readTask.GetAwaiter().GetResult());
}

static void TestDmmTestLoggerIsVisible()
{
    var source = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Tests",
        "MinimumLoopTests",
        "Program.cs"));

    const string dmmLoggerConstructor = "new GwInstekGDM9060Driver("
        + "ConsoleTestLogger<GwInstekGDM9060Driver>.Instance)";
    AssertEqual(4, CountOccurrences(source, dmmLoggerConstructor));
}

static void TestConsoleOutputEncodingIsUtf8()
{
    var source = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Tests",
        "MinimumLoopTests",
        "Program.cs"));
    const string outputEncodingStatement = "Console." + "OutputEncoding = new UTF8Encoding(false);";

    AssertEqual(true, source.Contains(outputEncodingStatement, StringComparison.Ordinal));
}

static void TestStageEFakeLogContract()
{
    var fakeSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Devices",
        "Fakes",
        "FakeInspectionHardware.cs"));
    var testPageSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "ViewModels",
        "TestPageViewModel.cs"));

    AssertEqual(false, fakeSource.Contains("[Fake硬件]", StringComparison.Ordinal));
    AssertEqual(false, fakeSource.Contains("[Fake][审计]", StringComparison.Ordinal));
    AssertEqual(false, fakeSource.Contains("[DMM模式][Fake]", StringComparison.Ordinal));
    AssertEqual(true, fakeSource.Contains(
        "[Fake][DMM] 模拟返回 Command=READ?, RawText={RawText}",
        StringComparison.Ordinal));
    AssertEqual(true, fakeSource.Contains(
        "[Fake][DMM] 模拟返回 Command=MEAS:CONT?, RawText={RawText}",
        StringComparison.Ordinal));
    AssertEqual(false, testPageSource.Contains("[复位流程][Fake]", StringComparison.Ordinal));
    AssertEqual(true, testPageSource.Contains("[Fake][控制动作]", StringComparison.Ordinal));
}

static void TestStageFGlobalExceptionContract()
{
    var source = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Program.cs"));

    const string dispatcherRegistration = "app."
        + "DispatcherUnhandledException += OnDispatcherUnhandledException;";
    const string appDomainRegistration = "AppDomain.CurrentDomain."
        + "UnhandledException += OnAppDomainUnhandledException;";
    const string taskRegistration = "TaskScheduler."
        + "UnobservedTaskException += OnUnobservedTaskException;";

    AssertEqual(1, CountOccurrences(source, dispatcherRegistration));
    AssertEqual(1, CountOccurrences(source, appDomainRegistration));
    AssertEqual(1, CountOccurrences(source, taskRegistration));
    AssertEqual(true, source.Contains("[全局异常][UI线程]", StringComparison.Ordinal));
    AssertEqual(true, source.Contains("[全局异常][AppDomain]", StringComparison.Ordinal));
    AssertEqual(true, source.Contains("[全局异常][未观察Task]", StringComparison.Ordinal));
    AssertEqual(true, source.Contains("e.SetObserved();", StringComparison.Ordinal));
    AssertEqual(true, source.IndexOf("RegisterGlobalExceptionHandlers(app);", StringComparison.Ordinal)
        < source.IndexOf("app.Run(mainWindow);", StringComparison.Ordinal));
}

static void TestDmmStageBSourceContract()
{
    var source = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "Devices", "GwInstekGDM9060Driver.cs"));
    var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);

    AssertEqual(true, normalized.Contains(
        "when (readTimeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)",
        StringComparison.Ordinal));
    AssertEqual(true, normalized.Contains("[DMM查询][取消]", StringComparison.Ordinal));
    AssertEqual(true, normalized.Contains("[DMM测量][失败] Command=READ?", StringComparison.Ordinal));
    AssertEqual(true, normalized.Contains("await MarkConnectionLostAsync().ConfigureAwait(false);", StringComparison.Ordinal));
}

static void TestDmmModeFailureMarksDisconnected()
{
    using var driver = new GwInstekGDM9060Driver(ConsoleTestLogger<GwInstekGDM9060Driver>.Instance);
    SetPrivateField(driver, "_isConnected", true);

    var method = typeof(GwInstekGDM9060Driver).GetMethod(
        "ConfigureResistanceModeInternalAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 DMM 模式配置内部方法");

    var task = (Task<bool>)(method.Invoke(driver, new object?[]
    {
        "测试模式",
        "CONF:RES",
        null,
        CancellationToken.None,
        false
    }) ?? throw new InvalidOperationException("DMM 模式配置方法未返回任务"));

    AssertEqual(false, task.GetAwaiter().GetResult());
    AssertEqual(false, driver.IsConnected);
}

static void TestStageCModbusLogContract()
{
    var source = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Services",
        "TcpModbus",
        "TcpClientPLCMotionService.cs"));
    var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);

    const string writeRejected = "[Modbus诊断][写入拒绝] FC={FC}, UnitId={UnitId}, StartAddress={StartAddress}, IsConnected={IsConnected}, IsRunning={IsRunning}";
    AssertEqual(true, CountOccurrences(normalized, writeRejected) >= 2);
    AssertEqual(false, normalized.Contains("ExceptionType={ExceptionType}, Error={Error}", StringComparison.Ordinal));
    AssertEqual(true, normalized.Contains(
        "ex,\n                        \"[Modbus诊断][发送失败] TID={TID}, FC={FC}, StartAddress={StartAddress}, ElapsedMs={ElapsedMs}\"",
        StringComparison.Ordinal));
    AssertEqual(false, normalized.Contains("ElapsedMilliseconds >= 100", StringComparison.Ordinal));
    AssertEqual(true, normalized.Contains(">= timeoutMs * 0.8", StringComparison.Ordinal));
    AssertEqual(true, normalized.Contains(">= 300", StringComparison.Ordinal));
    AssertEqual(true, normalized.Contains(
        "[Modbus诊断][自定义请求超时] TID={TID}, FC={FC}, ElapsedMs={ElapsedMs}, TimeoutMs={TimeoutMs}, IsConnected={IsConnected}, IsRunning={IsRunning}",
        StringComparison.Ordinal));
    AssertEqual(true, normalized.Contains("_requestLock.Dispose();", StringComparison.Ordinal));
}

static void TestCtrlFix01SourceContract()
{
    var source = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "ViewModels",
        "TestPageViewModel.cs"))
        .Replace("\r\n", "\n", StringComparison.Ordinal);

    AssertEqual(true, source.Contains("private bool IsResetRequestInProgress()", StringComparison.Ordinal));
    AssertEqual(true, source.Contains("private bool IsStopRequestInProgress()", StringComparison.Ordinal));
    AssertMethodCallOrder(source, "TriggerDebugResetAsync", "IsResetRequestInProgress()", "RequestResetAsync");
    AssertMethodCallOrder(source, "TriggerPlcResetAsync", "IsResetRequestInProgress()", "RequestResetAsync");
    AssertMethodCallOrder(source, "TriggerDebugStopAsync", "IsStopRequestInProgress()", "RequestStopAsync");
    AssertEqual(true, source.Contains("if (UiState == TestUIState.ResetFailed)", StringComparison.Ordinal));
    AssertEqual(true, source.Contains("_resetSignalHandled = false;", StringComparison.Ordinal));
    AssertEqual(true, source.Contains("if (inputs.IsResetRequested && !_resetSignalHandled)", StringComparison.Ordinal));
    AssertEqual(false, source.Contains("if (_resetSignalHandled)\n                return;", StringComparison.Ordinal));

    var emergencyStart = source.IndexOf("private async Task TriggerDebugEmergencyStopAsync()", StringComparison.Ordinal);
    var emergencyEnd = source.IndexOf("#endregion", emergencyStart, StringComparison.Ordinal);
    var emergencyMethod = source[emergencyStart..emergencyEnd];
    AssertEqual(false, emergencyMethod.Contains("IsResetRequestInProgress()", StringComparison.Ordinal));
    AssertEqual(false, emergencyMethod.Contains("IsStopRequestInProgress()", StringComparison.Ordinal));
}

static void AssertMethodCallOrder(string source, string methodName, string guard, string request)
{
    var signature = $"private async Task {methodName}()";
    var methodStart = source.IndexOf(signature, StringComparison.Ordinal);
    var methodEnd = source.IndexOf("private async Task", methodStart + signature.Length, StringComparison.Ordinal);
    if (methodEnd < 0)
        methodEnd = source.Length;

    var method = source[methodStart..methodEnd];
    AssertEqual(true, method.Contains(guard, StringComparison.Ordinal));
    AssertEqual(true, method.IndexOf(guard, StringComparison.Ordinal) < method.IndexOf(request, StringComparison.Ordinal));
}

static void TestStageDSemiPhysicalLogContract()
{
    var viewModelSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "ViewModels",
        "TestPageViewModel.cs"));
    var dialogSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "ViewModels",
        "EmergencyStopDialogViewModel.cs"));
    var dmmSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Devices",
        "GwInstekGDM9060Driver.cs"));

    AssertEqual(true, viewModelSource.Contains("[半实物][调试动作]", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("[半实物][PLC信号变化]", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("[半实物][异常注入]", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("LogSemiPhysicalSignalChanges", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("if (!IsSemiPhysicalDebugMode)", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("DT120", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("DT121", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("DT122", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("DT123", StringComparison.Ordinal));

    AssertEqual(true, dialogSource.Contains("[半实物][调试动作]", StringComparison.Ordinal));
    AssertEqual(true, dialogSource.Contains("[半实物][PLC信号变化]", StringComparison.Ordinal));
    AssertEqual(true, dialogSource.Contains("DT303", StringComparison.Ordinal));

    AssertEqual(true, dmmSource.Contains("[DMM模式][配置完成]", StringComparison.Ordinal));
    AssertEqual(true, dmmSource.Contains("ThresholdOhm={ThresholdOhm}", StringComparison.Ordinal));
    AssertEqual(true, dmmSource.Contains("RawText={RawText}", StringComparison.Ordinal));
    AssertEqual(true, dmmSource.Contains("ElapsedMs={ElapsedMs}", StringComparison.Ordinal));
    AssertEqual(true, dmmSource.Contains("[DMM测量][取消]", StringComparison.Ordinal));
}

static (GwInstekGDM9060Driver Driver, TcpClient Client, TcpClient Server, TcpListener Listener) CreateDmmSocketPair()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var acceptTask = listener.AcceptTcpClientAsync();
    var client = new TcpClient();
    client.Connect(IPAddress.Loopback, port);
    var server = acceptTask.GetAwaiter().GetResult();

    var driver = new GwInstekGDM9060Driver(ConsoleTestLogger<GwInstekGDM9060Driver>.Instance);
    SetPrivateField(driver, "_tcpClient", client);
    SetPrivateField(driver, "_networkStream", client.GetStream());
    SetPrivateField(driver, "_isConnected", true);
    return (driver, client, server, listener);
}

static TestPointConfig CreateOpenContinuityTestPoint()
{
    return new TestPointConfig
    {
        CheckMode = CheckModeConstants.Continuity,
        ModeValue = "OPEN"
    };
}


static void TestRefClean01ForbiddenSourceContract()
{
    var sourceRoot = Environment.CurrentDirectory;
    var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
        .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        .Where(path => !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("\\Tests\\", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase))
        .ToList();

    var forbiddenNames = new[]
    {
        "LogDataView", "LogDataViewModel", "NavigateToDataExtract",
        "ICsvExportService", "CsvExportService", "DevelopmentDataTool",
        "TestDataSeeder", "SeedThroughNormalSaveAsync", "SeedBulkMonthAsync",
        "SeedHistoryAsync", "IsPlcCommunicationTestEnabled",
        "IsPlcCommunicationTestVisible", "NavigateToPlcCommunicationTest",
        "RefreshPlcTestButtonVisibility", "AppDbContext", "DatabaseSettings",
        "DatabaseInitializer", "SqliteTestRecordStorage",
        "Microsoft.EntityFrameworkCore", "UseSqlite", "AddDbContext",
        "AddDbContextFactory", "IDbContextFactory", "DbSet<"
    };

    var hits = new List<string>();
    foreach (var path in sourceFiles)
    {
        var content = File.ReadAllText(path);
        foreach (var forbiddenName in forbiddenNames)
        {
            if (content.Contains(forbiddenName, StringComparison.Ordinal))
                hits.Add($"{Path.GetRelativePath(sourceRoot, path)} => {forbiddenName}");
        }
    }

    AssertEqual(string.Empty, string.Join(Environment.NewLine, hits));
}

static void TestResultUi01SourceContract()
{
    string engineSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Services",
        "InspectionEngine.cs"));
    string viewModelSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "ViewModels",
        "TestPageViewModel.cs"));
    string cleanupModelSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Models",
        "Inspection",
        "RunOutputCleanupResult.cs"));
    string settingsViewModelSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "ViewModels",
        "SystemSettingsViewModel.cs"));
    string settingsViewSource = File.ReadAllText(Path.Combine(
        Environment.CurrentDirectory,
        "Views",
        "SystemSettingsView.xaml"));

    AssertEqual(true, engineSource.Contains("var finalWriteResult = await _plcDevice.WriteFinalResultAsync(", StringComparison.Ordinal));
    AssertEqual(true, engineSource.Contains("FinalResultWriteFailed", StringComparison.Ordinal));
    AssertEqual(true, engineSource.Contains("finalWriteResult.Message", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("ClearTransientRunOutputsAsync", StringComparison.Ordinal));
    AssertEqual(false, viewModelSource.Contains("ClearCurrentRunOutputsAsync", StringComparison.Ordinal));
    AssertEqual(false, cleanupModelSource.Contains("FinalResultCleared", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("_finalResultAutoClearCts", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("_finalResultAutoClearTask", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("ScheduleOkFinalResultAutoClear", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("Task.Delay(TimeSpan.FromSeconds(1), token)", StringComparison.Ordinal));
    AssertEqual(true, viewModelSource.Contains("if (UiState != TestUIState.Error)", StringComparison.Ordinal));
    AssertEqual(false, settingsViewModelSource.Contains("ResetToDefaultAsync", StringComparison.Ordinal));
    AssertEqual(false, settingsViewSource.Contains("ResetToDefaultCommand", StringComparison.Ordinal));
    AssertEqual(true, settingsViewSource.Contains("IsReadOnly=\"True\"", StringComparison.Ordinal));
}

static void TestCsvLogRootPathContract()
{
    string root = Path.Combine(
        Path.GetTempPath(),
        "gm-e78-csv-root-contract-" + Guid.NewGuid().ToString("N"));

    try
    {
        var pathManager = CreateCsvPathManager(root, maxRowsPerFile: 50000);
        AssertEqual(Path.Combine(root, "TestLog"), pathManager.GetTestLogRootPath());

        string pathManagerSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "Services",
            "CsvStoragePathManager.cs"));
        string settingsViewModelSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "ViewModels",
            "SystemSettingsViewModel.cs"));
        string settingsViewSource = File.ReadAllText(Path.Combine(
            Environment.CurrentDirectory,
            "Views",
            "SystemSettingsView.xaml"));

        AssertEqual(true, pathManagerSource.Contains(
            "public static string BuildTestLogRootPath(string rootPath)",
            StringComparison.Ordinal));
        AssertEqual(false, pathManagerSource.Contains("DATA_FOLDER_NAME", StringComparison.Ordinal));
        AssertEqual(true, settingsViewModelSource.Contains(
            "CsvStoragePathManager.BuildTestLogRootPath(rootPath)",
            StringComparison.Ordinal));
        AssertEqual(false, settingsViewModelSource.Contains(
            "Path.Combine(CustomStoragePath, \"数据\", \"TestLog\")",
            StringComparison.Ordinal));
        AssertEqual(true, settingsViewModelSource.Contains(
            "_csvPathManager.GetTestLogRootPath()",
            StringComparison.Ordinal));
        AssertEqual(true, settingsViewModelSource.Contains(
            "Directory.CreateDirectory(testLogPath)",
            StringComparison.Ordinal));
        AssertEqual(true, settingsViewModelSource.Contains(
            "new System.Diagnostics.ProcessStartInfo",
            StringComparison.Ordinal));
        AssertEqual(false, settingsViewSource.Contains("「数据\\TestLog」", StringComparison.Ordinal));
        AssertEqual(true, settingsViewSource.Contains("「TestLog」文件夹", StringComparison.Ordinal));
    }
    finally
    {
        TryDeleteDirectory(root);
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"expected {expected}, actual {actual}");
    }
}

static int CountOccurrences(string text, string value)
{
    int count = 0;
    int index = 0;
    while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += value.Length;
    }

    return count;
}

static void SetPrivateField<TValue>(object target, string fieldName, TValue value)
{
    var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"找不到私有字段: {fieldName}");
    field.SetValue(target, value);
}

static object GetDmmCachedMode(string modeName)
{
    var enumType = typeof(GwInstekGDM9060Driver).GetNestedType(
        "DmmCachedMode",
        BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 DMM 模式缓存枚举");
    return Enum.Parse(enumType, modeName);
}

static void ReadDmmRequest(NetworkStream stream)
{
    var requestBuffer = new byte[128];
    int requestBytes = stream.Read(requestBuffer, 0, requestBuffer.Length);
    AssertEqual(true, requestBytes > 0);
}

static void WriteDmmResponse(NetworkStream stream, string response)
{
    var responseBytes = Encoding.ASCII.GetBytes(response + "\r\n");
    stream.Write(responseBytes, 0, responseBytes.Length);
    stream.Flush();
}

static void SetDebugIdentityInjection(GwInstekGDM9060Driver driver, bool enabled)
{
    var property = driver.GetType().GetProperty(
        "DebugInjectIdentityResponseBeforeNextMeasurement",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 Debug DMM 串台注入钩子");
    property.SetValue(driver, enabled);
}

static bool GetDebugIdentityInjection(GwInstekGDM9060Driver driver)
{
    var property = driver.GetType().GetProperty(
        "DebugInjectIdentityResponseBeforeNextMeasurement",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 Debug DMM 串台注入钩子");
    return (bool)(property.GetValue(driver)
        ?? throw new InvalidOperationException("Debug DMM 串台注入钩子值为空"));
}

static TValue GetPrivateField<TValue>(object target, string fieldName)
{
    var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"找不到私有字段 {fieldName}");
    return (TValue)(field.GetValue(target)
        ?? throw new InvalidOperationException($"私有字段 {fieldName} 当前值为空"));
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

static string InvokeFormatMeasurementRecordResult(MeasurementResult measurement, TestPointConfig testPoint)
{
    var method = typeof(TestPageViewModel).GetMethod(
        "FormatMeasurementRecordResult",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 FormatMeasurementRecordResult 方法");

    return (string)(method.Invoke(null, new object[] { measurement, testPoint })
        ?? throw new InvalidOperationException("FormatMeasurementRecordResult 返回空值"));
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

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
    catch
    {
        // 测试清理失败不覆盖主体断言结果。
    }
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

/// <summary>
/// 测试专用控制台 Logger，让 DMM 串台隔离过程在最小闭环输出中可见。
/// </summary>
sealed class ConsoleTestLogger<TCategory> : ILogger<TCategory>
{
    public static ConsoleTestLogger<TCategory> Instance { get; } = new();

    private readonly object _syncRoot = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
        => NoopDisposable.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        string message = formatter(state, exception);
        lock (_syncRoot)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {message}");
            if (exception != null)
                Console.WriteLine(exception);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
