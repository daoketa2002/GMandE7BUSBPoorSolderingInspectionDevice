namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;

/// <summary>
/// PLC 地址映射，集中管理本机检测流程使用的 DT 寄存器地址。
///
/// 最新地址表（2025-07）：
/// - DT120~DT123：启动/复位/停止/急停
/// - DT130~DT185：每脚独立选择区和极性（每引脚占用连续2个寄存器：Select, Polarity）
/// - DT234：上位机允许开始检测
/// - DT302：继电器动作完成
/// - DT303：报警解除
/// - DT304：产品 OK
/// - DT305：产品 NG
/// </summary>
public sealed class PlcAddressMap
{
    // ── 控制信号 ──

    /// <summary>DT120：启动请求（PLC 写，上位机读）</summary>
    public const ushort StartSignal = 120;

    /// <summary>DT121：复位（PLC/上位机读写）</summary>
    public const ushort ResetSignal = 121;

    /// <summary>DT122：停止/暂停（PLC 写，上位机读）</summary>
    public const ushort StopSignal = 122;

    /// <summary>DT123：急停（PLC 写，上位机读）</summary>
    public const ushort EmergencyStopSignal = 123;

    /// <summary>DT234：上位机允许开始检测（上位机写，PLC 读）</summary>
    public const ushort PcReadyToStart = 234;

    /// <summary>DT302：当前测试点继电器动作完成（PLC 写，上位机读）</summary>
    public const ushort RelayActionCompleted = 302;

    /// <summary>DT303：报警解除（PLC 写，上位机读）</summary>
    public const ushort AlarmReleased = 303;

    /// <summary>DT304：产品 OK 综合结果（上位机写，PLC 读）</summary>
    public const ushort ProductOk = 304;

    /// <summary>DT305：产品 NG 综合结果（上位机写，PLC 读）</summary>
    public const ushort ProductNg = 305;

    // ── 引脚输出范围 ──

    /// <summary>引脚输出起始地址 DT130</summary>
    public const ushort PinOutputStart = 130;

    /// <summary>引脚输出结束地址 DT185</summary>
    public const ushort PinOutputEnd = 185;

    /// <summary>引脚输出寄存器总数（DT130~DT185 共 56 个寄存器）</summary>
    public const int PinOutputRegisterCount = PinOutputEnd - PinOutputStart + 1;

    /// <summary>DT130~DT185 每引脚 Select/Polarity 地址映射。</summary>
    private static readonly Dictionary<string, (ushort Select, ushort Polarity)> PinAddressMap = new()
    {
        ["A1"] = (130, 131),   ["A2"] = (132, 133),
        ["A3"] = (134, 135),   ["A4"] = (136, 137),
        ["A5"] = (142, 143),   ["A6"] = (144, 145),
        ["A7"] = (146, 147),   ["A8"] = (148, 149),
        ["A9"] = (140, 141),   ["A10"] = (156, 157),
        ["A11"] = (158, 159),  ["A12"] = (160, 161),
        ["B1"] = (162, 163),   ["B2"] = (164, 165),
        ["B3"] = (166, 167),   ["B4"] = (168, 169),
        ["B5"] = (170, 171),   ["B6"] = (172, 173),
        ["B7"] = (174, 175),   ["B8"] = (176, 177),
        ["B9"] = (178, 179),   ["B10"] = (180, 181),
        ["B11"] = (182, 183),  ["B12"] = (184, 185),
    };

    // ── 已确认的寄存器属性（用于运行时 DI 注入，支持统一地址管理） ──

    /// <summary>DT120：PLC 请求启动，PC 接管后写 0 清除。</summary>
    public ushort StartRequestRegister { get; init; } = StartSignal;

    /// <summary>DT121：复位请求，PC 完成复位处理后写 0 清除。</summary>
    public ushort ResetRegister { get; init; } = ResetSignal;

    /// <summary>DT122：停止信号，PLC 写，PC 只读。</summary>
    public ushort StopRegister { get; init; } = StopSignal;

    /// <summary>DT123：急停信号，PLC 写，PC 只读。</summary>
    public ushort EmergencyStopRegister { get; init; } = EmergencyStopSignal;

    /// <summary>DT234：上位机允许开始检测，启动前检查通过后写 1。</summary>
    public ushort PcReadyRegister { get; init; } = PcReadyToStart;

    /// <summary>DT302：继电器动作完成信号，PLC 写，PC 读。</summary>
    public ushort RelayCompletedRegister { get; init; } = RelayActionCompleted;

    /// <summary>DT303：报警解除，PLC 写，PC 读。</summary>
    public ushort AlarmReleasedRegister { get; init; } = AlarmReleased;

    /// <summary>DT304：产品 OK 结果，上位机写。</summary>
    public ushort ProductOkRegister { get; init; } = ProductOk;

    /// <summary>DT305：产品 NG 结果，上位机写。</summary>
    public ushort ProductNgRegister { get; init; } = ProductNg;

    // ── 旧语义寄存器（已废弃，保留仅供检查引用，新流程禁止使用） ──
    // DT130~DT133 不再作为"左右脚号+左右极性"通用模型，
    // 改为每引脚独立选择区（见 PinAddressMap）。
    // DT160/DT161 现在是 A12 引脚的 Select/Polarity 地址，不再是流程信号。

    /// <summary>单点结果基地址（未确认，保持 null）。</summary>
    public ushort? PointResultBaseRegister { get; init; }

    /// <summary>NG 项目编号寄存器（未确认，保持 null）。</summary>
    public ushort? NgPointIndexRegister { get; init; }

    /// <summary>报警代码寄存器（未确认，保持 null）。</summary>
    public ushort? AlarmCodeRegister { get; init; }

    // ── 引脚地址查找方法 ──

    /// <summary>
    /// 根据引脚名称获取对应的 Select 寄存器和 Polarity 寄存器地址。
    /// </summary>
    /// <param name="pinName">引脚名称，如 "A4"、"B12"。</param>
    /// <returns>(Select地址, Polarity地址)</returns>
    public static (ushort SelectAddress, ushort PolarityAddress) GetPinAddresses(string pinName)
    {
        if (string.IsNullOrWhiteSpace(pinName))
            throw new ArgumentException("引脚名称不能为空。", nameof(pinName));

        string normalized = pinName.Trim().ToUpperInvariant();
        if (PinAddressMap.TryGetValue(normalized, out var addrs))
            return addrs;

        throw new ArgumentException($"未知引脚名称或不在地址表范围内：{pinName}", nameof(pinName));
    }

    /// <summary>
    /// 判断给定地址是否在引脚输出范围（DT130~DT185）内。
    /// </summary>
    public static bool IsPinOutputAddress(ushort address)
        => address >= PinOutputStart && address <= PinOutputEnd;

    /// <summary>
    /// 将方案中的 Pin 名称转换为 PLC 使用的点位序号。
    /// 规则：A1~A12 -> 1~12，B1~B12 -> 21~32。
    /// （保留该方法供旧兼容使用，新流程优先使用 GetPinAddresses）
    /// </summary>
    public static ushort ConvertPinNameToNumber(string pinName)
    {
        if (string.IsNullOrWhiteSpace(pinName))
            throw new ArgumentException("Pin 名称不能为空。", nameof(pinName));

        string normalized = pinName.Trim().ToUpperInvariant();
        if (normalized.Length < 2)
            throw new ArgumentException($"Pin 格式错误：{pinName}", nameof(pinName));

        char group = normalized[0];
        if (!int.TryParse(normalized[1..], out int number))
            throw new ArgumentException($"Pin 编号不是数字：{pinName}", nameof(pinName));

        return group switch
        {
            'A' when number is >= 1 and <= 12 => (ushort)number,
            'B' when number is >= 1 and <= 12 => (ushort)(20 + number),
            _ => throw new ArgumentException($"Pin 超出范围或分组错误：{pinName}", nameof(pinName))
        };
    }

    /// <summary>
    /// 将方案中的极性转换为 PLC 数值。最小闭环规则：正极=0，负极=1。
    /// </summary>
    public static ushort ConvertPolarityToValue(string? polarity)
    {
        return polarity switch
        {
            Models.PinPolarityConstants.Positive => 0,
            Models.PinPolarityConstants.Negative => 1,
            _ => throw new ArgumentException($"未知极性：{polarity}", nameof(polarity))
        };
    }
}
