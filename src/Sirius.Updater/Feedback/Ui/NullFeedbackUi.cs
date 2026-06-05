using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.Feedback.Ui;

/// <summary>
/// Fail-fast sentinel UI mirroring <c>NullUpdateUi</c>. Used when the
/// consumer hasn't attached a real UI — better than silently hanging on
/// a compose call that nobody can see.
/// </summary>
public sealed class NullFeedbackUi : IFeedbackUi
{
    public static readonly NullFeedbackUi Instance = new();
    NullFeedbackUi() { }

    public Task<IFeedbackUiSession> PresentAsync(FeedbackComposeContext context, CancellationToken cancel)
    {
        throw new InvalidOperationException(
            "SiriusFeedback has no UI attached. Wire one up at startup " +
            "(e.g. feedback = feedback.WithWinUI(rootElement) on a WinUI 3 host) " +
            "or set SiriusFeedbackServices.Ui explicitly.");
    }
}

/// <summary>
/// Headless session used by tests / non-interactive flows when the
/// caller pre-fills the request and never wants a UI. Returns the
/// pre-fill verbatim on <see cref="ComposeAsync"/>.
/// </summary>
public sealed class HeadlessFeedbackUiSession : IFeedbackUiSession
{
    readonly FeedbackComposeOutcome _outcome;
    public HeadlessFeedbackUiSession(FeedbackComposeOutcome outcome) => _outcome = outcome;

    public Task<FeedbackComposeOutcome> ComposeAsync(CancellationToken cancel) => Task.FromResult(_outcome);
    public void TransitionToSubmitting(string title, string? subtitle) { }
    public void ReportProgress(string? title, string? subtitle, double? percent) { }
    public void ShowSuccess(string issueUrl, int issueNumber, IReadOnlyList<UploadedAttachment> attachments) { }
    public void ShowError(string title, string? message) { }
    public Task CloseAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
