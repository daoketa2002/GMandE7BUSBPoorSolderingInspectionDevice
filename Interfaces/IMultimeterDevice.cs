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
        /// 初始化为四线电阻测量模式。最小闭环统一使用四线电阻模式读取原始值。
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

        /// <summary>
        /// 导通测量专用读取（SCPI: MEAS:CONT?）
        /// 返回导通电阻值（原始字符串，如 "+1.05000000E+01"、"OPEN"、"SHORT"）
        /// </summary>
        Task<string> ReadContinuityRawAsync(CancellationToken ct = default);

        /// <summary>
        /// 恢复万用表为远程可控的 4 线电阻空闲态。
        /// 指令：ABOR; *CLS; CONF:FRES; SENS:FRES:RANG:AUTO ON; SAMP:COUN 1; TRIG:COUN 1; TRIG:SOUR IMM
        /// 调用时机：正常检测结束且保存/取消弹窗关闭后。
        /// </summary>
        Task<bool> PrepareIdleResistanceModeAsync(CancellationToken ct = default);

        /// <summary>
        /// 退出远程控制，返回本地面板操作。
        /// 指令：SYST:LOC
        /// 调用时机：终了、退出运行页、急停、异常中止、程序关闭。
        /// </summary>
        Task ReleaseToLocalAsync(CancellationToken ct = default);

        /// <summary>
        /// 轻量级通信验证。发送 *IDN? 并检查是否有非空响应。
        /// 用于启动复核阶段快速判断万用表是否真正在线，避免仅依赖连接标志。
        /// 超时时间建议 ≤ 500ms，不应阻塞 UI 轮询。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>true=万用表可通信，false=无响应或超时</returns>
        Task<bool> PingAsync(CancellationToken ct = default);
    }
}
