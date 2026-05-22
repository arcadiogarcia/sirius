using System;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Auth;

namespace Sirius.Updater.Ui;

/// <summary>
/// Headless UI used when no real UI is wired up. Keeps the caller alive
/// while the auth poll runs but renders nothing. Useful for CI and
/// tests; the WinUI integration replaces this with
/// <c>ContentDialogUpdateUi</c>.
/// </summary>
public sealed class NullUpdateUi : IUpdateUi
{
    public static readonly NullUpdateUi Instance = new();

    public Task<IUpdateUiSession> PresentAsync(DeviceCodeInfo info, CancellationToken cancel)
        => Task.FromResult<IUpdateUiSession>(new NullSession(cancel));

    sealed class NullSession : IUpdateUiSession
    {
        readonly TaskCompletionSource<bool> _dismissed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration _reg;

        public NullSession(CancellationToken cancel)
        {
            // Forward external cancellation so the caller's WhenAny race
            // sees the dismissal exception consistently with a real UI.
            _reg = cancel.Register(() =>
                _dismissed.TrySetException(new OperationCanceledException(
                    "Null update UI cancelled.", cancel)));
        }

        public Task UserDismissed => _dismissed.Task;
        public void TransitionToProgress(string title, string? subtitle) { }
        public void ReportProgress(string? title, string? subtitle, double? percent) { }
        public Task CloseAsync() { _reg.Dispose(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _reg.Dispose(); return ValueTask.CompletedTask; }
    }
}
