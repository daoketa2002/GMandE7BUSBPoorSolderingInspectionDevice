namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

/// <summary>
/// PLC 启动前的左右工位安装拒绝代码快照。
/// 代码必须保留 1 和 2 的区别，供运行页显示具体传感器提示。
/// </summary>
public sealed class WorkstationInstallRejectSignals
{
    /// <summary>DT309 左工位拒绝代码。</summary>
    public ushort LeftCode { get; init; }

    /// <summary>DT310 右工位拒绝代码。</summary>
    public ushort RightCode { get; init; }
}
