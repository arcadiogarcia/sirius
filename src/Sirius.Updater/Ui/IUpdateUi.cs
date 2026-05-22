using System;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Auth;

namespace Sirius.Updater.Ui;

/// <summary>
/// UI surface for the updater. Implementations show a sign-in dialog
/// when needed and morph it into a progress view as the update runs.
///
/// The interface is split into the IUpdateUi (factory) and
/// <see cref="IUpdateUiSession"/> (instance) so the caller can keep
/// the dialog alive across the Check → Auth → Download → Install
/// transitions without having to coordinate state externally.
///
/// The WinUI integration ships <c>ContentDialogUpdateUi</c>; the core
/// library ships <see cref="NullUpdateUi"/>. Custom UIs are
/// straightforward — see <c>ContentDialogUpdateUi</c> for the canonical
/// implementation.
/// </summary>
public interface IUpdateUi
{
    /// <summary>
    /// Show the sign-in dialog and return an active session handle.
    /// The session stays alive until <see cref="IUpdateUiSession.CloseAsync"/>
    /// is called or the user dismisses it.
    /// </summary>
    Task<IUpdateUiSession> PresentAsync(DeviceCodeInfo info, CancellationToken cancel);
}

/// <summary>
/// Caller-owned handle for an active update dialog. See
/// <see cref="IUpdateUi"/> for the lifecycle contract.
/// </summary>
public interface IUpdateUiSession : IAsyncDisposable
{
    /// <summary>
    /// Completes (throws <see cref="OperationCanceledException"/>) when
    /// the user dismisses the dialog before the caller transitions it
    /// to progress mode. After <see cref="TransitionToProgress"/> the
    /// task never completes — the dialog is owned by the caller.
    /// </summary>
    Task UserDismissed { get; }

    /// <summary>
    /// Switch from "enter code" mode to "update in progress" mode.
    /// Removes the code/copy/cancel affordances and reveals a progress
    /// bar + status text. Idempotent.
    /// </summary>
    void TransitionToProgress(string title, string? subtitle);

    /// <summary>
    /// Update the progress view. <paramref name="percent"/> in [0,100]
    /// yields a determinate bar; null yields an indeterminate spinner.
    /// Null title/subtitle leave the existing text untouched.
    /// </summary>
    void ReportProgress(string? title, string? subtitle, double? percent);

    /// <summary>Programmatically close the dialog.</summary>
    Task CloseAsync();
}
