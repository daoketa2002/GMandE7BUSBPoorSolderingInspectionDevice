using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>检测配置参数</summary>
public class InspectionConfig
{
    public string PlanName { get; set; } = string.Empty;
    public List<TestPointConfig> TestPoints { get; set; } = new();
    public int RelaySettleTimeMs { get; set; } = 150;
    /// <summary>DT302 继电器动作完成业务等待上限，固定复用 Modbus 分层常量，不作为系统设置项。</summary>
    public int RelaySwitchTimeoutMs { get; set; } = ModbusTimeoutConstants.RelayBusinessWaitMs;

    /// <summary>导通阈值(Ω)，用于导通模式判定 OPEN/SHORT。默认 10Ω，范围 1~1000Ω。</summary>
    public double ContinuityThresholdOhm { get; set; } = 10.0;

    /// <summary>跳过 DT302 继电器动作完成等待。仅半实物联调无夹具或 DT302 反馈未接通时使用。</summary>
    public bool SkipDt302Wait { get; set; }

    /// <summary>
    /// 单项 NG 后是否继续测试后续项目。
    /// true（默认）：记录该项 NG，继续测完整个方案；
    /// false：首个 NG 后停止本轮，等待操作员复位或终了。
    /// </summary>
    public bool ContinueTestingAfterNg { get; set; } = true;
}
