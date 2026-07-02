namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

/// <summary>
/// PLC 地址映射，集中管理本机检测流程使用的 DT 寄存器地址。
/// 未确认的扩展地址使用 ushort? 表达，避免把 0 当成有效地址。
/// </summary>
public sealed class PlcAddressMap
{
    public const ushort StartSignal = 120;
    public const ushort ResetSignal = 121;
    public const ushort StopSignal = 122;
    public const ushort EmergencyStopSignal = 123;

    public const ushort LeftPinNumber = 130;
    public const ushort RightPinNumber = 131;
    public const ushort LeftPinPolarity = 132;
    public const ushort RightPinPolarity = 133;

    public const ushort RelayClosedCompleted = 160;
    public const ushort BoardRemovedAlarm = 161;

    /// <summary>DT120：PLC 请求启动，PC 接管后写 0 清除。</summary>
    public ushort StartRequestRegister { get; init; } = StartSignal;

    /// <summary>DT121：复位请求，PC 完成复位处理后写 0 清除。</summary>
    public ushort ResetRegister { get; init; } = ResetSignal;

    /// <summary>DT122：停止信号，PLC 写，PC 只读。</summary>
    public ushort StopRegister { get; init; } = StopSignal;

    /// <summary>DT123：急停信号，PLC 写，PC 只读。</summary>
    public ushort EmergencyStopRegister { get; init; } = EmergencyStopSignal;

    /// <summary>DT160：最小闭环中表示 PLC 已完成引脚闭合，PC 只读。</summary>
    public ushort RelayDisconnectRegister { get; init; } = RelayClosedCompleted;

    /// <summary>DT161：测试过程中板离设备报警，PLC 写，PC 只读。</summary>
    public ushort BoardLeavingAlarmRegister { get; init; } = BoardRemovedAlarm;

    /// <summary>DT130：左引脚编号。</summary>
    public ushort? LeftPinCodeRegister { get; init; } = LeftPinNumber;

    /// <summary>DT131：右引脚编号。</summary>
    public ushort? RightPinCodeRegister { get; init; } = RightPinNumber;

    /// <summary>DT132：左引脚极性，正极=0，负极=1。</summary>
    public ushort? LeftPinPolarityRegister { get; init; } = LeftPinPolarity;

    /// <summary>DT133：右引脚极性，正极=0，负极=1。</summary>
    public ushort? RightPinPolarityRegister { get; init; } = RightPinPolarity;

    /// <summary>DT160：PLC 完成继电器闭合后置 1。</summary>
    public ushort? RelaySwitchCompletedRegister { get; init; } = RelayClosedCompleted;

    /// <summary>单点结果基地址，未确认时保持 null。</summary>
    public ushort? PointResultBaseRegister { get; init; }

    /// <summary>综合结果寄存器，未确认时保持 null。</summary>
    public ushort? FinalResultRegister { get; init; }

    /// <summary>NG 项目编号寄存器，未确认时保持 null。</summary>
    public ushort? NgPointIndexRegister { get; init; }

    /// <summary>报警代码寄存器，未确认时保持 null。</summary>
    public ushort? AlarmCodeRegister { get; init; }

    /// <summary>
    /// 将方案中的 Pin 名称转换为 PLC 使用的点位序号。
    /// 规则：A1~A12 -> 1~12，B1~B12 -> 21~32。
    /// </summary>
    public static ushort ConvertPinNameToNumber(string pinName)
    {
        if (string.IsNullOrWhiteSpace(pinName))
            throw new ArgumentException("Pin 名称不能为空。", nameof(pinName));

        string normalized = pinName.Trim().ToUpperInvariant();
        if (normalized.Length < 2)
            throw new ArgumentException($"Pin 格式错误：{pinName}", nameof(pinName));

        char group = normalized[0];
        if (!int.TryParse(normalized[1..], out int number))
            throw new ArgumentException($"Pin 编号不是数字：{pinName}", nameof(pinName));

        return group switch
        {
            'A' when number is >= 1 and <= 12 => (ushort)number,
            'B' when number is >= 1 and <= 12 => (ushort)(20 + number),
            _ => throw new ArgumentException($"Pin 超出范围或分组错误：{pinName}", nameof(pinName))
        };
    }

    /// <summary>
    /// 将方案中的极性转换为 PLC 数值。最小闭环规则：正极=0，负极=1。
    /// </summary>
    public static ushort ConvertPolarityToValue(string? polarity)
    {
        return polarity switch
        {
            GMandE7BUSBPoorSolderingInspectionDevice.Models.PinPolarityConstants.Positive => 0,
            GMandE7BUSBPoorSolderingInspectionDevice.Models.PinPolarityConstants.Negative => 1,
            _ => throw new ArgumentException($"未知极性：{polarity}", nameof(polarity))
        };
    }
}
