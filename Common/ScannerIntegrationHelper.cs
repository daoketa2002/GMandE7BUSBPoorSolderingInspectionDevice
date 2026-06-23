// ============================================================
// 文件: Common/ScannerIntegrationHelper.cs
// 描述: 扫描枪集成帮助类 —— 封装扫描枪事件的订阅/取消、连接状态同步
//       避免在多个 ViewModel 中重复编写相同代码
// 使用方式:
//   1. ViewModel 中创建实例并注入 IScannerBarcodeService
//   2. 订阅 ScannerConnected / ScannerDisconnected / BarcodeScanned 事件
//   3. 页面进入时调用 Subscribe()，离开时调用 Unsubscribe()
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using Microsoft.Extensions.Logging;
using System;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common
{
    /// <summary>
    /// 扫描枪集成帮助类
    /// 封装了扫描枪连接状态同步、条码解析事件的订阅与取消
    /// 通过事件向 ViewModel 转发，保持 ViewModel 的轻量级
    /// </summary>
    public class ScannerIntegrationHelper : IDisposable
    {
        #region 字段

        private readonly IScannerBarcodeService? _scannerService;
        private readonly ILogger _logger;
        private bool _isSubscribed;

        #endregion

        #region 属性

        /// <summary>
        /// 扫描枪是否已连接
        /// </summary>
        public bool IsScannerConnected { get; private set; }

        /// <summary>
        /// 扫描枪状态文本（用于UI显示）
        /// </summary>
        public string ScannerStatusText { get; private set; } = "扫描枪未连接";

        /// <summary>
        /// 最近扫描的原始条码
        /// </summary>
        public string ScannedBarcode { get; private set; } = string.Empty;

        /// <summary>
        /// 扫描枪端口名
        /// </summary>
        public string PortName => _scannerService?.PortName ?? "COM9";

        #endregion

        #region 事件

        /// <summary>
        /// 扫描枪连接成功时触发
        /// </summary>
        public event Action? ScannerConnected;

        /// <summary>
        /// 扫描枪断开连接时触发
        /// </summary>
        public event Action? ScannerDisconnected;

        /// <summary>
        /// 扫描到条码时触发
        /// </summary>
        public event Action<BarcodeParsedEventArgs>? BarcodeScanned;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化扫描枪集成帮助类
        /// </summary>
        /// <param name="scannerService">扫描枪条码服务（可为null，表示未注册扫描枪）</param>
        /// <param name="logger">日志记录器（ILogger 或 ILogger&lt;T&gt;）</param>
        public ScannerIntegrationHelper(
            IScannerBarcodeService? scannerService,
            ILogger logger)
        {
            _scannerService = scannerService;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            InitializeState();
            _logger.LogDebug("ScannerIntegrationHelper 初始化完成，扫描枪服务: {HasService}", _scannerService != null);
        }

        #endregion

        #region 私有初始化方法

        /// <summary>
        /// 同步扫描枪初始连接状态
        /// </summary>
        private void InitializeState()
        {
            if (_scannerService != null)
            {
                IsScannerConnected = _scannerService.IsConnected;
                ScannerStatusText = _scannerService.IsConnected
                    ? $"扫描枪已连接 ({_scannerService.PortName})"
                    : "扫描枪未连接";
            }
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 订阅扫描枪事件（页面进入时调用）
        /// 重复调用安全，内部有防重复订阅保护
        /// </summary>
        public void Subscribe()
        {
            if (_scannerService == null || _isSubscribed)
                return;

            _scannerService.BarcodeParsed += OnBarcodeParsed;
            _scannerService.ConnectionStateChanged += OnConnectionStateChanged;
            _isSubscribed = true;

            _logger.LogDebug("已订阅扫描枪事件");
        }

        /// <summary>
        /// 取消订阅扫描枪事件（页面离开时调用）
        /// 防止在其他页面扫码时误触发本页逻辑
        /// </summary>
        public void Unsubscribe()
        {
            if (_scannerService == null || !_isSubscribed)
                return;

            _scannerService.BarcodeParsed -= OnBarcodeParsed;
            _scannerService.ConnectionStateChanged -= OnConnectionStateChanged;
            _isSubscribed = false;

            _logger.LogDebug("已取消订阅扫描枪事件");
        }

        /// <summary>
        /// 连接扫描枪
        /// </summary>
        /// <param name="portName">串口名称（如 COM9）</param>
        /// <returns>是否连接成功</returns>
        public bool Connect(string portName)
        {
            if (_scannerService == null)
            {
                _logger.LogWarning("扫描枪服务未注册，无法连接");
                return false;
            }

            var result = _scannerService.Connect(portName);
            _logger.LogInformation("扫描枪连接结果: {Result}, 端口: {Port}", result, portName);
            return result;
        }

        /// <summary>
        /// 断开扫描枪连接
        /// </summary>
        public void Disconnect()
        {
            _scannerService?.Disconnect();
        }

        #endregion

        #region 事件处理（内部）

        /// <summary>
        /// 扫描枪连接状态变更处理
        /// 自动同步 IsScannerConnected 和 ScannerStatusText
        /// </summary>
        private void OnConnectionStateChanged(object? sender, bool isConnected)
        {
            IsScannerConnected = isConnected;
            ScannerStatusText = isConnected
                ? $"扫描枪已连接 ({_scannerService?.PortName})"
                : "扫描枪已断开";

            _logger.LogDebug("扫描枪连接状态变更: {Status}", ScannerStatusText);

            if (isConnected)
            {
                ScannerConnected?.Invoke();
            }
            else
            {
                ScannerDisconnected?.Invoke();
            }
        }

        /// <summary>
        /// 扫描枪条码接收处理
        /// 更新 ScannedBarcode 并转发给订阅者
        /// </summary>
        private void OnBarcodeParsed(object? sender, BarcodeParsedEventArgs e)
        {
            ScannedBarcode = e.RawBarcode;
            _logger.LogDebug("扫描枪收到条码: {Barcode}, 机种={Model}, 序列号={Serial}",
                e.RawBarcode, e.ModelName, e.SerialPart);

            BarcodeScanned?.Invoke(e);
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// 释放资源，取消所有事件订阅
        /// </summary>
        public void Dispose()
        {
            Unsubscribe();
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}