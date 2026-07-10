using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;

/// <summary>
/// PLC 设备抽象接口 — 业务层唯一 PLC 入口。
/// 继承 ICommunicationDevice 保持与 DeviceConnectionManager 兼容，
/// 并扩展检测业务方法和事件。
///
/// 接口语义：
/// - 所有业务方法返回 PlcOperationResult，调用方不直接处理 Modbus 异常
/// - 读取 PLC 状态使用 PlcOperationResult&lt;PlcMachineInputs&gt; 表达成功/失败
/// - 关键写入使用 Warning 级别日志审计
/// </summary>
public interface IPlcDevice : ICommunicationDevice
{
    // ── 配置 ──

    /// <summary>应用 FP0H 通信配置（在 ConnectAsync 之前调用）</summary>
    void ApplyConfig(FP0HCommunicationConfig config);

    // ── 新业务操作（本机检测流程） ──

    /// <summary>
    /// 读取 PLC 机器输入信号快照。
    /// 一次性读出 DT120/121/122/123/302/303 等信号，组装为 PlcMachineInputs。
    /// 取代逐信号读取，减少 Modbus 通信次数。
    /// </summary>
    Task<PlcOperationResult<PlcMachineInputs>> ReadMachineInputsAsync(CancellationToken ct = default);

    /// <summary>
    /// 上位机请求启动（写 DT120=1）。
    /// 半实物联调临时入口：真实 PLC 接入但现场启动按钮链路未完整联通时，
    /// 由上位机临时写 DT120=1 触发现有 PLC 轮询启动流程。
    /// 正式整机联调后应删除，启动应由 PLC 或实体按钮触发。
    /// </summary>
    Task<PlcOperationResult> RequestStartAsync(CancellationToken ct = default);

    /// <summary>
    /// 清除 PLC 启动请求（写 DT120 = 0）。
    /// 正常完成时，在整轮检测结束且用户处理保存/取消弹窗后调用。
    /// 启动复核失败、复位或异常安全收口时也可以调用，避免无效启动请求残留。
    /// </summary>
    Task<PlcOperationResult> ClearStartRequestAsync(CancellationToken ct = default);

    /// <summary>
    /// 清除 PLC 复位请求（写 DT121 = 0）。
    /// PC 在完成复位处理后调用。
    /// </summary>
    Task<PlcOperationResult> ClearResetRequestAsync(CancellationToken ct = default);

    /// <summary>
    /// 上位机请求复位（写 DT121=1）。
    /// 半实物联调临时入口：真实 PLC 接入但现场复位按钮链路未完整联通时，
    /// 由上位机临时写 DT121=1 触发现有复位流程。
    /// 正式整机联调后应删除，复位应由 PLC/实体按钮触发。
    /// </summary>
    Task<PlcOperationResult> RequestResetAsync(CancellationToken ct = default);

    /// <summary>
    /// 上位机请求停止（写 DT122=1）。
    /// 半实物联调临时入口，用于模拟 PLC 停止信号。
    /// </summary>
    Task<PlcOperationResult> RequestStopAsync(CancellationToken ct = default);

    /// <summary>
    /// 清除 PLC 停止请求（写 DT122=0）。
    /// </summary>
    Task<PlcOperationResult> ClearStopRequestAsync(CancellationToken ct = default);

    /// <summary>
    /// 上位机请求急停（写 DT123=1）。
    /// 半实物联调临时入口，用于模拟 PLC 急停信号。
    /// </summary>
    Task<PlcOperationResult> RequestEmergencyStopAsync(CancellationToken ct = default);

    /// <summary>
    /// 清除 PLC 急停请求（写 DT123=0）。
    /// 急停解除时调用。
    /// </summary>
    Task<PlcOperationResult> ClearEmergencyStopRequestAsync(CancellationToken ct = default);

    /// <summary>
    /// 写入当前测试点的两个引脚到 PLC（最新地址表：DT130~DT185 每脚独立选择区）。
    /// 每个引脚写入连续 2 个寄存器：Select=1, Polarity=极性值。
    /// 极性编码：正极=0，负极=1。
    /// </summary>
    Task<PlcOperationResult> WriteCurrentTestPointAsync(
        string leftPinName,
        string rightPinName,
        ushort leftPolarityCode,
        ushort rightPolarityCode,
        CancellationToken ct = default);

    /// <summary>
    /// 等待 PLC 继电器切换完成（读 DT302=1）。
    /// 超时未完成返回失败。
    /// </summary>
    Task<PlcOperationResult> WaitRelaySwitchCompletedAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// 写入上位机允许开始检测信号（写 DT234 = 1）。
    /// 启动前检查通过后调用。
    /// </summary>
    Task<PlcOperationResult> WritePcReadyAsync(CancellationToken ct = default);

    /// <summary>
    /// 清除上位机允许开始检测信号（写 DT234 = 0）。
    /// 检测结束或复位时调用。
    /// </summary>
    Task<PlcOperationResult> ClearPcReadyAsync(CancellationToken ct = default);

    /// <summary>
    /// 写入单点检测结果到 PLC（PointResultBaseRegister + pointIndex）。
    /// pointIndex: 项目索引（0-based）
    /// isOk: true=OK, false=NG
    /// </summary>
    Task<PlcOperationResult> WritePointResultAsync(int pointIndex, bool isOk, CancellationToken ct = default);

    /// <summary>
    /// 写入综合检测结果到 PLC。
    /// 写 DT304/DT305：全部 OK → DT304=1, DT305=0；存在 NG → DT304=0, DT305=1
    /// </summary>
    Task<PlcOperationResult> WriteFinalResultAsync(bool isOk, int? ngPointIndex, CancellationToken ct = default);

    /// <summary>
    /// 清空产品综合结果（写 DT304=0, DT305=0）。
    /// 复位或下一轮准备时调用，表示当前无产品结果。
    /// 不可用 WriteFinalResultAsync(false, null) 替代，因为 false 语义为"NG"，不是"无结果"。
    /// </summary>
    Task<PlcOperationResult> ClearFinalResultAsync(CancellationToken ct = default);

    /// <summary>
    /// 清空引脚输出寄存器（清 DT130~DT185 全部为 0）。
    /// 单项完成、复位、急停、异常中止后调用。
    /// </summary>
    Task<PlcOperationResult> ClearPinOutputsAsync(CancellationToken ct = default);

    /// <summary>
    /// 清空继电器动作完成标志（写 DT302 = 0）。
    /// 当前测试点完成读取万用表并判定后调用，表示上位机已取走结果。
    /// 在下一项开始前、复位、停止、急停、异常中止路径中也必须清 DT302。
    /// </summary>
    Task<PlcOperationResult> ClearRelayActionCompletedAsync(CancellationToken ct = default);

    /// <summary>
    /// 半实物/Fake 调试请求报警解除（写 DT303 = 1）。
    /// 仅用于调试按钮模拟 PLC 已解除报警；正式真实模式应由 PLC 侧写入 DT303。
    /// </summary>
    Task<PlcOperationResult> RequestAlarmReleaseAsync(CancellationToken ct = default);

    /// <summary>
    /// 清空报警解除信号（写 DT303 = 0）。
    /// 用户点击急停弹窗"解除"按钮后调用。
    /// 最新流程中急停解除会先清 DT123，再清 DT303。
    /// </summary>
    Task<PlcOperationResult> ClearAlarmReleasedAsync(CancellationToken ct = default);

    /// <summary>
    /// 上位机请求终了（写 DT306=1）。
    /// 终了按钮触发，返回主菜单后延时清 0。
    /// </summary>
    Task<PlcOperationResult> RequestTerminateAsync(CancellationToken ct = default);

    /// <summary>
    /// 清除终了请求（写 DT306=0）。
    /// 返回主菜单后延时调用。
    /// </summary>
    Task<PlcOperationResult> ClearTerminateRequestAsync(CancellationToken ct = default);

    /// <summary>
    /// 向上位机异常状态写入 PLC。
    /// 通信失败、万用表无响应等异常时调用。
    /// </summary>
    Task<PlcOperationResult> WritePcErrorAsync(CancellationToken ct = default);

    // ── 阶段 D 新增：拆分读取职责 ──

    /// <summary>
    /// 读取 PLC 控制信号快照（DT120~DT123）。
    /// 一次 FC03 读取 4 个保持寄存器，返回启动/复位/停止/急停四路信号。
    /// 取代高频路径中的 ReadMachineInputsAsync 调用。
    /// </summary>
    Task<PlcOperationResult<PlcControlSignals>> ReadControlSignalsAsync(CancellationToken ct = default);

    /// <summary>
    /// 读取继电器动作完成标志（DT302 单寄存器）。
    /// 仅用于 InspectionEngine.WaitRelaySwitchCompletedAsync 的内部轮询。
    /// </summary>
    Task<PlcOperationResult<bool>> ReadRelayCompletedAsync(CancellationToken ct = default);

    /// <summary>
    /// 读取报警解除信号（DT303 单寄存器）。
    /// 仅用于 EmergencyStopDialogViewModel 的 500ms 独立轮询。
    /// </summary>
    Task<PlcOperationResult<bool>> ReadAlarmReleasedAsync(CancellationToken ct = default);

    // ── 事件 ──

    /// <summary>PLC 通信通知事件（连接丢失、重连成功、写入失败等）</summary>
    event EventHandler<PlcNotification>? NotificationReceived;

    /// <summary>PLC 报警状态变更事件（预留，第一阶段不触发）</summary>
    event EventHandler<AlarmState>? AlarmStateChanged;
}
