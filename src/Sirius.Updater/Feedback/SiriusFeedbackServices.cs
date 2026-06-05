using Sirius.Updater.Auth;
using Sirius.Updater.Diagnostics;
using Sirius.Updater.Feedback.AttachmentStorage;
using Sirius.Updater.Feedback.Sinks;
using Sirius.Updater.Feedback.Ui;
using Sirius.Updater.TokenStore;

namespace Sirius.Updater.Feedback;

/// <summary>
/// Optional advanced overrides for <see cref="SiriusFeedback"/>. Each
/// property is null by default — the facade fills in a built-in
/// implementation in that case.
/// </summary>
public sealed record SiriusFeedbackServices
{
    /// <summary>Sink that turns the composed request into a filed
    /// issue. Defaults to <see cref="GitHubIssuesSink"/>.</summary>
    public IFeedbackSink? Sink { get; init; }

    /// <summary>Attachment storage backend. Defaults to
    /// <see cref="GitHubRepoBranchStorage"/>.</summary>
    public IAttachmentStorage? Storage { get; init; }

    /// <summary>Token store chain. Defaults to the same layered
    /// configuration <see cref="SiriusUpdater"/> uses, with matching
    /// DPAPI directory + entropy so the two modules share a single
    /// signed-in session per ProductName.</summary>
    public ITokenStore? TokenStore { get; init; }

    /// <summary>Interactive auth. Defaults to
    /// <see cref="GitHubDeviceFlowAuthenticator"/>.</summary>
    public IAuthenticator? Authenticator { get; init; }

    /// <summary>
    /// Auth UI surface — used during interactive sign-in only. Most
    /// consumers point this at the same
    /// <c>ContentDialogUpdateUi</c> they wired up for the updater so
    /// the device-code dialog has consistent UX across both flows.
    /// </summary>
    public Sirius.Updater.Ui.IUpdateUi? AuthUi { get; init; }

    /// <summary>Feedback UI surface — compose dialog, submitting view,
    /// success/error view. Defaults to <see cref="NullFeedbackUi"/>,
    /// which fails fast. The WinUI integration ships
    /// <c>ContentDialogFeedbackUi</c>.</summary>
    public IFeedbackUi? Ui { get; init; }

    /// <summary>Diagnostic log sink. Defaults to
    /// <see cref="NullUpdaterLog"/>.</summary>
    public IUpdaterLog? Log { get; init; }
}
