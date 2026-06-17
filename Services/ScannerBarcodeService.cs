using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    /// <summary>
    /// 扫描枪条码服务
    /// 封装扫描枪硬件，统一解析条码格式，供各ViewModel订阅使用
    /// 
    /// 解析格式："T998248391,250919,00004Z"
    ///   第1段 → 机种名称
    ///   第2段 → 日期
    ///   第3段 → 序列号
    /// </summary>
    public class ScannerBarcodeService : IScannerBarcodeService, IDisposable
    {
        private readonly ILogger<ScannerBarcodeService> _logger;
        private readonly HoneywellH1900Scanner? _scanner;
        private bool _subscribed = false;

        public event EventHandler<BarcodeParsedEventArgs>? BarcodeParsed;
        public event EventHandler<bool>? ConnectionStateChanged;

        public bool IsConnected => _scanner?.IsConnected ?? false;
        public string PortName => _scanner?.PortName ?? "COM9";

        public ScannerBarcodeService(ILogger<ScannerBarcodeService> logger, HoneywellH1900Scanner? scanner = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _scanner = scanner;
            SubscribeToScanner();
            _logger.LogInformation("ScannerBarcodeService 初始化完成，扫描枪: {HasScanner}", _scanner != null);
        }

        private void SubscribeToScanner()
        {
            if (_scanner == null || _subscribed) return;

            _scanner.BarcodeReceived += OnScannerBarcodeReceived;
            _scanner.ConnectionStateChanged += OnScannerConnectionChanged;
            _subscribed = true;

            _logger.LogDebug("已订阅扫描枪事件");
        }

        private void OnScannerConnectionChanged(object? sender, bool isConnected)
        {
            // 切换到UI线程触发
            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ConnectionStateChanged?.Invoke(this, isConnected);
                });
            }
            else
            {
                ConnectionStateChanged?.Invoke(this, isConnected);
            }
        }

        private void OnScannerBarcodeReceived(object? sender, BarcodeReceivedEventArgs e)
        {
            // 解析条码
            var parsed = ParseBarcode(e.Barcode);

            // 切换到UI线程触发
            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
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
        /// </summary>
        private BarcodeParsedEventArgs ParseBarcode(string barcode)
        {
            string modelName = barcode;
            string? datePart = null;
            string? serialPart = null;

            try
            {
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

                _logger.LogDebug("条码解析: 机种={Model}, 日期={Date}, 序列号={Serial}",
                    modelName, datePart, serialPart);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "条码解析异常，使用原始值: {Barcode}", barcode);
            }

            return new BarcodeParsedEventArgs(barcode, modelName, datePart, serialPart);
        }

        public bool Connect(string portName)
        {
            if (_scanner == null)
            {
                _logger.LogWarning("扫描枪服务未注册，无法连接");
                return false;
            }
            return _scanner.Connect(portName);
        }

        public void Disconnect()
        {
            _scanner?.Disconnect();
        }

        public void Dispose()
        {
            if (_scanner != null && _subscribed)
            {
                _scanner.BarcodeReceived -= OnScannerBarcodeReceived;
                _scanner.ConnectionStateChanged -= OnScannerConnectionChanged;
                _subscribed = false;
            }
            GC.SuppressFinalize(this);
        }
    }
}