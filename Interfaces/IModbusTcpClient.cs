using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.TCP报文相关;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

/// <summary>
/// 通用 Modbus TCP 通信接口。
/// 只关心连接、断开和 Modbus 标准操作（FC 0x01~0x10），不包含 FP0H 业务含义。
/// 此接口供 IPlcDevice 实现类内部使用，不直接注入 ViewModel。
/// </summary>
public interface IModbusTcpClient
{
    bool IsConnected { get; }

    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>PLC 通信通知（连接丢失、重连成功、写入失败等）</summary>
    event EventHandler<PlcNotification>? NotificationReceived;

    // ── 连接生命周期 ──

    /// <summary>根据给定选项建立 TCP 连接并验证 Modbus 通信</summary>
    Task<bool> ConnectAsync(ModbusTcpClientOptions options, CancellationToken ct = default);

    /// <summary>断开 TCP 连接</summary>
    Task DisconnectAsync();

    // ── Modbus 标准操作 ──

    /// <summary>读线圈 (FC 0x01)</summary>
    Task<ModbusResponse?> ReadCoilsAsync(byte unitId, ushort startAddress, ushort quantity, CancellationToken ct = default, int timeoutMs = 1000);

    /// <summary>读保持寄存器 (FC 0x03)</summary>
    Task<ModbusResponse?> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort quantity, CancellationToken ct = default, int timeoutMs = 1000);

    /// <summary>写单线圈 (FC 0x05)</summary>
    Task<ModbusResponse?> WriteSingleCoilAsync(byte unitId, ushort address, bool value, CancellationToken ct = default, int timeoutMs = 1000);

    /// <summary>写单保持寄存器 (FC 0x06)</summary>
    Task<ModbusResponse?> WriteSingleRegisterAsync(byte unitId, ushort address, ushort value, CancellationToken ct = default, int timeoutMs = 1000);

    /// <summary>写多保持寄存器 (FC 0x10)</summary>
    Task<ModbusResponse?> WriteMultipleRegistersAsync(byte unitId, ushort startAddress, ushort[] values, CancellationToken ct = default, int timeoutMs = 1000);

    /// <summary>发送自定义 Modbus TCP 请求帧并等待匹配事务 ID 的响应</summary>
    Task<ModbusResponse?> SendCustomRequestAsync(byte[] requestFrame, CancellationToken ct = default, int timeoutMs = 1000);
}
