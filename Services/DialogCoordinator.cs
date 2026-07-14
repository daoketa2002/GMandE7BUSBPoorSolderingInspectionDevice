using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services;

/// <summary>
/// 运行页弹窗的最小串行协调器。
/// 只负责租约和急停独占状态，不承担窗口创建、优先级队列或业务逻辑。
/// </summary>
public sealed class DialogCoordinator : IDialogCoordinator, IDisposable
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly object _stateSync = new();
    private TaskCompletionSource<bool>? _emergencyDialogCompleted;
    private int _emergencyDialogPendingOrActive;

    public bool IsEmergencyDialogPendingOrActive
        => Volatile.Read(ref _emergencyDialogPendingOrActive) != 0;

    public async Task<IAsyncDisposable> AcquireOrdinaryDialogAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? emergencyCompletionTask;
            lock (_stateSync)
            {
                emergencyCompletionTask = _emergencyDialogCompleted?.Task;
            }

            if (emergencyCompletionTask != null)
            {
                await emergencyCompletionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await _dialogGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_stateSync)
            {
                if (_emergencyDialogCompleted == null)
                    return new DialogLease(ReleaseOrdinaryDialog);

                emergencyCompletionTask = _emergencyDialogCompleted.Task;
            }

            _dialogGate.Release();
            await emergencyCompletionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IAsyncDisposable> AcquireEmergencyDialogAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? existingEmergencyTask;
            lock (_stateSync)
            {
                existingEmergencyTask = _emergencyDialogCompleted?.Task;
                if (existingEmergencyTask == null)
                {
                    _emergencyDialogCompleted = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    Interlocked.Exchange(ref _emergencyDialogPendingOrActive, 1);
                }
            }

            if (existingEmergencyTask != null)
            {
                await existingEmergencyTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _dialogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new DialogLease(ReleaseEmergencyDialog);
            }
            catch
            {
                CompleteEmergencyReservation();
                throw;
            }
        }
    }

    private void ReleaseOrdinaryDialog()
    {
        _dialogGate.Release();
    }

    private void ReleaseEmergencyDialog()
    {
        CompleteEmergencyReservation();
        _dialogGate.Release();
    }

    private void CompleteEmergencyReservation()
    {
        TaskCompletionSource<bool>? completion;
        lock (_stateSync)
        {
            completion = _emergencyDialogCompleted;
            _emergencyDialogCompleted = null;
            Volatile.Write(ref _emergencyDialogPendingOrActive, 0);
        }

        completion?.TrySetResult(true);
    }

    public void Dispose()
    {
        _dialogGate.Dispose();
    }

    private sealed class DialogLease : IAsyncDisposable, IDisposable
    {
        private readonly Action _release;
        private int _released;

        public DialogLease(Action release)
        {
            _release = release;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _release();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
