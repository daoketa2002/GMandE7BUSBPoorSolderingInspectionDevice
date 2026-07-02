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
    /// 一次性读出 DT120/121/122/123/161 等信号，组装为 PlcMachineInputs。
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
    /// PC 在完成复位处理后调用（已写 DT160=1 通知断开引脚输出后）。
    /// </summary>
    Task<PlcOperationResult> ClearResetRequestAsync(CancellationToken ct = default);

    /// <summary>
    /// 写入当前测试点的左右引脚编号到 PLC。
    /// PLC 收到编号后控制对应继电器闭合引脚回路。
    /// leftPinCode / rightPinCode 编码规则：A{n}=n, B{n}=20+n
    /// </summary>
    Task<PlcOperationResult> WriteCurrentTestPinsAsync(ushort leftPinCode, ushort rightPinCode, CancellationToken ct = default);

    /// <summary>
    /// 写入当前测试点的左右引脚编号和左右极性到 DT130~DT133。
    /// 极性编码按最小闭环规则：正极=0，负极=1。
    /// </summary>
    Task<PlcOperationResult> WriteCurrentTestPointAsync(
        ushort leftPinCode,
        ushort rightPinCode,
        ushort leftPolarityCode,
        ushort rightPolarityCode,
        CancellationToken ct = default);

    /// <summary>
    /// 等待 PLC 继电器切换完成。
    /// 通过轮询 RelaySwitchCompletedRegister 判断继电器是否闭合到位。
    /// 超时未完成返回失败。
    /// </summary>
    Task<PlcOperationResult> WaitRelaySwitchCompletedAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// 写入单点检测结果到 PLC。
    /// pointIndex: 项目索引（0-based）
    /// isOk: true=OK, false=NG
    /// </summary>
    Task<PlcOperationResult> WritePointResultAsync(int pointIndex, bool isOk, CancellationToken ct = default);

    /// <summary>
    /// 写入综合检测结果到 PLC。
    /// isOk: true=所有项目OK, false=存在NG
    /// ngPointIndex: 第一个NG项目的索引（isOk=false时有效），null 表示无NG
    /// </summary>
    Task<PlcOperationResult> WriteFinalResultAsync(bool isOk, int? ngPointIndex, CancellationToken ct = default);

    /// <summary>
    /// 通知 PLC 断开引脚输出（写 DT160 = 1）。
    /// 测试完成、检测中止或复位时调用。
    /// PLC 收到后负责断开当前继电器并复位 DT160。
    /// </summary>
    Task<PlcOperationResult> RequestRelayDisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// 向上位机异常状态写入 PLC。
    /// 通信失败、板离设备报警、万用表无响应等异常时调用。
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
