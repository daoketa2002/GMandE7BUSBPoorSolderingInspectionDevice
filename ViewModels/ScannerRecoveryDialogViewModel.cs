using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

/// <summary>扫描枪普通重连、用户扫码验证和深度恢复弹窗状态。</summary>
public partial class ScannerRecoveryDialogViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceConnectionManager _deviceManager;
    private readonly IScannerBarcodeService _scannerBarcodeService;
    private readonly ILogger<ScannerRecoveryDialogViewModel> _logger;
    private bool _subscribed;
    private bool _disposed;

    public ScannerRecoveryDialogViewModel(
        IDeviceConnectionManager deviceManager,
        IScannerBarcodeService scannerBarcodeService,
        ILogger<ScannerRecoveryDialogViewModel> logger)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _scannerBarcodeService = scannerBarcodeService ?? throw new ArgumentNullException(nameof(scannerBarcodeService));
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

    [ObservableProperty]
    private string _closeButtonText = "关闭";

    /// <summary>仅在扫描枪恢复流程完成后允许弹窗接收扫码数据。</summary>
    [ObservableProperty]
    private bool _isBarcodeReceptionEnabled;

    /// <summary>恢复弹窗中首个通过现有产品条码校验、等待用户确认的条码。</summary>
    public BarcodeParsedEventArgs? VerifiedProductBarcode { get; private set; }

    public event Action<bool?>? RequestClose;

    public async Task InitializeAsync()
    {
        SubscribeToScannerBarcode();
        ClearVerificationResult();

        IsBusy = true;
        CanClose = false;
        CanDeepRecoverButton = false;
        IsBarcodeReceptionEnabled = false;
        BarcodeText = "恢复过程中暂不接收扫码……";
        ReceiveStatusText = "接收状态：正在重连，扫码暂不可用";
        StatusText = "正在重新连接扫描枪，请稍候……";

        try
        {
            await _deviceManager.ReconnectDeviceAsync("Scanner").ConfigureAwait(true);
            if (_deviceManager.IsScannerConnected)
            {
                IsBarcodeReceptionEnabled = true;
                BarcodeText = "等待扫码……";
                StatusText = BuildProductScanPrompt("扫描枪串口已重新打开。");
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

    [RelayCommand(CanExecute = nameof(CanStartDeepRecovery))]
    private async Task DeepRecoverAsync()
    {
        ClearVerificationResult();
        IsBusy = true;
        CanClose = false;
        CanDeepRecoverButton = false;
        IsBarcodeReceptionEnabled = false;
        BarcodeText = "恢复过程中暂不接收扫码……";
        ReceiveStatusText = "接收状态：正在重启数据通道，扫码暂不可用";
        StatusText = "正在重启扫描枪数据通道，请暂勿扫码……";

        try
        {
            var result = await _deviceManager
                .DeepRecoverScannerAsync()
                .ConfigureAwait(true);
            if (result.Succeeded)
            {
                var drainedBytes = result.DrainResult?.DrainedBytes ?? 0;
                IsBarcodeReceptionEnabled = true;
                BarcodeText = "等待扫码……";
                StatusText = BuildProductScanPrompt("扫描枪数据通道已重启。");
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
        _scannerBarcodeService.BarcodeParsed += OnBarcodeParsed;
        _deviceManager.ScannerDeepRecoveryProgressChanged += OnDeepRecoveryProgressChanged;
        _subscribed = true;
    }

    public void UnsubscribeFromScannerEvents()
    {
        if (!_subscribed)
            return;

        _scannerBarcodeService.BarcodeReceived -= OnBarcodeReceived;
        _scannerBarcodeService.BarcodeParsed -= OnBarcodeParsed;
        _deviceManager.ScannerDeepRecoveryProgressChanged -= OnDeepRecoveryProgressChanged;
        _subscribed = false;
    }

    private void OnDeepRecoveryProgressChanged(object? sender, string message)
    {
        ApplyOnUiThread(() => StatusText = message);
    }

    private void OnBarcodeReceived(object? sender, BarcodeReceivedEventArgs e)
    {
        if (!IsBarcodeReceptionEnabled)
        {
            _logger.LogWarning(
                "[扫描枪恢复弹窗] 恢复进行中忽略扫码: Length={Length}",
                e.RawResponse.Length);
            return;
        }

        // 原始事件早于解析事件到达，先清除上一次结果，避免无效新条码沿用旧有效条码。
        VerifiedProductBarcode = null;
        var displayText = FormatBarcodeForDisplay(e.Barcode);
        ApplyOnUiThread(() =>
        {
            BarcodeText = displayText;
            CloseButtonText = "关闭";
            ReceiveStatusText = $"接收状态：已收到扫码数据，长度 {e.RawResponse.Length} 字节，正在校验产品条码……";
            StatusText = "已收到扫码数据，正在校验产品条码……";
            CanClose = true;
            _logger.LogInformation(
                "[扫描枪恢复弹窗] 收到原始扫码，等待产品条码校验: Length={Length}",
                e.RawResponse.Length);
        });
    }

    private void OnBarcodeParsed(object? sender, BarcodeParsedEventArgs e)
    {
        if (!IsBarcodeReceptionEnabled)
            return;

        if (!e.IsProductBarcodeValid)
        {
            VerifiedProductBarcode = null;
            ApplyOnUiThread(() =>
            {
                CloseButtonText = "关闭";
                ReceiveStatusText = "接收状态：产品条码无效，未使用";
                StatusText =
                    $"本次条码不能用于生产：{e.ParseFailureReason ?? "产品条码校验失败"}\n" +
                    "请重新扫描当前产品条码。";
                _logger.LogWarning(
                    "[扫描枪恢复弹窗] 产品条码无效，未保存: Reason={Reason}",
                    e.ParseFailureReason ?? "产品条码校验失败");
            });
            return;
        }

        VerifiedProductBarcode = e;
        ApplyOnUiThread(() =>
        {
            CloseButtonText = "使用此条码";
            ReceiveStatusText = "接收状态：产品条码有效，等待确认使用";
            StatusText =
                $"已收到有效产品条码。\n机种：{e.ModelName}\n序列号：{e.SerialPart}\n\n" +
                "请点击“使用此条码”返回运行页。";
            _logger.LogInformation(
                "[扫描枪恢复弹窗] 产品条码有效，等待用户确认: Model={Model}, Serial={Serial}",
                e.ModelName,
                e.SerialPart);
        });
    }

    private void ClearVerificationResult()
    {
        VerifiedProductBarcode = null;
        CloseButtonText = "关闭";
        BarcodeText = "等待扫码……";
        ReceiveStatusText = "接收状态：尚未收到扫码数据";
    }

    private static string BuildProductScanPrompt(string prefix)
        => prefix +
           "\n\n请扫描当前产品条码。\n若蜂鸣后仍显示“等待扫码”，请松开扳机并再次扫描。";

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
