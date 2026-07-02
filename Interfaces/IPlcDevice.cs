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
    /// 清除 PLC 启动请求（写 DT120 = 0）。
    /// PC 在读到 DT120=1 并接管启动后调用，防止同一启动信号重复触发检测。
    /// </summary>
    Task<PlcOperationResult> ClearStartRequestAsync(CancellationToken ct = default);

    /// <summary>
    /// 清除 PLC 复位请求（写 DT121 = 0）。
    /// PC 在完成复位处理后调用。
    /// </summary>
    Task<PlcOperationResult> ClearResetRequestAsync(CancellationToken ct = default);

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
    /// 清空引脚输出寄存器（清 DT130~DT185 全部为 0）。
    /// 单项完成、复位、急停、异常中止后调用。
    /// </summary>
    Task<PlcOperationResult> ClearPinOutputsAsync(CancellationToken ct = default);

    /// <summary>
    /// 通知 PLC 断开引脚输出（写 DT160 = 1 — 旧地址表语义）。
    /// 已废弃，新流程使用 ClearPinOutputsAsync 清寄存器。
    /// PLC 端收到 DT160=1 负责断开继电器并复位 DT160。
    /// </summary>
    Task<PlcOperationResult> RequestRelayDisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// 清空报警解除信号（写 DT303 = 0）。
    /// 用户点击急停弹窗"解除"按钮后调用。
    /// 只清 DT303，不清 DT123。
    /// </summary>
    Task<PlcOperationResult> ClearAlarmReleasedAsync(CancellationToken ct = default);

    /// <summary>
    /// 向上位机异常状态写入 PLC。
    /// 通信失败、万用表无响应等异常时调用。
    /// </summary>
    Task<PlcOperationResult> WritePcErrorAsync(CancellationToken ct = default);

    // ── 旧业务操作（待移除，主检测流程已不再使用） ──

    /// <summary>读取启动信号（M0 线圈状态）</summary>
    Task<PlcOperationResult> ReadStartSignalAsync(CancellationToken ct = default);

    /// <summary>设置忙碌信号 (M4)</summary>
    Task<PlcOperationResult> SetBusyAsync(bool value, CancellationToken ct = default);

    /// <summary>设置 OK 信号 (M1)</summary>
    Task<PlcOperationResult> SetOkAsync(bool value, CancellationToken ct = default);

    /// <summary>设置 NG 信号 (M2)</summary>
    Task<PlcOperationResult> SetNgAsync(bool value, CancellationToken ct = default);

    /// <summary>设置错误信号 (M3)</summary>
    Task<PlcOperationResult> SetErrorAsync(bool value, CancellationToken ct = default);

    /// <summary>选择测试点（写入 D100）</summary>
    Task<PlcOperationResult> SelectTestPointAsync(int testPointIndex, CancellationToken ct = default);

    /// <summary>控制继电器开合（RelayBaseAddress + channel）</summary>
    Task<PlcOperationResult> SetRelayAsync(int channel, bool value, CancellationToken ct = default);

    /// <summary>写入单个测试点结果到保持寄存器</summary>
    Task<PlcOperationResult> WriteTestResultAsync(int index, ushort resultValue, CancellationToken ct = default);

    // ── 事件 ──

    /// <summary>PLC 通信通知事件（连接丢失、重连成功、写入失败等）</summary>
    event EventHandler<PlcNotification>? NotificationReceived;

    /// <summary>PLC 报警状态变更事件（预留，第一阶段不触发）</summary>
    event EventHandler<AlarmState>? AlarmStateChanged;
}
