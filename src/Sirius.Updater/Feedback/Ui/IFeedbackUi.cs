using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.Feedback.Ui;

/// <summary>
/// UI surface for the feedback flow. Implementations show a compose
/// dialog, wait for the user to fill in the form + click Submit (or
/// Cancel), then receive caller-driven transitions through Submitting →
/// Submitted/Error states.
///
/// The WinUI integration ships <c>ContentDialogFeedbackUi</c>; the core
/// library ships <see cref="NullFeedbackUi"/> which fails fast so a
/// caller without a real UI hears about it instead of silently hanging.
/// </summary>
public interface IFeedbackUi
{
    /// <summary>
    /// Show the compose dialog and return an active session. The
    /// session stays alive across compose → (auth) → submit → result
    /// transitions and is owned by the caller.
    /// </summary>
    Task<IFeedbackUiSession> PresentAsync(FeedbackComposeContext context, CancellationToken cancel);
}

/// <summary>
/// Pre-fill + context for the compose dialog.
/// </summary>
public sealed record FeedbackComposeContext(
    string ProductName,
    string Repository,
    string AttachmentsRepository,
    string AttachmentsBranch,
    string? InitialTitle,
    string? InitialBody,
    IReadOnlyList<FeedbackAttachment> Attachments);

/// <summary>
/// Caller-owned handle for an active feedback dialog.
/// </summary>
public interface IFeedbackUiSession : IAsyncDisposable
{
    /// <summary>
    /// Completes when the user clicks Submit (carrying their edited
    /// title/body + the final include flags on each attachment), or
    /// throws <see cref="OperationCanceledException"/> when they cancel.
    /// </summary>
    Task<FeedbackComposeOutcome> ComposeAsync(CancellationToken cancel);

    /// <summary>Switch to the submitting view.</summary>
    void TransitionToSubmitting(string title, string? subtitle);

    /// <summary>Push progress while submitting (uploads + post).</summary>
    void ReportProgress(string? title, string? subtitle, double? percent);

    /// <summary>Switch to the success view with a "View on GitHub" link.</summary>
    void ShowSuccess(string issueUrl, int issueNumber, IReadOnlyList<UploadedAttachment> attachments);

    /// <summary>Switch to the error view with an optional recovery hint.</summary>
    void ShowError(string title, string? message);

    /// <summary>Programmatically close the dialog.</summary>
    Task CloseAsync();
}

/// <summary>
/// Result of the compose phase. Carries the user-edited values that
/// will overwrite the request before submit. The
/// <see cref="Attachments"/> list reflects per-file opt-out (entries
/// the user un-checked have <see cref="FeedbackAttachment.Include"/>
/// set to false).
/// </summary>
public sealed record FeedbackComposeOutcome(
    string Title,
    string Body,
    IReadOnlyList<FeedbackAttachment> Attachments);
