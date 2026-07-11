using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.Development;

/// <summary>
/// 仅用于 Debug 阶段生成和清理 ZZTEST- 测试数据，正式业务不依赖本类。
/// </summary>
public class TestDataSeeder
{
    private const string TestPrefix = "ZZTEST-";
    private const string DefaultOperator = "开发测试";
    private const int RecordsPerVersion = 2000;

    private readonly CsvStoragePathManager _pathManager;
    private readonly ITestRecordStorage _testRecordStorage;
    private readonly MonthlyLogIndexService _monthlyLogIndexService;
    private readonly ILogger<TestDataSeeder> _logger;

    public TestDataSeeder(
        CsvStoragePathManager pathManager,
        ITestRecordStorage testRecordStorage,
        MonthlyLogIndexService monthlyLogIndexService,
        ILogger<TestDataSeeder> logger)
    {
        _pathManager = pathManager ?? throw new ArgumentNullException(nameof(pathManager));
        _testRecordStorage = testRecordStorage ?? throw new ArgumentNullException(nameof(testRecordStorage));
        _monthlyLogIndexService = monthlyLogIndexService ?? throw new ArgumentNullException(nameof(monthlyLogIndexService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task SeedThroughNormalSaveAsync(string machineType, int count) =>
        SeedThroughNormalSaveAsync(machineType, count, null);

    public async Task SeedThroughNormalSaveAsync(string machineType, int count, IProgress<int>? progress)
    {
        machineType = NormalizeTestMachineType(machineType);
        count = Math.Max(1, count);
        _logger.LogWarning("[ZZTEST] 正常链路生成开始 MachineType={MachineType} Count={Count}", machineType, count);

        var baseTime = DateTime.Now;
        for (var i = 0; i < count; i++)
        {
            var planIndex = i < count * 0.6 ? 1 : 2;
            var planVersion = planIndex == 1 && i >= count * 0.3 ? 2 : 1;
            var template = CreateTemplate(planIndex, planVersion);
            var record = CreateRecord(machineType, template, i + 1, baseTime.AddSeconds(i), i);
            await _testRecordStorage.SaveRecordAsync(record);
            progress?.Report(i + 1);
        }

        _logger.LogWarning("[ZZTEST] 正常链路生成完成 Count={Count}", count);
    }

    public Task SeedBulkMonthAsync(string month, string machineType, int planCount, int recordCount) =>
        SeedBulkMonthAsync(month, machineType, planCount, recordCount, null);

    public async Task SeedBulkMonthAsync(
        string month,
        string machineType,
        int planCount,
        int recordCount,
        IProgress<int>? progress)
    {
        var monthDate = ParseMonth(month);
        machineType = NormalizeTestMachineType(machineType);
        planCount = Math.Max(1, planCount);
        recordCount = Math.Max(1, recordCount);

        _logger.LogWarning(
            "[ZZTEST] 单月批量生成开始 Month={Month} MachineType={MachineType} Plans={PlanCount} Records={RecordCount}",
            monthDate.ToString("yyyy-MM", CultureInfo.InvariantCulture), machineType, planCount, recordCount);

        await Task.Run(() => WriteBulkMonth(monthDate, machineType, planCount, recordCount, progress));
        await _monthlyLogIndexService.RebuildMonthIndexAsync(_pathManager.GetMonthFolderPath(monthDate));

        _logger.LogWarning("[ZZTEST] 单月批量生成完成 Month={Month} Records={RecordCount}", month, recordCount);
    }

    public Task SeedHistoryAsync(string startMonth, int monthCount, string machineType, int recordsPerMonth) =>
        SeedHistoryAsync(startMonth, monthCount, machineType, recordsPerMonth, null);

    public async Task SeedHistoryAsync(
        string startMonth,
        int monthCount,
        string machineType,
        int recordsPerMonth,
        IProgress<int>? progress)
    {
        var start = ParseMonth(startMonth);
        monthCount = Math.Max(1, monthCount);
        recordsPerMonth = Math.Max(1, recordsPerMonth);
        machineType = NormalizeTestMachineType(machineType);
        var completed = 0;

        _logger.LogWarning("[ZZTEST] 跨月份生成开始 StartMonth={StartMonth} MonthCount={MonthCount}", startMonth, monthCount);

        for (var i = 0; i < monthCount; i++)
        {
            var monthDate = start.AddMonths(i);
            await Task.Run(() => WriteBulkMonth(monthDate, machineType, 3, recordsPerMonth, null));
            await _monthlyLogIndexService.RebuildMonthIndexAsync(_pathManager.GetMonthFolderPath(monthDate));
            completed += recordsPerMonth;
            progress?.Report(completed);
            _logger.LogWarning("[ZZTEST] 月份生成完成 Month={Month} Records={Records}", monthDate.ToString("yyyy-MM"), recordsPerMonth);
        }
    }

    public async Task ClearDevTestDataAsync()
    {
        _logger.LogWarning("[ZZTEST] 清理开始");
        var affectedMonthFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var testLogRoot = _pathManager.GetTestLogRootPath();
        if (!Directory.Exists(testLogRoot))
            return;

        foreach (var monthFolder in Directory.GetDirectories(testLogRoot))
        {
            foreach (var filePath in Directory.GetFiles(monthFolder, "ZZTEST-*.csv"))
            {
                File.Delete(filePath);
                affectedMonthFolders.Add(monthFolder);
                _logger.LogWarning("[ZZTEST] 删除测试文件 File={File}", filePath);
            }
        }

        foreach (var monthFolder in affectedMonthFolders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            await _monthlyLogIndexService.RebuildMonthIndexAsync(monthFolder);

        _logger.LogWarning("[ZZTEST] 清理完成 AffectedMonths={MonthCount}", affectedMonthFolders.Count);
    }

    public Task RebuildMonthIndexAsync(string month)
    {
        var monthDate = ParseMonth(month);
        return _monthlyLogIndexService.RebuildMonthIndexAsync(_pathManager.GetMonthFolderPath(monthDate));
    }

    private void WriteBulkMonth(DateTime monthDate, string machineType, int planCount, int recordCount, IProgress<int>? progress)
    {
        var monthFolder = _pathManager.GetMonthFolderPath(monthDate);
        Directory.CreateDirectory(monthFolder);
        var written = 0;

        for (var planIndex = 1; planIndex <= planCount; planIndex++)
        {
            var recordsForPlan = recordCount / planCount + (planIndex <= recordCount % planCount ? 1 : 0);
            var versionCount = (int)Math.Ceiling(recordsForPlan / (double)RecordsPerVersion);

            for (var version = 1; version <= versionCount; version++)
            {
                var template = CreateTemplate(planIndex, version);
                var recordsForVersion = Math.Min(RecordsPerVersion, recordsForPlan - ((version - 1) * RecordsPerVersion));
                var baseFileName = _pathManager.GetBaseFileName(machineType, template.PlanName);
                var splitSuffix = version == 1 ? string.Empty : $"({version - 1})";
                var filePath = Path.Combine(monthFolder, $"{baseFileName}{splitSuffix}.csv");

                _logger.LogWarning(
                    "[ZZTEST] 写入模板 Plan={Plan} Version=V{Version} PinCount={PinCount}",
                    template.PlanName, template.PlanVersion, template.Items.Count);

                using (var writer = new StreamWriter(filePath, append: false, new UTF8Encoding(true)))
                {
                    var firstRecord = CreateRecord(machineType, template, written + 1, monthDate, written);
                    writer.WriteLine(CsvRecordFormatter.BuildHeader(firstRecord));

                    for (var i = 0; i < recordsForVersion; i++)
                    {
                        var timestamp = monthDate
                            .AddDays(i % DateTime.DaysInMonth(monthDate.Year, monthDate.Month))
                            .AddSeconds(i);
                        var record = CreateRecord(machineType, template, written + 1, timestamp, written);
                        writer.WriteLine(CsvRecordFormatter.BuildDataRow(record, i + 1));
                        written++;
                        progress?.Report(written);
                    }
                }

                ValidateCsvColumnCount(filePath);
            }
        }
    }

    private static LogRecord CreateRecord(
        string machineType,
        TestPlanTemplate template,
        int serialIndex,
        DateTime timestamp,
        int sampleIndex)
    {
        var isOk = sampleIndex % 5 != 0;
        var pinResults = template.Items.Select((item, index) => new PinResult
        {
            PinName = item.PinName,
            Result = CreateMeasuredValue(item, isOk || index > 0, sampleIndex + index)
        }).ToList();

        return new LogRecord
        {
            Timestamp = timestamp,
            CreatedAt = DateTime.Now,
            Series = machineType,
            MachineType = machineType,
            SerialNumber = $"ZZTEST-SN-{timestamp:yyyyMM}-{serialIndex:000000}",
            PlanName = template.PlanName,
            PlanVersion = template.PlanVersion,
            Operator = DefaultOperator,
            FinalResult = isOk ? "OK" : "NG",
            PinResults = pinResults
        };
    }

    private static string CreateMeasuredValue(TestItemTemplate item, bool isOk, int sampleIndex)
    {
        if (item.ValueKind == TestValueKind.Continuity)
            return isOk ? item.ExpectedContinuity : item.ExpectedContinuity == "SHORT" ? "OPEN" : "SHORT";

        if (!isOk)
            return "1050.000";

        var value = 0.5m + ((sampleIndex % 1000) / 37m);
        return value.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    private static TestPlanTemplate CreateTemplate(int planIndex, int version)
    {
        var planName = $"方案{planIndex}";
        if (planIndex == 1 && version == 1)
        {
            return new(planName, version, new[]
            {
                new TestItemTemplate("A1-B1", TestValueKind.Continuity, "SHORT"),
                new TestItemTemplate("A2-B2", TestValueKind.Continuity, "OPEN"),
                new TestItemTemplate("A3-B3", TestValueKind.Resistance, string.Empty)
            });
        }

        if (planIndex == 1 && version == 2)
        {
            return new(planName, version, new[]
            {
                new TestItemTemplate("B1-B2", TestValueKind.Continuity, "SHORT"),
                new TestItemTemplate("B3-B4", TestValueKind.Continuity, "OPEN"),
                new TestItemTemplate("A3-B3", TestValueKind.Resistance, string.Empty),
                new TestItemTemplate("B5-B6", TestValueKind.Resistance, string.Empty)
            });
        }

        var offset = ((planIndex - 1) * 3) + version;
        return new(planName, version, new[]
        {
            new TestItemTemplate($"A{offset}-A{offset + 1}", TestValueKind.Continuity, "SHORT"),
            new TestItemTemplate($"B{offset}-B{offset + 1}", TestValueKind.Continuity, "OPEN"),
            new TestItemTemplate($"A{offset}-B{offset}", TestValueKind.Resistance, string.Empty)
        });
    }

    private static string NormalizeTestMachineType(string machineType)
    {
        var value = string.IsNullOrWhiteSpace(machineType) ? "ZZTEST-索引机种A" : machineType.Trim();
        if (!value.StartsWith(TestPrefix, StringComparison.OrdinalIgnoreCase))
            value = TestPrefix + value;

        var validationError = NameValidationHelper.ValidateMachineType(value);
        if (validationError != null)
            throw new ArgumentException(validationError, nameof(machineType));

        return value;
    }

    private static void ValidateCsvColumnCount(string filePath)
    {
        using var parser = new TextFieldParser(filePath, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true
        };
        parser.SetDelimiters(",");

        var header = parser.ReadFields() ?? throw new InvalidDataException($"CSV 缺少表头：{filePath}");
        var lineNumber = 1;
        while (!parser.EndOfData)
        {
            lineNumber++;
            var fields = parser.ReadFields() ?? Array.Empty<string>();
            if (fields.Length != header.Length)
                throw new InvalidDataException($"CSV 第 {lineNumber} 行列数为 {fields.Length}，表头列数为 {header.Length}：{filePath}");
        }
    }

    private static DateTime ParseMonth(string month)
    {
        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            throw new ArgumentException("月份格式必须为 yyyy-MM。", nameof(month));

        return new DateTime(result.Year, result.Month, 1);
    }

    private sealed record TestPlanTemplate(string PlanName, int PlanVersion, IReadOnlyList<TestItemTemplate> Items);
    private sealed record TestItemTemplate(string PinName, TestValueKind ValueKind, string ExpectedContinuity);
    private enum TestValueKind { Continuity, Resistance }
}
