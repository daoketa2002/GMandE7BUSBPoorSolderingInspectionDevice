using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制
{
    /// <summary>
    /// 定义通信中设备响应的匹配方式。
    /// 用于判断设备返回的数据是否符合预期，作为测试步骤成功与否的依据。
    /// </summary>
    public enum ResponseMatchType
    {
        /// <summary>
        /// 匹配方式：响应字符串以指定的期望值开头即可。
        /// 例如：期望值为 "OK"，则 "OK"、"OK\r\n"、"OK: Success" 均视为匹配成功。
        /// </summary>
        StartsWith,

        /// <summary>
        /// 匹配方式：响应字符串必须与期望值完全相同（区分大小写）。
        /// 例如：期望值为 "READY"，则只有完全相同的 "READY" 才视为成功。
        /// </summary>
        Exact,

        /// <summary>
        /// 匹配方式：使用正则表达式对响应字符串进行匹配。
        /// 期望值（ExpectedValue）应为合法的正则表达式模式。
        /// 例如：期望值为 "^TEMP=\d+$"，可匹配 "TEMP=25"、"TEMP=100" 等。
        /// 注意：若正则表达式格式错误，会导致步骤执行失败。
        /// </summary>
        Regex,

        /// <summary>
        /// 匹配方式：只要收到非空的响应数据，即视为成功。
        /// 不关心响应内容具体是什么，常用于只需确认设备有响应的场景。
        /// 期望值（ExpectedValue）在此模式下被忽略。
        /// </summary>
        Any,

        /// <summary>
        /// 匹配方式：不等待或校验任何响应。
        /// 仅发送命令后即认为成功（除非发送过程出错）。
        /// 常用于发送无需应答的控制指令，如灯光开关、继电器动作等。
        /// 期望值（ExpectedValue）在此模式下被忽略。
        /// </summary>
        None
    }
}
