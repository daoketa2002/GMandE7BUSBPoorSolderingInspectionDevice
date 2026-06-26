// ============================================================
// 文件: Services/ScannerBarcodeService.cs
// 描述: 扫描枪条码服务（重构版）
// 修改: 
//   1. 移除构造函数中的 Connect() 调用（消除与 DeviceConnectionManager 的竞态）
//   2. 新增 InitializeAsync() 方法，由 DeviceConnectionManager 在硬件就绪后调用
//   3. 不再直接持有硬件引用，改为通过 IScannerDevice 接口注入
//   4. 所有连接操作委托给 DeviceConnectionManager，本服务仅负责解析与转发
// ============================================================

using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 扫描枪条码服务
    /// 职责：条码解析与事件转发（不负责连接管理）
    /// 
    /// 架构说明：
    /// - DeviceConnectionManager 负责连接/断开硬件
    /// - ScannerBarcodeService 负责解析条码并转发给各 ViewModel
    /// - 连接状态通过订阅 IScannerDevice.ConnectionStateChanged 获取
    /// 
    /// 解析格式："T998248391,250919,00004Z"
    ///   第1段 → 机种名称
    ///   第2段 → 日期
    ///   第3段 → 序列号
    /// </summary>
    public class ScannerBarcodeService : IScannerBarcodeService, IDisposable
    {
        private readonly ILogger<ScannerBarcodeService> _logger;
        private readonly IScannerDevice? _scannerDevice;
        private bool _subscribed = false;
        private bool _isDisposed = false;

        /// <summary>
        /// 连接状态（从硬件事件同步）
        /// </summary>
        private volatile bool _isConnected;

        public event EventHandler<BarcodeParsedEventArgs>? BarcodeParsed;
        public event EventHandler<bool>? ConnectionStateChanged;

        /// <summary>
        /// 扫描枪是否已连接（从硬件事件同步）
        /// </summary>
        public bool IsConnected => _isConnected;

        /// <summary>
        /// 扫描枪端口名（从硬件驱动属性读取，或返回默认值）
        /// </summary>
        public string PortName => _scannerDevice is Devices.Scanner.HoneywellH1900Scanner scanner
            ? scanner.PortName
            : "COM9";

        /// <summary>
        /// 构造函数
        /// ⭐ 不再自动调用 Connect()，只订阅硬件事件
        /// 连接由 DeviceConnectionManager 统一管理
        /// </summary>
        /// <param name="logger">日志记录器</param>
        /// <param name="scannerDevice">扫描枪硬件驱动（可选，通过DI注入）</param>
        public ScannerBarcodeService(
            ILogger<ScannerBarcodeService> logger,
            IScannerDevice? scannerDevice = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _scannerDevice = scannerDevice;

            // ⭐ 仅订阅硬件事件，不发起连接
            SubscribeToScannerEvents();

            _logger.LogInformation("ScannerBarcodeService 初始化完成（等待 DeviceConnectionManager 启动硬件）");
        }

        /// <summary>
        /// ⭐ 异步初始化（由 DeviceConnectionManager 在扫描枪连接成功后调用）
        /// 同步硬件连接状态
        /// </summary>
        public Task InitializeAsync()
        {
            if (_isDisposed)
            {
                _logger.LogWarning("ScannerBarcodeService 已释放，无法初始化");
                return Task.CompletedTask;
            }

            // 同步当前连接状态
            if (_scannerDevice != null)
            {
                _isConnected = _scannerDevice.IsConnected;
                _logger.LogInformation("扫描枪条码服务已初始化，当前连接状态: {IsConnected}", _isConnected);
            }
            else
            {
                _logger.LogWarning("扫描枪硬件驱动未注入，条码解析服务将无法接收数据");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 订阅扫描枪硬件事件（条码接收 + 连接状态变更）
        /// </summary>
        private void SubscribeToScannerEvents()
        {
            if (_scannerDevice == null || _subscribed)
            {
                _logger.LogDebug("扫描枪硬件事件订阅已跳过 (DeviceNull={IsNull}, Subscribed={Subscribed})",
                    _scannerDevice == null, _subscribed);
                return;
            }

            // ⭐ 订阅条码接收事件（原始数据 → 解析 → 转发）
            _scannerDevice.BarcodeReceived += OnScannerBarcodeReceived;

            // ⭐ 订阅连接状态变更事件（转发给 ViewModel）
            _scannerDevice.ConnectionStateChanged += OnScannerConnectionChanged;

            _subscribed = true;
            _logger.LogInformation("已订阅扫描枪硬件事件（条码接收 + 连接状态变更）");
        }

        /// <summary>
        /// 连接状态变更回调
        /// 确保在UI线程触发事件
        /// </summary>
        private void OnScannerConnectionChanged(object? sender, bool isConnected)
        {
            _isConnected = isConnected;
            _logger.LogInformation("扫描枪连接状态变更: {IsConnected}", isConnected);

            // 确保在UI线程触发事件
            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ConnectionStateChanged?.Invoke(this, isConnected);
                });
            }
            else
            {
                ConnectionStateChanged?.Invoke(this, isConnected);
            }
        }

        /// <summary>
        /// 条码接收回调
        /// 解析原始条码并触发 BarcodeParsed 事件
        /// </summary>
        private void OnScannerBarcodeReceived(object? sender, BarcodeReceivedEventArgs e)
        {
            var parsed = ParseBarcode(e.Barcode);

            // 确保在UI线程触发事件
            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    BarcodeParsed?.Invoke(this, parsed);
                });
            }
            else
            {
                BarcodeParsed?.Invoke(this, parsed);
            }
        }

        /// <summary>
        /// 解析条码格式："T998248391,250919,00004Z"
        /// 
        /// 解析规则：
        /// - 第1段 → 机种名称（ModelName）
        /// - 第2段 → 日期（DatePart，可选）
        /// - 第3段 → 序列号（SerialPart，可选）
        /// - 无法解析时，整个条码作为 ModelName
        /// </summary>
        private BarcodeParsedEventArgs ParseBarcode(string barcode)
        {
            string modelName = barcode;
            string? datePart = null;
            string? serialPart = null;

            try
            {
                // 按逗号分割
                var parts = barcode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 1)
                {
                    modelName = parts[0].Trim();
                }
                if (parts.Length >= 2)
                {
                    datePart = parts[1].Trim();
                }
                if (parts.Length >= 3)
                {
                    serialPart = parts[2].Trim();
                }

                _logger.LogDebug("条码解析成功: 机种={Model}, 日期={Date}, 序列号={Serial}",
                    modelName, datePart, serialPart);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "条码解析异常，使用原始值: {Barcode}", barcode);
            }

            return new BarcodeParsedEventArgs(barcode, modelName, datePart, serialPart);
        }

        /// <summary>
        /// 连接扫描枪（保留兼容，但实际连接由 DeviceConnectionManager 管理）
        /// </summary>
        public bool Connect(string portName)
        {
            _logger.LogWarning("ScannerBarcodeService.Connect() 已弃用，请使用 DeviceConnectionManager 管理连接");
            return _isConnected;
        }

        /// <summary>
        /// 异步断开扫描枪
        /// </summary>
        public async Task DisconnectAsync()
        {
            _logger.LogInformation("ScannerBarcodeService 断开请求（委托给 DeviceConnectionManager）");
            // 实际断开由 DeviceConnectionManager 管理
            await Task.CompletedTask;
        }

        /// <summary>
        /// 同步断开扫描枪（兼容旧代码）
        /// </summary>
        public void Disconnect()
        {
            _logger.LogInformation("ScannerBarcodeService.Disconnect() 已弃用");
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_scannerDevice != null && _subscribed)
            {
                _scannerDevice.BarcodeReceived -= OnScannerBarcodeReceived;
                _scannerDevice.ConnectionStateChanged -= OnScannerConnectionChanged;
                _subscribed = false;
            }

            _logger.LogInformation("ScannerBarcodeService 已释放");
            GC.SuppressFinalize(this);
        }
    }
}