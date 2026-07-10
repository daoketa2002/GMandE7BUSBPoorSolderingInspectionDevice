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
    public string SerialNumber { get; init; } = string.Empty;
    public string SchemeName { get; init; } = string.Empty;
    public string OperatorName { get; init; } = string.Empty;
    public bool IsSchemeNameInvalid { get; init; }
    public bool IsPlcConnected { get; init; }
    public bool IsDmmConnected { get; init; }
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
            or TestUIState.CompletedPass
            or TestUIState.CompletedFail
            or TestUIState.SingleItemNgStopped
            or TestUIState.Error)
        {
            return Fail("当前状态需要先复位，无法启动检测");
        }
        if (request.UiState == TestUIState.EmergencyStop)
            return Fail("设备处于急停状态，请先复位后再启动检测");

        // 基本信息检查
        if (string.IsNullOrWhiteSpace(request.ModelName))
            return Fail("未输入机种名称，无法启动");
        if (string.IsNullOrWhiteSpace(request.SerialNumber))
            return Fail("未输入序列号，无法启动");
        if (string.IsNullOrWhiteSpace(request.SchemeName) || request.IsSchemeNameInvalid)
            return Fail("当前方案无效或不属于当前机种，无法启动");
        if (string.IsNullOrWhiteSpace(request.OperatorName))
            return Fail("未指定作业员，无法启动");

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

            bool leftIsPositive = string.Equals(tp.PinLeftPolarity, PinPolarityConstants.Positive, StringComparison.OrdinalIgnoreCase);
            bool rightIsNegative = string.Equals(tp.PinRightPolarity, PinPolarityConstants.Negative, StringComparison.OrdinalIgnoreCase);
            if (!leftIsPositive || !rightIsNegative)
                return Fail($"第 {i + 1} 项 [{tp.Name}] 极性必须左正右负，当前左={tp.PinLeftPolarity} 右={tp.PinRightPolarity}");

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
