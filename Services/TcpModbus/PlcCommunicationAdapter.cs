// ============================================================
// 文件: Services/TcpModbus/PlcCommunicationAdapter.cs
// 描述: PLC 通信适配器（修正版）
// 修正: ConnectionStateChanged 事件代理改用内部订阅+转发
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services.TcpModbus
{
    /// <summary>
    /// PLC 通信适配器
    /// 将 TcpClientPLCMotionService 适配为 IPlcDevice 接口
    /// 解决 PLC 驱动未实现 ICommunicationDevice 的问题
    /// </summary>
    public class PlcCommunicationAdapter : IPlcDevice, IDisposable
    {
        #region 字段

        private readonly TcpClientPLCMotionService _plcService;
        private readonly ILogger<PlcCommunicationAdapter> _logger;
        private bool _isDisposed;

        #endregion

        #region 属性

        /// <summary>
        /// PLC是否已连接
        /// 代理到原始服务的 IsConnected 属性
        /// </summary>
        public bool IsConnected => _plcService.IsConnected;

        #endregion

        #region 事件

        /// <summary>
        /// 连接状态变更事件
        /// 由于原始服务使用 Action&lt;bool&gt; 而非 EventHandler&lt;bool&gt;，
        /// 采用内部订阅+转发方式实现委托类型转换
        /// </summary>
        public event EventHandler<bool>? ConnectionStateChanged;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化 PLC 通信适配器
        /// </summary>
        /// <param name="plcService">原始 PLC 服务实例</param>
        /// <param name="logger">日志记录器</param>
        public PlcCommunicationAdapter(
            TcpClientPLCMotionService plcService,
            ILogger<PlcCommunicationAdapter> logger)
        {
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ⭐ 订阅原始服务的 Action<bool> 事件，内部转发为 EventHandler<bool>
            _plcService.ConnectionStateChanged += OnPlcConnectionStateChanged;

            _logger.LogDebug("PlcCommunicationAdapter 初始化完成");
        }

        #endregion

        #region 事件转发

        /// <summary>
        /// 原始 PLC 服务连接状态变更回调（Action&lt;bool&gt;）
        /// 将 Action&lt;bool&gt; 转换为 EventHandler&lt;bool&gt; 并触发
        /// </summary>
        /// <param name="connected">PLC 是否已连接</param>
        private void OnPlcConnectionStateChanged(bool connected)
        {
            // 安全触发 EventHandler<bool> 事件
            ConnectionStateChanged?.Invoke(this, connected);
        }

        #endregion

        #region 配置注入

        /// <summary>
        /// 从 FP0HCommunicationConfig 应用PLC连接配置到内部的 TcpClientPLCMotionService
        /// 由 DeviceConnectionManager 在启动时调用，确保配置与UI同步
        /// </summary>
        /// <param name="config">PLC通信配置（来自系统设定页）</param>
        public void ApplyConfig(FP0HCommunicationConfig config)
        {
            _plcService.Host = config.IpAddress;
            _plcService.Port = config.Port;
            _plcService.ReceiveTimeoutMs = config.ReceiveTimeoutMs;
            _plcService.SendTimeoutMs = config.SendTimeoutMs;
            _plcService.ReconnectDelayMs = config.ReconnectDelayMs;
            _plcService.MaxReconnectAttempts = config.MaxReconnectAttempts;
            _plcService.HealthCheckMode = config.HealthCheckMode;
            _plcService.HealthCheckIntervalSeconds = config.HealthCheckIntervalSeconds;
            _plcService.LastDataTimeoutSeconds = config.LastDataTimeoutSeconds;

            _logger.LogDebug("已应用PLC配置: {Host}:{Port}", _plcService.Host, _plcService.Port);
        }

        #endregion

        #region 接口实现

        /// <summary>
        /// 连接 PLC
        /// 将 StartAsync 映射为 ConnectAsync，统一设备生命周期
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>连接成功返回 true</returns>
        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                _logger.LogInformation("PLC适配器: 开始连接...");

                // 如果已在运行，先停止再启动（确保干净状态）
                if (_plcService.IsRunning)
                {
                    await _plcService.StopAsync();
                }

                await _plcService.StartAsync();

                bool connected = _plcService.IsConnected;
                _logger.LogInformation("PLC适配器: 连接结果={Result}", connected);
                return connected;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PLC适配器: 连接失败");
                return false;
            }
        }

        /// <summary>
        /// 断开 PLC 连接
        /// 将 StopAsync 映射为 DisconnectAsync，统一设备生命周期
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                _logger.LogInformation("PLC适配器: 断开连接...");

                if (_plcService.IsRunning)
                {
                    await _plcService.StopAsync();
                }

                _logger.LogInformation("PLC适配器: 已断开");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PLC适配器: 断开连接失败");
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // ⭐ 取消订阅原始事件，防止内存泄漏
            _plcService.ConnectionStateChanged -= OnPlcConnectionStateChanged;

            // 不释放 _plcService，因为它是单例，生命周期由DI容器管理
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}