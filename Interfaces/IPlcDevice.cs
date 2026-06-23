// ============================================================
// 文件: Interfaces/Devices/IPlcDevice.cs
// 描述: PLC 设备抽象接口
// 继承 ICommunicationDevice，DI 容器可通过此接口区分 PLC
// ============================================================

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices
{
    /// <summary>
    /// PLC 设备抽象接口
    /// 继承通用通信设备接口，无额外成员
    /// 唯一目的是让 DI 容器能区分 PLC 和万用表
    /// </summary>
    public interface IPlcDevice : ICommunicationDevice
    {
        // 继承 ICommunicationDevice 全部成员
        // 如需 PLC 特有方法（如读写寄存器），可在此扩展
    }
}