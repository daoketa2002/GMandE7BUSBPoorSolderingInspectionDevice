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
    private readonly bool _isSemiPhysicalDebugMode;
    private CancellationTokenSource? _pollingCts;
    private bool _dt303ReadFailureActive;
    private bool _hasInitialDt303Read;
    private bool _lastDt303Value;

    [ObservableProperty]
    private bool _isAlarmReleased;

    [ObservableProperty]
    private bool _isReleaseInProgress;

    [ObservableProperty]
    private string _statusText = "等待报警解除信号 DT303=1";

    /// <summary>半实物/Fake 调试按钮是否可见。</summary>
    public bool CanSimulateAlarmRelease => _canSimulateAlarmRelease;

    public EmergencyStopDialogViewModel(
        IPlcDevice plcDevice,
        Func<Task<bool>> releaseEmergencyStop,
        bool canSimulateAlarmRelease,
        bool isSemiPhysicalDebugMode = false,
        ILogger<EmergencyStopDialogViewModel>? logger = null)
    {
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _releaseEmergencyStop = releaseEmergencyStop ?? throw new ArgumentNullException(nameof(releaseEmergencyStop));
        _canSimulateAlarmRelease = canSimulateAlarmRelease;
        _isSemiPhysicalDebugMode = isSemiPhysicalDebugMode;
        _logger = logger;
    }

    /// <summary>
    /// DT303 门禁状态变化时刷新解除命令的 CanExecute。
    /// </summary>
    partial void OnIsAlarmReleasedChanged(bool value)
    {
        ReleaseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 解除执行状态变化时刷新解除命令的 CanExecute，防止重复提交。
    /// </summary>
    partial void OnIsReleaseInProgressChanged(bool value)
    {
        ReleaseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 弹窗加载后调用，启动 DT303 后台轮询。
    /// </summary>
    public void StartPolling()
    {
        StopPolling();

        // 每次弹窗打开都从本地禁用状态开始，不向 PLC 写入 DT303。
        IsAlarmReleased = false;
        IsReleaseInProgress = false;
        StatusText = "等待报警解除信号 DT303=1";
        _dt303ReadFailureActive = false;
        _hasInitialDt303Read = false;
        _lastDt303Value = false;

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
                    if (_dt303ReadFailureActive)
                    {
                        _dt303ReadFailureActive = false;
                        _logger?.LogInformation("[急停弹窗][DT303读取恢复] 已恢复读取");
                    }

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => UpdateAlarmState(result.Value));
                }
                else if (!ct.IsCancellationRequested)
                {
                    await DisableAlarmReleaseAsync();
                    LogDt303ReadFailure(result.Message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    break;

                await DisableAlarmReleaseAsync();
                LogDt303ReadFailure(ex.Message, ex);
            }

            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// DT303 读取失败时在 UI 线程安全地禁用解除按钮。
    /// </summary>
    private async Task DisableAlarmReleaseAsync()
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsAlarmReleased = false;
            StatusText = "报警解除信号读取失败，解除按钮已禁用";
        });
    }

    /// <summary>
    /// 连续读取失败只记录一次，避免每 500ms 重复刷相同 Warning。
    /// </summary>
    private void LogDt303ReadFailure(string message, Exception? exception = null)
    {
        if (_dt303ReadFailureActive)
            return;

        _dt303ReadFailureActive = true;
        if (exception == null)
        {
            _logger?.LogWarning(
                "[急停弹窗][DT303读取失败] 解除按钮已禁用，Message={Message}",
                message);
        }
        else
        {
            _logger?.LogWarning(
                exception,
                "[急停弹窗][DT303读取失败] 解除按钮已禁用，Message={Message}",
                message);
        }
    }

    /// <summary>
    /// 更新 DT303 报警解除状态。
    /// </summary>
    private void UpdateAlarmState(bool alarmReleased)
    {
        if (!_hasInitialDt303Read)
        {
            _hasInitialDt303Read = true;
            _lastDt303Value = alarmReleased;
            _logger?.LogInformation(
                "[急停弹窗][DT303首次读取] Value={Value}",
                alarmReleased ? 1 : 0);
        }
        else if (_lastDt303Value != alarmReleased)
        {
            if (_isSemiPhysicalDebugMode)
            {
                _logger?.LogInformation(
                    "[半实物][PLC信号变化] DT303: {Previous} -> {Current}, Action=AlarmRelease",
                    _lastDt303Value ? 1 : 0,
                    alarmReleased ? 1 : 0);
            }

            _logger?.LogWarning(
                "[急停弹窗][DT303变化] {Previous} -> {Current}，解除按钮已{Action}",
                _lastDt303Value ? 1 : 0,
                alarmReleased ? 1 : 0,
                alarmReleased ? "启用" : "禁用");

            _lastDt303Value = alarmReleased;
        }

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
    [RelayCommand(CanExecute = nameof(CanRelease))]
    private async Task ReleaseAsync()
    {
        if (!CanRelease())
            return;

        IsReleaseInProgress = true;
        try
        {
            var result = await _plcDevice.ReadAlarmReleasedAsync();
            if (!result.IsSuccess)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsAlarmReleased = false;
                    StatusText = "报警解除信号未确认，请等待设备解除";
                });
                _logger?.LogWarning(
                    "[急停弹窗][解除点击拒绝] DT303读取失败，解除按钮已禁用，Message={Message}",
                    result.Message);
                return;
            }

            if (!result.Value)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsAlarmReleased = false;
                    StatusText = "报警解除信号未确认，请等待设备解除";
                });
                _logger?.LogWarning("[急停弹窗][解除点击拒绝] DT303未确认等于1，解除按钮已禁用");
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsAlarmReleased = true;
                StatusText = "已收到报警解除信号，可以解除弹窗";
            });
            _logger?.LogWarning("[急停弹窗][解除点击确认] DT303=1，提交正式急停解除请求");

            bool released = await _releaseEmergencyStop();
            if (!released)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsAlarmReleased = false;
                    StatusText = "急停信号未确认释放，请检查设备状态后重试";
                });
            }
        }
        catch (Exception ex)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsAlarmReleased = false;
                StatusText = "急停信号未确认释放，请检查设备状态后重试";
            });
            _logger?.LogWarning(ex, "[急停弹窗][解除失败] DT303复核或正式解除过程发生异常");
        }
        finally
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => IsReleaseInProgress = false);
        }
    }

    /// <summary>
    /// 只有 DT303 已确认且当前没有执行解除请求时才允许点击。
    /// </summary>
    private bool CanRelease()
    {
        return IsAlarmReleased && !IsReleaseInProgress;
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

        if (_isSemiPhysicalDebugMode)
        {
            _logger?.LogInformation(
                "[半实物][调试动作] Action=AlarmRelease, Signal=DT303, Value=1");
        }

        var result = await _plcDevice.RequestAlarmReleaseAsync(default).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            if (_isSemiPhysicalDebugMode)
            {
                _logger?.LogWarning(
                    "[半实物][异常注入] Scenario=AlarmReleaseWriteFailure, ExpectedFailure=true, Message={Message}",
                    result.Message);
            }

            _logger?.LogWarning("[急停弹窗] 模拟写入 DT303=1 失败: {Message}", result.Message);
            return;
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => UpdateAlarmState(true));

        _logger?.LogWarning("[急停弹窗][调试] 已写入 DT303=1，直接启用解除按钮");
    }
}
