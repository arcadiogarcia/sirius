using System;

namespace Sirius.Updater;

/// <summary>
/// A snapshot of update activity, surfaced through
/// <see cref="IProgress{T}"/> callbacks supplied to
/// <see cref="SiriusUpdater.UpdateAsync"/>. Also flows into the
/// <see cref="Ui.IUpdateUi"/> session for in-dialog progress.
/// </summary>
public sealed record UpdateProgress(
    UpdateStage Stage,
    string Title,
    string? Detail,
    double? Percent,
    long? BytesTransferred = null,
    long? BytesTotal       = null);

/// <summary>
/// High-level phase of the update pipeline. The UI layer maps these to
/// visual states (e.g. indeterminate spinner vs. progress bar).
/// </summary>
public enum UpdateStage
{
    /// <summary>Talking to GitHub to figure out the latest release.</summary>
    Checking,
    /// <summary>Waiting for the user to complete the Device Flow.</summary>
    AwaitingAuthorization,
    /// <summary>Streaming the MSIX asset to local disk.</summary>
    Downloading,
    /// <summary>Verifying / preparing the package before hand-off.</summary>
    Preparing,
    /// <summary>Installer running; this process is about to be replaced.</summary>
    Installing,
    /// <summary>Terminal success.</summary>
    Completed,
    /// <summary>Terminal failure — see the matching result record.</summary>
    Failed,
}
