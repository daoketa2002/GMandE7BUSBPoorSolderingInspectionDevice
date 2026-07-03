// ============================================================
// 文件: Interfaces/Devices/IMultimeterDevice.cs
// 描述: 万用表设备抽象接口
// 继承 ICommunicationDevice，DI 容器可通过此接口区分万用表
// ============================================================

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices
{
    /// <summary>
    /// 万用表设备抽象接口
    /// 继承通用通信设备接口，无额外成员
    /// 唯一目的是让 DI 容器能区分万用表和 PLC
    /// </summary>
    public interface IMultimeterDevice : ICommunicationDevice
    {
        /// <summary>
        /// 初始化为电阻测量模式。最小闭环统一使用电阻模式读取原始值。
        /// </summary>
        Task<bool> InitializeResistanceModeAsync(CancellationToken ct = default);

        /// <summary>
        /// 初始化为导通测量模式（Continuity），并设置导通阈值。
        /// SCPI: CONF:CONT + SENS:CONT:THR {thresholdOhm}
        /// </summary>
        /// <param name="thresholdOhm">导通阈值(Ω)，默认 10Ω，范围 1~1000Ω</param>
        Task<bool> InitializeContinuityModeAsync(double thresholdOhm = 10.0, CancellationToken ct = default);

        /// <summary>
        /// 读取万用表原始返回文本，例如 "+1.05000000E+01"、"OPEN"、"SHORT"。
        /// 判定逻辑由 InspectionEngine 负责，驱动层不做业务判断。
        /// </summary>
        Task<string> ReadResistanceRawAsync(CancellationToken ct = default);
    }
}
