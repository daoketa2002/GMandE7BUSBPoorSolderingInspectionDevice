// ============================================================
// 文件: Interfaces/Devices/IMultimeterDevice.cs
// 描述: 万用表设备抽象接口
// 继承 ICommunicationDevice，DI 容器可通过此接口区分万用表
// ============================================================

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices
{
    /// <summary>
    /// 万用表设备抽象接口
    /// 继承通用通信设备接口，无额外成员
    /// 唯一目的是让 DI 容器能区分万用表和 PLC
    /// </summary>
    public interface IMultimeterDevice : ICommunicationDevice
    {
        // 继承 ICommunicationDevice 全部成员
        // 如需万用表特有方法（如设置测量模式），可在此扩展
    }
}