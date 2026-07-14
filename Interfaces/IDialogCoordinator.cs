namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

/// <summary>
/// 统一协调普通通知和急停对话框的显示租约，避免模态窗口互相覆盖。
/// </summary>
public interface IDialogCoordinator
{
    /// <summary>获取普通通知显示租约。</summary>
    Task<IAsyncDisposable> AcquireOrdinaryDialogAsync(CancellationToken cancellationToken = default);

    /// <summary>获取急停对话框显示租约。申请后普通设备提示不得再叠加。</summary>
    Task<IAsyncDisposable> AcquireEmergencyDialogAsync(CancellationToken cancellationToken = default);

    /// <summary>急停对话框正在等待或已经显示。</summary>
    bool IsEmergencyDialogPendingOrActive { get; }
}
