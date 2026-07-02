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
    private readonly bool _isFakeMode;
    private CancellationTokenSource? _pollingCts;

    [ObservableProperty]
    private bool _isAlarmReleased;

    [ObservableProperty]
    private string _statusText = "等待报警解除信号 DT303=1";

    /// <summary>Fake 调试按钮是否可见。</summary>
    public bool IsFakeModeVisible => _isFakeMode;

    public EmergencyStopDialogViewModel(
        IPlcDevice plcDevice,
        Action closeDialog,
        bool isFakeMode,
        ILogger<EmergencyStopDialogViewModel>? logger = null)
    {
        _plcDevice = plcDevice ?? throw new ArgumentNullException(nameof(plcDevice));
        _closeDialog = closeDialog ?? throw new ArgumentNullException(nameof(closeDialog));
        _isFakeMode = isFakeMode;
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
    /// 用户点击"解除"按钮。写 DT303=0，关闭弹窗。
    /// </summary>
    [RelayCommand]
    private async Task ReleaseAsync()
    {
        if (!IsAlarmReleased)
            return;

        _logger?.LogWarning("[急停弹窗][审计] 用户点击解除按钮，清除 DT303=0");

        var result = await _plcDevice.ClearAlarmReleasedAsync(default);
        if (!result.IsSuccess)
        {
            _logger?.LogWarning("[急停弹窗] 清除 DT303 失败: {Message}", result.Message);
            return;
        }

        // 关闭弹窗前更新本地状态
        UpdateAlarmState(false);
        _closeDialog();
    }

    /// <summary>
    /// Fake 调试用：模拟 PLC 置 DT303=1。
    /// 仅在 Fake 模式下可用，真实 PLC 模式下按钮隐藏。
    /// </summary>
    [RelayCommand]
    private async Task FakeTriggerAlarmReleaseAsync()
    {
        if (!_isFakeMode)
            return;

        if (_plcDevice is GMandE7BUSBPoorSolderingInspectionDevice.Devices.Fakes.FakeInspectionHardware fake)
        {
            await fake.WriteInputRegisterAsync(PlcAddressMap.AlarmReleased, 1).ConfigureAwait(false);

            // 同时更新本地状态，无需等轮询
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => UpdateAlarmState(true));

            _logger?.LogWarning("[急停弹窗][Fake调试] 模拟 PLC 写入 DT303=1");
        }
    }
}
