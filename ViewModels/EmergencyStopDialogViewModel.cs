using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces.Devices;
using Microsoft.Extensions.Logging;

namespace GMandE7BUSBPoorSolderingInspectionDevice.ViewModels;

/// <summary>
/// 急停锁定弹窗 ViewModel。
/// 只负责显示 DT303 报警解除状态和提交用户解除请求，最终运行状态由运行页读回 DT123 后决定。
/// </summary>
public partial class EmergencyStopDialogViewModel : ObservableObject
{
    private readonly ILogger<EmergencyStopDialogViewModel>? _logger;
    private readonly IPlcDevice _plcDevice;
    private readonly Func<Task<bool>> _releaseEmergencyStop;
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
        Func<Task<bool>> releaseEmergencyStop,
        bool canSimulateAlarmRelease,
        ILogger<EmergencyStopDialogViewModel>? logger = null)
    {
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _releaseEmergencyStop = releaseEmergencyStop ?? throw new ArgumentNullException(nameof(releaseEmergencyStop));
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
                var result = await _plcDevice.ReadAlarmReleasedAsync(ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => UpdateAlarmState(result.Value));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 弹窗轮询失败不打断主流程，运行页解除时会做正式读回确认。
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
    /// 用户点击“解除”按钮。弹窗只提交请求，不直接清 PLC 或切换运行页状态。
    /// </summary>
    [RelayCommand]
    private async Task ReleaseAsync()
    {
        if (!IsAlarmReleased)
            return;

        _logger?.LogWarning("[急停弹窗][审计] 用户点击解除按钮，提交急停解除请求");

        bool released = await _releaseEmergencyStop();
        if (!released)
        {
            StatusText = "急停信号未确认释放，请检查后重试";
        }
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

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => UpdateAlarmState(true));

        _logger?.LogWarning("[急停弹窗][调试] 已写入 DT303=1，直接启用解除按钮");
    }
}
