using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using GMandE7BUSBPoorSolderingInspectionDevice.Devices.Scanner;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

/// <summary>扫描枪普通重连、用户扫码验证和深度恢复弹窗状态。</summary>
public partial class ScannerRecoveryDialogViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceConnectionManager _deviceManager;
    private readonly IScannerBarcodeService _scannerBarcodeService;
    private readonly IScannerDevice _scannerDevice;
    private readonly ILogger<ScannerRecoveryDialogViewModel> _logger;
    private bool _subscribed;
    private bool _disposed;
    private bool _acceptanceDrainMode;

    public ScannerRecoveryDialogViewModel(
        IDeviceConnectionManager deviceManager,
        IScannerBarcodeService scannerBarcodeService,
        IScannerDevice scannerDevice,
        ILogger<ScannerRecoveryDialogViewModel> logger)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _scannerBarcodeService = scannerBarcodeService ?? throw new ArgumentNullException(nameof(scannerBarcodeService));
        _scannerDevice = scannerDevice ?? throw new ArgumentNullException(nameof(scannerDevice));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeepRecoverCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeepRecoverCommand))]
    private bool _canDeepRecoverButton;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseDialogCommand))]
    private bool _canClose;

    [ObservableProperty]
    private string _statusText = "正在准备扫描枪恢复……";

    [ObservableProperty]
    private string _barcodeText = "等待扫码……";

    [ObservableProperty]
    private string _receiveStatusText = "接收状态：尚未收到扫码数据";

    public event Action<bool?>? RequestClose;

    public async Task InitializeAsync()
    {
        SubscribeToScannerBarcode();
        ClearVerificationResult();

        if (_acceptanceDrainMode)
        {
            await InitializeAcceptanceDrainAsync();
            return;
        }

        IsBusy = true;
        CanClose = false;
        CanDeepRecoverButton = false;
        StatusText = "正在重新连接扫描枪，请稍候……";

        try
        {
            await _deviceManager.ReconnectDeviceAsync("Scanner").ConfigureAwait(true);
            if (_deviceManager.IsScannerConnected)
            {
                StatusText = "扫描枪串口已重新打开。\n请扫描任意条码，确认数据接收是否正常。";
                ReceiveStatusText = "接收状态：尚未收到扫码数据";
                CanDeepRecoverButton = true;
                _logger.LogInformation("[扫描枪恢复弹窗] 普通串口重连成功，等待用户验证");
            }
            else
            {
                StatusText = "普通串口重连失败。\n请点击“深度恢复”尝试重启扫描枪数据通道。";
                ReceiveStatusText = "接收状态：尚未建立有效连接";
                CanDeepRecoverButton = true;
                _logger.LogWarning("[扫描枪恢复弹窗] 普通串口重连失败");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[扫描枪恢复弹窗] 普通串口重连异常");
            StatusText = "普通串口重连失败。\n请点击“深度恢复”尝试重启扫描枪数据通道。";
            ReceiveStatusText = "接收状态：尚未建立有效连接";
            CanDeepRecoverButton = true;
        }
        finally
        {
            IsBusy = false;
            CanClose = true;
        }
    }

    /// <summary>由开发验收按钮设置，仅模拟排空流程，不调用 Windows 服务或真实串口。</summary>
    public void EnableAcceptanceDrainMode() => _acceptanceDrainMode = true;

    private async Task InitializeAcceptanceDrainAsync()
    {
        IsBusy = true;
        CanClose = false;
        CanDeepRecoverButton = false;
        StatusText = "正在清理恢复前积压的扫码数据，请暂勿扫码……";

        try
        {
            if (_scannerDevice is not HoneywellH1900Scanner scanner)
            {
                StatusText = "当前扫描枪驱动不支持开发验收排空。";
                return;
            }

            scanner.SimulateRecoveryDrainForAcceptance();
            var drainResult = await scanner.WaitForRecoveryDrainAsync().ConfigureAwait(true);
            StatusText = "扫描枪数据通道已重启。\n请扫描任意条码，确认恢复结果。";
            ReceiveStatusText = $"接收状态：已清理 {drainResult.DrainedBytes} 字节，等待扫码……";
            _logger.LogInformation(
                "[开发验收][扫码恢复] 模拟排空完成: DrainedBytes={DrainedBytes}",
                drainResult.DrainedBytes);
        }
        finally
        {
            IsBusy = false;
            CanClose = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartDeepRecovery))]
    private async Task DeepRecoverAsync()
    {
        ClearVerificationResult();
        IsBusy = true;
        CanClose = false;
        CanDeepRecoverButton = false;
        StatusText = "正在重启扫描枪数据通道，请暂勿扫码……";

        try
        {
            var result = await _deviceManager
                .DeepRecoverScannerAsync()
                .ConfigureAwait(true);
            if (result.Succeeded)
            {
                var drainedBytes = result.DrainResult?.DrainedBytes ?? 0;
                StatusText = "扫描枪数据通道已重启。\n请扫描任意条码，确认恢复结果。";
                ReceiveStatusText = $"接收状态：已清理 {drainedBytes} 字节，等待扫码……";
                _logger.LogInformation(
                    "[扫描枪恢复弹窗] 深度恢复成功，已清理 {DrainedBytes} 字节",
                    drainedBytes);
            }
            else
            {
                StatusText = result.ResultCode == "ServiceUnavailable"
                    ? "扫描枪深度恢复服务未安装或未运行，\n请联系维护人员处理。"
                    : result.Message;
                ReceiveStatusText = "接收状态：深度恢复失败";
                _logger.LogWarning(
                    "[扫描枪恢复弹窗] 深度恢复失败: Code={Code}, Message={Message}",
                    result.ResultCode,
                    result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[扫描枪恢复弹窗] 深度恢复异常");
            StatusText = "扫描枪深度恢复失败，请联系维护人员处理。";
            ReceiveStatusText = "接收状态：深度恢复失败";
        }
        finally
        {
            IsBusy = false;
            CanClose = true;
        }
    }

    private bool CanStartDeepRecovery() => !IsBusy && CanDeepRecoverButton;

    [RelayCommand(CanExecute = nameof(CanCloseDialog))]
    private void CloseDialog() => RequestClose?.Invoke(true);

    private bool CanCloseDialog() => CanClose && !IsBusy;

    private void SubscribeToScannerBarcode()
    {
        if (_subscribed)
            return;

        _scannerBarcodeService.BarcodeReceived += OnBarcodeReceived;
        _deviceManager.ScannerDeepRecoveryProgressChanged += OnDeepRecoveryProgressChanged;
        _subscribed = true;
    }

    public void UnsubscribeFromScannerEvents()
    {
        if (!_subscribed)
            return;

        _scannerBarcodeService.BarcodeReceived -= OnBarcodeReceived;
        _deviceManager.ScannerDeepRecoveryProgressChanged -= OnDeepRecoveryProgressChanged;
        _subscribed = false;
    }

    private void OnDeepRecoveryProgressChanged(object? sender, string message)
    {
        ApplyOnUiThread(() => StatusText = message);
    }

    private void OnBarcodeReceived(object? sender, BarcodeReceivedEventArgs e)
    {
        var displayText = FormatBarcodeForDisplay(e.Barcode);
        ApplyOnUiThread(() =>
        {
            BarcodeText = displayText;
            ReceiveStatusText = $"接收状态：已收到扫码数据，长度 {e.RawResponse.Length} 字节";
            StatusText = "扫描枪恢复成功。\n关闭窗口后，请重新扫描当前产品条码。";
            CanClose = true;
            _logger.LogInformation("[扫描枪恢复弹窗] 已收到验证扫码，长度={Length}", e.RawResponse.Length);
        });
    }

    private void ClearVerificationResult()
    {
        BarcodeText = "等待扫码……";
        ReceiveStatusText = "接收状态：尚未收到扫码数据";
    }

    private static string FormatBarcodeForDisplay(string? rawBarcode)
    {
        if (string.IsNullOrEmpty(rawBarcode))
            return "(空)";

        var formatted = rawBarcode.Trim()
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return formatted.Length <= 200 ? formatted : formatted[..200] + "…";
    }

    private static void ApplyOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        UnsubscribeFromScannerEvents();
        GC.SuppressFinalize(this);
    }
}
