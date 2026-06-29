// ============================================================
// 文件: Common/Validators/InputValidationHelper.cs
// 描述: 通用输入校验工具类
//       提供项目全局统一的 IP地址、端口号、串口号、引脚名等
//       格式和范围校验方法。所有方法均为纯函数，无副作用。
// ============================================================

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Validators
{
    /// <summary>
    /// 通用输入校验工具类
    /// 所有方法均为静态纯函数，不依赖外部状态
    /// 用于 ViewModel 保存/提交时的数据校验，以及 View 层输入拦截
    /// </summary>
    public static class InputValidationHelper
    {
        #region IP 地址校验

        /// <summary>
        /// IPv4 地址正则：4段数字，每段0-255，段间用"."分隔
        /// </summary>
        private static readonly Regex IpAddressRegex = new(
            @"^(25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\." +
            @"(25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\." +
            @"(25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\." +
            @"(25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)$",
            RegexOptions.Compiled);

        /// <summary>
        /// 校验字符串是否为合法的 IPv4 地址
        /// </summary>
        /// <param name="ip">待校验的IP地址字符串</param>
        /// <returns>合法返回 true，否则 false</returns>
        public static bool IsValidIpAddress(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return false;
            return IpAddressRegex.IsMatch(ip.Trim());
        }

        #endregion

        #region 端口号校验

        /// <summary>
        /// TCP/UDP 端口号有效范围（1-65535）
        /// </summary>
        public const int PortMinValue = 1;
        public const int PortMaxValue = 65535;

        /// <summary>
        /// 校验端口号是否在合法范围（1-65535）内
        /// </summary>
        /// <param name="port">端口号</param>
        /// <returns>合法返回 true，否则 false</returns>
        public static bool IsValidPort(int port)
        {
            return port >= PortMinValue && port <= PortMaxValue;
        }

        #endregion

        #region 串口号校验

        /// <summary>
        /// COM 端口号有效范围（1-256）
        /// </summary>
        public const int ComPortMinNumber = 1;
        public const int ComPortMaxNumber = 256;

        /// <summary>
        /// 串口号正则：COM 后跟数字（不区分大小写）
        /// </summary>
        private static readonly Regex ComPortRegex = new(
            @"^COM(\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 校验字符串是否为合法的 COM 端口名称（如 COM1、COM8、COM256）
        /// 格式要求：大小写不敏感的"COM" + 1~256之间的数字
        /// </summary>
        /// <param name="comPort">待校验的串口名称</param>
        /// <returns>合法返回 true，否则 false</returns>
        public static bool IsValidComPort(string? comPort)
        {
            if (string.IsNullOrWhiteSpace(comPort))
                return false;

            var match = ComPortRegex.Match(comPort.Trim());
            if (!match.Success)
                return false;

            // 提取数字部分，校验范围
            if (int.TryParse(match.Groups[1].Value, out int number))
            {
                return number >= ComPortMinNumber && number <= ComPortMaxNumber;
            }

            return false;
        }

        #endregion

        #region 引脚名称校验

        /// <summary>
        /// 引脚名正则：大写A或B开头 + 1~2位数字（如 A1、B20）
        /// </summary>
        private static readonly Regex PinNameRegex = new(
            @"^[AB]\d{1,2}$",
            RegexOptions.Compiled);

        /// <summary>
        /// 校验单个引脚名称是否合法（如 A1、B20、A10）
        /// 格式：大写A或B + 1~2位数字
        /// </summary>
        /// <param name="pinName">引脚名称</param>
        /// <returns>合法返回 true，否则 false</returns>
        public static bool IsValidPinName(string? pinName)
        {
            if (string.IsNullOrWhiteSpace(pinName))
                return false;
            return PinNameRegex.IsMatch(pinName.Trim());
        }

        #endregion

        #region 电阻值范围校验

        /// <summary>
        /// 电阻值下限有效范围（Ω）。
        /// 电阻值统一按 Ω 输入，最大值按 GDM-9060 100MΩ 档满量程展开。
        /// </summary>
        public const double ResistanceMinValue = 0.0;
        public const double ResistanceMaxValue = 119_999_900.0;

        /// <summary>
        /// 万用表电阻量程信息。界面仍然只让用户输入 Ω，此结构用于自动提示和保存校验。
        /// </summary>
        public sealed record ResistanceRangeInfo(
            string RangeName,
            double MaxOhms,
            double ResolutionOhms,
            int DecimalPlaces,
            int RequiredMultiple);

        private static readonly ResistanceRangeInfo[] ResistanceRanges =
        [
            new("100Ω", 119.9999, 0.0001, 4, 0),
            new("1kΩ", 1_199.999, 0.001, 3, 0),
            new("10kΩ", 11_999.99, 0.01, 2, 0),
            new("100kΩ", 119_999.9, 0.1, 1, 0),
            new("1MΩ", 1_199_999, 1, 0, 1),
            new("10MΩ", 11_999_990, 10, 0, 10),
            new("100MΩ", 119_999_900, 100, 0, 100)
        ];

        /// <summary>
        /// 校验电阻值是否在合理范围内
        /// 排除 NaN、∞、负值、过大值
        /// </summary>
        /// <param name="value">电阻值</param>
        /// <returns>合法返回 null；不合法返回错误描述文本</returns>
        public static string? ValidateResistanceValue(double? value)
        {
            if (value == null)
                return null; // null 在导通模式下合法

            double v = value.Value;

            if (double.IsNaN(v))
                return "电阻值不能为 NaN（非数字）";
            if (double.IsInfinity(v))
                return "电阻值不能为无穷大";
            if (v < ResistanceMinValue)
                return $"电阻值不能为负数，当前值: {v}";
            if (v > ResistanceMaxValue)
                return $"电阻值过大（最大 {ResistanceMaxValue:N0} Ω），当前值: {v}";

            var range = GetResistanceRangeInfo(v);
            if (!IsAlignedToResistanceResolution(v, range))
            {
                if (range.RequiredMultiple > 1)
                    return $"当前值预计使用 {range.RangeName} 量程，仪器分辨率约 {FormatResolution(range)}，请输入 {range.RequiredMultiple}Ω 的整数倍";

                return $"当前值预计使用 {range.RangeName} 量程，仪器分辨率约 {FormatResolution(range)}，最多保留 {range.DecimalPlaces} 位小数";
            }

            return null; // 合法
        }

        /// <summary>
        /// 根据 Ω 数值自动推断万用表预计量程。
        /// </summary>
        public static ResistanceRangeInfo GetResistanceRangeInfo(double value)
        {
            foreach (var range in ResistanceRanges)
            {
                if (value <= range.MaxOhms)
                    return range;
            }

            return ResistanceRanges[^1];
        }

        /// <summary>
        /// 生成方案编辑页下限/上限输入框下方的智能提示文案。
        /// </summary>
        public static string GetResistanceInputHint(double? value)
        {
            if (value == null)
                return "提示：输入电阻值后，将自动判断预计量程和仪器分辨率。";

            double v = value.Value;
            if (double.IsNaN(v) || double.IsInfinity(v) || v < ResistanceMinValue)
                return "提示：请输入不小于 0 的有效电阻值。";
            if (v > ResistanceMaxValue)
                return $"提示：电阻值不能超过 {ResistanceMaxValue:N0} Ω。";

            var range = GetResistanceRangeInfo(v);
            if (range.RequiredMultiple > 1)
                return $"提示：当前电阻预计使用{range.RangeName}量程，仪器分辨率约{FormatResolution(range)}，请按{range.RequiredMultiple}Ω整数倍输入。";

            if (range.DecimalPlaces == 0)
                return $"提示：当前电阻预计使用{range.RangeName}量程，仪器分辨率约{FormatResolution(range)}，请输入整数 Ω。";

            return $"提示：当前电阻预计使用{range.RangeName}量程，仪器分辨率约{FormatResolution(range)}，最多保留{range.DecimalPlaces}位小数。";
        }

        /// <summary>
        /// 按预计量程分辨率格式化电阻值，保证运行页和 CSV 中显示规则一致。
        /// </summary>
        public static string FormatResistanceValue(double value)
        {
            var range = GetResistanceRangeInfo(Math.Abs(value));
            double rounded = RoundToResolution(value, range.ResolutionOhms);
            return rounded.ToString($"F{range.DecimalPlaces}", CultureInfo.InvariantCulture);
        }

        private static string FormatResolution(ResistanceRangeInfo range)
        {
            return range.ResolutionOhms >= 1
                ? $"{range.ResolutionOhms:0}Ω"
                : $"{range.ResolutionOhms.ToString($"F{range.DecimalPlaces}", CultureInfo.InvariantCulture)}Ω";
        }

        private static bool IsAlignedToResistanceResolution(double value, ResistanceRangeInfo range)
        {
            double scaled = value / range.ResolutionOhms;
            return Math.Abs(scaled - Math.Round(scaled)) < 0.000_001;
        }

        private static double RoundToResolution(double value, double resolution)
        {
            // 只用于显示测量值；方案阈值保存前仍要求用户输入符合分辨率的原始数值。
            return Math.Round(value / resolution, MidpointRounding.AwayFromZero) * resolution;
        }

        /// <summary>
        /// 校验下限是否小于等于上限（电阻值模式下）
        /// </summary>
        /// <param name="lower">下限值</param>
        /// <param name="upper">上限值</param>
        /// <returns>合法返回 null；不合法返回错误描述文本</returns>
        public static string? ValidateLimitRange(double? lower, double? upper)
        {
            // 只有两个都有值时才需要校验大小关系
            if (lower == null || upper == null)
                return null;

            if (lower.Value > upper.Value)
                return $"下限 ({lower.Value}) 不能大于上限 ({upper.Value})";

            return null;
        }

        #endregion

        #region 超时值范围

        /// <summary>
        /// 超时值（毫秒）有效范围
        /// </summary>
        public const int TimeoutMsMinValue = 50;
        public const int TimeoutMsMaxValue = 120_000; // 2分钟

        /// <summary>
        /// 校验超时毫秒值是否在合理范围内
        /// </summary>
        public static bool IsValidTimeoutMs(int value)
        {
            return value >= TimeoutMsMinValue && value <= TimeoutMsMaxValue;
        }

        #endregion

        #region 心跳间隔范围

        /// <summary>
        /// 心跳检测间隔（秒）有效范围
        /// </summary>
        public const int HealthCheckIntervalMinSec = 1;
        public const int HealthCheckIntervalMaxSec = 3600; // 1小时

        /// <summary>
        /// 校验心跳间隔秒数是否在合理范围内
        /// </summary>
        public static bool IsValidHealthCheckInterval(int seconds)
        {
            return seconds >= HealthCheckIntervalMinSec && seconds <= HealthCheckIntervalMaxSec;
        }

        #endregion

        #region 重连延迟范围

        /// <summary>
        /// 重连延迟（毫秒）有效范围
        /// </summary>
        public const int ReconnectDelayMsMin = 100;
        public const int ReconnectDelayMsMax = 60_000;

        /// <summary>
        /// 校验重连延迟是否在合理范围内
        /// </summary>
        public static bool IsValidReconnectDelayMs(int value)
        {
            return value >= ReconnectDelayMsMin && value <= ReconnectDelayMsMax;
        }

        #endregion

        #region 重连次数范围

        /// <summary>
        /// 最大重连次数有效范围
        /// </summary>
        public const int ReconnectAttemptsMin = 1;
        public const int ReconnectAttemptsMax = 100;

        /// <summary>
        /// 校验最大重连次数是否在合理范围内
        /// </summary>
        public static bool IsValidReconnectAttempts(int value)
        {
            return value >= ReconnectAttemptsMin && value <= ReconnectAttemptsMax;
        }

        #endregion

        #region Modbus 从站地址范围

        /// <summary>
        /// Modbus 从站地址有效范围（1-247）
        /// </summary>
        public const int ModbusSlaveIdMin = 1;
        public const int ModbusSlaveIdMax = 247;

        /// <summary>
        /// 校验 Modbus 从站ID 是否在合法范围内
        /// </summary>
        public static bool IsValidModbusSlaveId(int value)
        {
            return value >= ModbusSlaveIdMin && value <= ModbusSlaveIdMax;
        }

        #endregion

        #region 数据超时范围

        /// <summary>
        /// 数据超时（秒）有效范围
        /// </summary>
        public const int DataTimeoutMinSec = 3;
        public const int DataTimeoutMaxSec = 3600;

        /// <summary>
        /// 校验数据超时秒数是否在合理范围内
        /// </summary>
        public static bool IsValidDataTimeout(int seconds)
        {
            return seconds >= DataTimeoutMinSec && seconds <= DataTimeoutMaxSec;
        }

        #endregion

        #region 项目数量上限

        /// <summary>
        /// 方案中检测项目最大数量（防止无限添加导致界面卡顿）
        /// </summary>
        public const int MaxInspectItemsCount = 50;

        #endregion

        #region 名称长度限制

        /// <summary>
        /// 机种名称最大长度（对应文件夹名限制）
        /// </summary>
        public const int MaxMachineTypeLength = 50;

        /// <summary>
        /// 方案名称最大长度（对应文件名限制）
        /// </summary>
        public const int MaxPlanNameLength = 50;

        /// <summary>
        /// 作业员名称最大长度
        /// </summary>
        public const int MaxOperatorNameLength = 20;

        /// <summary>
        /// 序列号最大长度
        /// </summary>
        public const int MaxSerialNumberLength = 50;

        /// <summary>
        /// 引脚名称最大长度（A1~B20 最长3字符，但允许手动输入更长的含前缀名称）
        /// </summary>
        public const int MaxPinNameLength = 10;

        #endregion
    }
}
