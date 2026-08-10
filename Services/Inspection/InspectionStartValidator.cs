using GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.Inspection;

/// <summary>启动校验结果</summary>
public sealed record InspectionValidationResult(bool IsValid, string? ErrorMessage);

/// <summary>启动校验请求参数</summary>
public sealed class InspectionStartValidationRequest
{
    public TestUIState UiState { get; init; }
    public string ModelName { get; init; } = string.Empty;
    /// <summary>实际机种与已确认参照机种不一致时，禁止任何来源启动。</summary>
    public bool IsReferenceMachineMismatch { get; init; }
    public string SerialNumber { get; init; } = string.Empty;
    public string SchemeName { get; init; } = string.Empty;
    public string OperatorName { get; init; } = string.Empty;
    public bool IsSchemeNameInvalid { get; init; }
    public bool IsPlcConnected { get; init; }
    public bool IsDmmConnected { get; init; }
    /// <summary>方案列表或检测项目仍在异步加载时，不允许依据旧数据启动。</summary>
    public bool IsPlanLoading { get; init; }
    /// <summary>当前机种和序列号组合是否已完成本轮重复记录确认。</summary>
    public bool IsCurrentSerialConfirmed { get; init; }
    /// <summary>最近一个月重复记录查询是否仍在进行。</summary>
    public bool IsDuplicateCheckInProgress { get; init; }
    /// <summary>是否正在等待操作员处理重复测试提醒。</summary>
    public bool IsDuplicateDecisionPending { get; init; }
    /// <summary>最近一个月重复记录查询是否失败。</summary>
    public bool IsDuplicateCheckFailed { get; init; }
    /// <summary>已有运行控制动作时，禁止并发启动。</summary>
    public bool IsControlActionInProgress { get; init; }
    /// <summary>检测引擎已运行时，禁止重复启动。</summary>
    public bool IsInspectionEngineRunning { get; init; }
    public PlcControlSignals? PlcInputs { get; init; }
    public InspectionConfig Config { get; init; } = new();
    public int UiItemCount { get; init; }
}

/// <summary>启动条件校验器（纯静态工具类，第一轮不抽接口）</summary>
public static class InspectionStartValidator
{
    /// <summary>
    /// 校验启动条件。返回 IsValid=true 允许启动，否则返回错误信息。
    /// </summary>
    public static InspectionValidationResult Validate(InspectionStartValidationRequest request)
    {
        // UI 状态检查
        if (request.UiState is TestUIState.Testing
            or TestUIState.Paused
            or TestUIState.AwaitingReset
            or TestUIState.Resetting
            or TestUIState.Error)
        {
            return Fail("当前状态需要先复位，无法启动检测");
        }
        if (request.UiState == TestUIState.EmergencyStop)
            return Fail("设备处于急停状态，请先复位后再启动检测");

        if (request.IsControlActionInProgress)
            return Fail("系统正在处理其他动作，请等待当前操作完成");
        if (request.IsInspectionEngineRunning)
            return Fail("检测已在运行中，无需重复启动");
        // 基本信息检查
        if (string.IsNullOrWhiteSpace(request.ModelName))
            return Fail("未输入机种名称，无法启动");
        if (request.IsReferenceMachineMismatch)
            return Fail("当前机种与参照机种不一致，无法启动检测");
        if (!InputValidationHelper.IsValidSerialNumber(request.SerialNumber))
            return Fail("本轮机种名称和序列号尚未确认。请先扫码或手动输入机种名称和序列号，再重新启动。");
        if (request.IsDuplicateCheckInProgress)
            return Fail("正在检查该机种和序列号的历史测试记录，请稍后重新启动。");
        if (request.IsDuplicateDecisionPending)
            return Fail("请先处理当前的重复测试提醒，再重新启动。");
        if (request.IsDuplicateCheckFailed)
            return Fail("机种和序列号历史记录检查失败，请重新输入后再试。");
        if (!request.IsCurrentSerialConfirmed)
            return Fail("本轮机种名称和序列号尚未确认。请先扫码或手动输入机种名称和序列号，再重新启动。");
        if (request.IsPlanLoading)
            return Fail("当前方案仍在加载，请等待加载完成后再启动");
        if (string.IsNullOrWhiteSpace(request.SchemeName) || request.IsSchemeNameInvalid)
            return Fail("当前方案无效或不属于当前机种，无法启动");
        if (!InputValidationHelper.IsValidOperatorName(request.OperatorName))
            return Fail("作业员名称为空、超长或包含控制字符，无法启动");

        // 设备就绪检查
        if (!request.IsPlcConnected)
            return Fail("PLC 未就绪，无法启动");
        if (!request.IsDmmConnected)
            return Fail("万用表未就绪，无法启动");

        // PLC 控制信号检查
        if (request.PlcInputs != null)
        {
            if (request.PlcInputs.IsResetRequested)
                return Fail("PLC 复位信号(DT121)未释放，无法启动");
            if (request.PlcInputs.IsStopRequested)
                return Fail("PLC 停止信号(DT122)未释放，无法启动");
            if (request.PlcInputs.IsEmergencyStop)
                return Fail("PLC 急停信号(DT123)未释放，无法启动");
        }

        // 引脚列表非空
        if (request.UiItemCount == 0)
            return Fail("当前方案没有可检测项目，无法启动");

        // 详细引脚合法性检查
        if (request.Config.TestPoints.Count == 0)
            return Fail("检测配置无测试点，无法启动");

        for (int i = 0; i < request.Config.TestPoints.Count; i++)
        {
            var tp = request.Config.TestPoints[i];

            if (string.IsNullOrWhiteSpace(tp.PinLeft))
                return Fail($"第 {i + 1} 项 [{tp.Name}] 左引脚为空，无法启动");
            if (string.IsNullOrWhiteSpace(tp.PinRight))
                return Fail($"第 {i + 1} 项 [{tp.Name}] 右引脚为空，无法启动");

            try
            {
                PlcAddressMap.GetPinAddresses(tp.PinLeft);
                PlcAddressMap.GetPinAddresses(tp.PinRight);
            }
            catch (ArgumentException ex)
            {
                return Fail($"第 {i + 1} 项 [{tp.Name}] 引脚无效：{ex.Message}");
            }

            if (string.Equals(tp.PinLeft, tp.PinRight, StringComparison.OrdinalIgnoreCase))
                return Fail($"第 {i + 1} 项 [{tp.Name}] 左右引脚相同({tp.PinLeft})，无法启动");

            bool leftPolarityIsValid = string.Equals(tp.PinLeftPolarity, PinPolarityConstants.Positive, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tp.PinLeftPolarity, PinPolarityConstants.Negative, StringComparison.OrdinalIgnoreCase);
            if (!leftPolarityIsValid)
                return Fail($"第 {i + 1} 项 [{tp.Name}] 左极性无效：{tp.PinLeftPolarity}");

            bool rightPolarityIsValid = string.Equals(tp.PinRightPolarity, PinPolarityConstants.Positive, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tp.PinRightPolarity, PinPolarityConstants.Negative, StringComparison.OrdinalIgnoreCase);
            if (!rightPolarityIsValid)
                return Fail($"第 {i + 1} 项 [{tp.Name}] 右极性无效：{tp.PinRightPolarity}");

            // PLC 接线允许左右互换，但两个引脚必须保持一正一负。
            if (string.Equals(tp.PinLeftPolarity, tp.PinRightPolarity, StringComparison.OrdinalIgnoreCase))
                return Fail($"第 {i + 1} 项 [{tp.Name}] 左右引脚必须一正一负，当前左={tp.PinLeftPolarity} 右={tp.PinRightPolarity}");

            if (string.Equals(tp.CheckMode, CheckModeConstants.Resistance, StringComparison.OrdinalIgnoreCase))
            {
                if (!tp.LowerLimit.HasValue)
                    return Fail($"第 {i + 1} 项 [{tp.Name}] 电阻模式下限为空，无法启动");
                if (!tp.UpperLimit.HasValue)
                    return Fail($"第 {i + 1} 项 [{tp.Name}] 电阻模式上限为空，无法启动");
                if (InputValidationHelper.ValidateResistanceValue(tp.LowerLimit.Value) != null)
                    return Fail($"第 {i + 1} 项 [{tp.Name}] 电阻模式下限({tp.LowerLimit})无效，无法启动");
                if (InputValidationHelper.ValidateResistanceValue(tp.UpperLimit.Value) != null)
                    return Fail($"第 {i + 1} 项 [{tp.Name}] 电阻模式上限({tp.UpperLimit})无效，无法启动");
                if (tp.LowerLimit.Value > tp.UpperLimit.Value)
                    return Fail($"第 {i + 1} 项 [{tp.Name}] 电阻模式下限({tp.LowerLimit})大于上限({tp.UpperLimit})，无法启动");
            }

            if (string.Equals(tp.CheckMode, CheckModeConstants.Continuity, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(tp.ModeValue, "OPEN", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(tp.ModeValue, "SHORT", StringComparison.OrdinalIgnoreCase))
                    return Fail($"第 {i + 1} 项 [{tp.Name}] 导通模式期望值无效(期望 OPEN 或 SHORT)，当前={tp.ModeValue}");
            }
        }

        return new InspectionValidationResult(true, null);
    }

    private static InspectionValidationResult Fail(string message)
        => new(false, message);
}
