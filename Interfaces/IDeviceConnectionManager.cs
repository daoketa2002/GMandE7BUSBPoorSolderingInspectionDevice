// ============================================================
// 文件: Interfaces/IDeviceConnectionManager.cs
// 描述: 全局设备连接管理器接口
// 职责: 统一管理三设备（PLC、万用表、扫描枪）的自动连接、
//       重连和状态监控，各 ViewModel 通过订阅事件获取状态
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    /// <summary>设备连接的最小展示状态。</summary>
    public enum DeviceConnectionStatus
    {
        Disconnected,
        Connecting,
        Connected
    }

    /// <summary>
    /// 全局设备连接管理器接口
    /// 管理 PLC、万用表、扫描枪的自动连接和状态监控
    /// </summary>
    public interface IDeviceConnectionManager
    {
        #region 设备连接状态

        /// <summary>PLC是否已连接</summary>
        bool IsPlcConnected { get; }

        /// <summary>万用表是否已连接</summary>
        bool IsDmmConnected { get; }

        /// <summary>扫描枪是否已连接</summary>
        bool IsScannerConnected { get; }

        /// <summary>所有设备是否全部就绪</summary>
        bool AreAllDevicesReady { get; }

        #endregion

        #region 状态文本

        /// <summary>PLC连接状态文本</summary>
        string PlcStatusText { get; }

        /// <summary>万用表连接状态文本</summary>
        string DmmStatusText { get; }

        /// <summary>扫描枪连接状态文本</summary>
        string ScannerStatusText { get; }

        /// <summary>供运行页展示的三态连接状态。</summary>
        DeviceConnectionStatus PlcStatus { get; }
        DeviceConnectionStatus DmmStatus { get; }
        DeviceConnectionStatus ScannerStatus { get; }

        #endregion

        #region 方法

        /// <summary>
        /// 启动设备连接管理器，自动连接所有设备
        /// 在应用启动时调用，由 Program.cs 或 App.xaml.cs 触发
        /// </summary>
        Task StartAllAsync();

        /// <summary>
        /// 停止所有设备连接
        /// 在应用退出时调用
        /// </summary>
        Task StopAllAsync();

        /// <summary>
        /// 手动重连指定设备（先断开再连接）
        /// </summary>
        /// <param name="deviceType">设备类型："PLC" / "DMM" / "Scanner"</param>
        Task ReconnectDeviceAsync(string deviceType);

        /// <summary>
        /// 连接指定设备（仅连接，不先断开）
        /// 与 ReconnectDeviceAsync 的区别：如果已连接则直接返回成功，不会先断开
        /// </summary>
        /// <param name="deviceType">设备类型："PLC" / "DMM" / "Scanner"</param>
        Task ConnectDeviceAsync(string deviceType);

        /// <summary>
        /// 断开指定设备
        /// </summary>
        /// <param name="deviceType">设备类型："PLC" / "DMM" / "Scanner"</param>
        Task DisconnectDeviceAsync(string deviceType);

        /// <summary>主动执行扫描枪 Windows 服务深度恢复和历史数据排空。</summary>
        Task<ScannerDeepRecoveryResult> DeepRecoverScannerAsync(
            CancellationToken cancellationToken = default);

        /// <summary>保存配置后，将新参数注入生产设备，并仅重连连接参数发生变化的设备。</summary>
        Task<DeviceReconnectSummary> ApplySettingsAndReconnectAsync(bool reconnectPlc, bool reconnectDmm, bool reconnectScanner);

        #endregion

        #region 临时测试

        /// <summary>
        /// 测试输入 PLC 配置是否可达（使用独立临时测试器）。
        /// 等待当前 PLC 生产连接/重连结束后，获取 _plcLock 执行测试。
        /// 不修改生产连接状态，不发布生产连接事件。
        /// </summary>
        Task<bool> TestPlcConfigurationAsync(
            FP0HCommunicationConfig config,
            CancellationToken ct = default);

        /// <summary>
        /// 测试输入 DMM 配置是否可达（使用独立临时测试器）。
        /// 等待当前 DMM 生产连接/重连结束后，获取 _dmmLock 执行测试。
        /// 不修改生产连接状态，不发布生产连接事件。
        /// </summary>
        Task<string?> TestDmmConfigurationAsync(
            GDM9060CommunicationConfig config,
            CancellationToken ct = default);

        #endregion

        #region 事件

        /// <summary>PLC连接状态变更事件</summary>
        event EventHandler<DeviceConnectionStateChangedEventArgs>? PlcConnectionStateChanged;

        /// <summary>万用表连接状态变更事件</summary>
        event EventHandler<DeviceConnectionStateChangedEventArgs>? DmmConnectionStateChanged;

        /// <summary>扫描枪连接状态变更事件</summary>
        event EventHandler<DeviceConnectionStateChangedEventArgs>? ScannerConnectionStateChanged;

        /// <summary>扫描枪条码接收事件（全局转发）</summary>
        event EventHandler<BarcodeParsedEventArgs>? BarcodeScanned;

        /// <summary>扫描枪异常超长帧事件。</summary>
        event EventHandler<ScannerFrameRejectedEventArgs>? ScannerFrameRejected;

        /// <summary>扫描枪深度恢复阶段状态文本。</summary>
        event EventHandler<string>? ScannerDeepRecoveryProgressChanged;

        /// <summary>设备就绪状态变更事件（所有设备全部就绪时触发）</summary>
        event EventHandler<bool>? AllDevicesReadyChanged;

        #endregion
    }

    /// <summary>
    /// 设备连接状态变更事件参数
    /// </summary>
    public class DeviceConnectionStateChangedEventArgs : EventArgs
    {
        /// <summary>设备类型（PLC / DMM / Scanner）</summary>
        public string DeviceType { get; }

        /// <summary>是否已连接</summary>
        public bool IsConnected { get; }

        /// <summary>状态文本描述</summary>
        public string StatusText { get; }

        /// <summary>连接状态，供 UI 区分连接中与未连接。</summary>
        public DeviceConnectionStatus Status { get; }

        public DeviceConnectionStateChangedEventArgs(string deviceType, bool isConnected, string statusText,
            DeviceConnectionStatus status = DeviceConnectionStatus.Disconnected)
        {
            DeviceType = deviceType;
            IsConnected = isConnected;
            StatusText = statusText;
            Status = status;
        }
    }
}
