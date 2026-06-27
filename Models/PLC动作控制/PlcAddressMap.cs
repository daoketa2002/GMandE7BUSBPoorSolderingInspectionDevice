namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

/// <summary>
/// PLC 地址映射（松下 FP0H Modbus 地址）
/// 集中管理检测流程中使用的线圈地址和保持寄存器地址，
/// 后续可从设置文件读取以实现地址可配置。
/// ⚠️ 请根据实际 PLC 程序修改默认值
/// </summary>
public class PlcAddressMap
{
    // ── 线圈地址 (FC 0x01 读取 / FC 0x05 写入) ──

    /// <summary>M0: 启动信号（PLC 置位后上位机开始检测）</summary>
    public ushort StartFlag { get; set; } = 0;

    /// <summary>M1: OK 信号（全部测试点通过后置位）</summary>
    public ushort OkFlag { get; set; } = 1;

    /// <summary>M2: NG 信号（存在不良测试点时置位）</summary>
    public ushort NgFlag { get; set; } = 2;

    /// <summary>M3: 错误信号（检测流程异常时置位）</summary>
    public ushort ErrorFlag { get; set; } = 3;

    /// <summary>M4: 忙碌信号（检测进行中置位，完成后复位）</summary>
    public ushort BusyFlag { get; set; } = 4;

    /// <summary>M10+: 继电器控制基地址（切换测试点通道）</summary>
    public ushort RelayBaseAddress { get; set; } = 10;

    // ── 保持寄存器地址 (FC 0x06 写入 / FC 0x03 读取) ──

    /// <summary>D100: 测试点选择寄存器（写入当前测试点编号，从 1 开始）</summary>
    public ushort TestPointSelectRegister { get; set; } = 100;

    /// <summary>D200+: 测试结果存储基地址（1=OK, 0=NG）</summary>
    public ushort TestResultBaseRegister { get; set; } = 200;
}
