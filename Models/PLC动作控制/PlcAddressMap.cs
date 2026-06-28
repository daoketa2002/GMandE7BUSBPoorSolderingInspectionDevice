namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

/// <summary>
/// PLC 地址映射（松下 FP0H 本机信号表）
/// 集中管理检测流程使用的保持寄存器数据地址（DT），不涉及线圈地址。
///
/// 地址来源：本机 PLC 信号表（已确认 DT120/121/122/123/160/161）
/// 未确认地址使用 ushort? 表达"未配置"，不允许默认 0。
/// </summary>
public sealed class PlcAddressMap
{
    // ═══════════════════════════════════════════════════════════════
    //  已确认地址（非空）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>DT120: 启动请求。PLC 置 1 请求启动，PC 接管后写 0 清除</summary>
    public ushort StartRequestRegister { get; init; } = 120;

    /// <summary>DT121: 复位请求。PLC/上位均可置 1，PC 处理完毕后写 0 清除</summary>
    public ushort ResetRegister { get; init; } = 121;

    /// <summary>DT122: 停止。PLC 置位，PC 只读</summary>
    public ushort StopRegister { get; init; } = 122;

    /// <summary>DT123: 急停。PLC 置位，PC 只读</summary>
    public ushort EmergencyStopRegister { get; init; } = 123;

    /// <summary>DT160: 测试完成上位通知 PLC 断开引脚输出。PC 写，PLC 读并复位</summary>
    public ushort RelayDisconnectRegister { get; init; } = 160;

    /// <summary>DT161: 测试过程中板离设备报警。PLC 置位，PC 只读</summary>
    public ushort BoardLeavingAlarmRegister { get; init; } = 161;

    // ═══════════════════════════════════════════════════════════════
    //  未确认地址（可空 — 与电气确认后再赋值）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>左引脚编号寄存器（如 DT130）</summary>
    public ushort? LeftPinCodeRegister { get; init; }

    /// <summary>右引脚编号寄存器（如 DT131）</summary>
    public ushort? RightPinCodeRegister { get; init; }

    /// <summary>继电器切换完成标志（如 DT150）</summary>
    public ushort? RelaySwitchCompletedRegister { get; init; }

    /// <summary>单点结果基地址（如 DT200+，按索引偏移）</summary>
    public ushort? PointResultBaseRegister { get; init; }

    /// <summary>综合结果寄存器（如 DT210）</summary>
    public ushort? FinalResultRegister { get; init; }

    /// <summary>NG 项目编号寄存器（如 DT211）</summary>
    public ushort? NgPointIndexRegister { get; init; }

    /// <summary>报警代码寄存器（如 DT212）</summary>
    public ushort? AlarmCodeRegister { get; init; }
}
