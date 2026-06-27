using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;

/// <summary>
/// PLC 设备抽象接口 — 业务层唯一 PLC 入口。
/// 继承 ICommunicationDevice 保持与 DeviceConnectionManager 兼容，
/// 并扩展检测业务方法和事件。
/// </summary>
public interface IPlcDevice : ICommunicationDevice
{
    // ── 配置 ──

    /// <summary>应用 FP0H 通信配置（在 ConnectAsync 之前调用）</summary>
    void ApplyConfig(FP0HCommunicationConfig config);

    // ── 业务操作 ──
    // 全部返回 PlcOperationResult，调用方不直接处理 Modbus 异常

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

    /// <summary>
    /// 选择测试点（写入测试点编号到 D100 寄存器）。
    /// testPointIndex 从 0 开始，写入 PLC 的值为 index + 1。
    /// </summary>
    Task<PlcOperationResult> SelectTestPointAsync(int testPointIndex, CancellationToken ct = default);

    /// <summary>控制继电器开合（RelayBaseAddress + channel, FC 0x05）</summary>
    Task<PlcOperationResult> SetRelayAsync(int channel, bool value, CancellationToken ct = default);

    /// <summary>写入单个测试点结果到保持寄存器 (TestResultBaseRegister + index)</summary>
    Task<PlcOperationResult> WriteTestResultAsync(int index, ushort resultValue, CancellationToken ct = default);

    // ── 事件 ──

    /// <summary>PLC 通信通知事件（连接丢失、重连成功、写入失败等）</summary>
    event EventHandler<PlcNotification>? NotificationReceived;

    /// <summary>PLC 报警状态变更事件（预留，第一阶段不触发）</summary>
    event EventHandler<AlarmState>? AlarmStateChanged;
}