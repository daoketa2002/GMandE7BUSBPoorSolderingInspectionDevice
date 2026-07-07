using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.PLC动作控制;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

/// <summary>
/// 急停锁定弹窗 ViewModel。
/// 管理 DT303 报警解除状态和解除按钮可用性。
/// 启动后台轮询读取 DT303，Fake 模式下提供模拟 DT303=1 的调试按钮。
/// </summary>
public partial class EmergencyStopDialogViewModel : ObservableObject
{
    private readonly ILogger<EmergencyStopDialogViewModel>? _logger;
    private readonly IPlcDevice _plcDevice;
    private readonly Action _closeDialog;
    private readonly bool _canSimulateAlarmRelease;
    private CancellationTokenSource? _pollingCts;

    [ObservableProperty]
    private bool _isAlarmReleased;

    [ObservableProperty]
    private string _statusText = "等待报警解除信号 DT303=1";

    /// <summary>半实物/Fake 调试按钮是否可见。</summary>
    public bool CanSimulateAlarmRelease => _canSimulateAlarmRelease;

    public EmergencyStopDialogViewModel(
        IPlcDevice plcDevice,
        Action closeDialog,
        bool canSimulateAlarmRelease,
        ILogger<EmergencyStopDialogViewModel>? logger = null)
    {
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _closeDialog = closeDialog ?? throw new ArgumentNullException(nameof(closeDialog));
        _canSimulateAlarmRelease = canSimulateAlarmRelease;
        _logger = logger;
    }

    /// <summary>
    /// 弹窗加载后调用，启动 DT303 后台轮询。
    /// </summary>
    public void StartPolling()
    {
        _pollingCts = new CancellationTokenSource();
        _ = PollDt303Async(_pollingCts.Token);
    }

    /// <summary>
    /// 弹窗关闭时调用，停止轮询。
    /// </summary>
    public void StopPolling()
    {
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;
    }

    /// <summary>
    /// 后台轮询 DT303 状态。
    /// </summary>
    private async Task PollDt303Async(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _plcDevice.ReadMachineInputsAsync(ct).ConfigureAwait(false);
                if (result.IsSuccess && result.Value != null)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => UpdateAlarmState(result.Value.IsAlarmReleased));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 轮询异常不中断循环
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 更新 DT303 报警解除状态。
    /// </summary>
    private void UpdateAlarmState(bool alarmReleased)
    {
        if (IsAlarmReleased == alarmReleased)
            return;

        IsAlarmReleased = alarmReleased;

        if (alarmReleased)
        {
            StatusText = "已收到报警解除信号，可以解除弹窗";
            _logger?.LogWarning("[急停弹窗] DT303 从 0→1，解除按钮已启用");
        }
        else
        {
            StatusText = "等待报警解除信号 DT303=1";
        }
    }

    /// <summary>
    /// 用户点击"解除"按钮。写 DT123=0 和 DT303=0，关闭弹窗。
    /// 即使清除失败也关闭弹窗，避免弹窗卡死。
    /// 清除失败后由 TestPageViewModel.RetryClearEmergencySignalsAsync() 接管重试。
    /// </summary>
    [RelayCommand]
    private async Task ReleaseAsync()
    {
        if (!IsAlarmReleased)
            return;

        _logger?.LogWarning("[急停弹窗][审计] 用户点击解除按钮，清除 DT123=0 和 DT303=0");

        // 先尝试清除 DT123
        var emergencyResult = await _plcDevice.ClearEmergencyStopRequestAsync(default);
        if (!emergencyResult.IsSuccess)
        {
            _logger?.LogWarning("[急停弹窗] 清除 DT123 失败: {Message}，弹窗照常关闭，交由后台重试", emergencyResult.Message);
        }

        // 再尝试清除 DT303
        var alarmResult = await _plcDevice.ClearAlarmReleasedAsync(default);
        if (!alarmResult.IsSuccess)
        {
            _logger?.LogWarning("[急停弹窗] 清除 DT303 失败: {Message}，弹窗照常关闭，交由后台重试", alarmResult.Message);
        }

        // ★ 无论清除成功与否，都关闭弹窗，避免弹窗卡死
        // TestPageViewModel.RetryClearEmergencySignalsAsync() 会在后台接管失败重试
        _closeDialog();
    }

    /// <summary>
    /// 半实物/Fake 调试用：模拟 PLC 置 DT303=1。
    /// 写成功后直接启用解除按钮，不等待下一次 DT303 轮询读回。
    /// </summary>
    [RelayCommand]
    private async Task TriggerAlarmReleaseAsync()
    {
        if (!_canSimulateAlarmRelease)
            return;

        var result = await _plcDevice.RequestAlarmReleaseAsync(default).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger?.LogWarning("[急停弹窗] 模拟写入 DT303=1 失败: {Message}", result.Message);
            return;
        }

        // 半实物/Fake 调试按钮已经由上位机主动写入 DT303=1，无需再等轮询确认。
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => UpdateAlarmState(true));

        _logger?.LogWarning("[急停弹窗][调试] 已写入 DT303=1，直接启用解除按钮");
    }
}
