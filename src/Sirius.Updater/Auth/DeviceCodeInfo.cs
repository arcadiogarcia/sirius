namespace Sirius.Updater.Auth;

/// <summary>
/// Snapshot of an OAuth Device-Flow code presented to the user.
/// Backend-agnostic so a future non-GitHub authenticator can reuse the
/// same <see cref="Ui.IUpdateUi"/> surface.
/// </summary>
public sealed record DeviceCodeInfo(
    /// <summary>The short user code to display.</summary>
    string UserCode,
    /// <summary>Canonical verification URL (e.g. <c>https://github.com/login/device</c>).</summary>
    string VerificationUri,
    /// <summary>How long the user has before the code expires.</summary>
    System.TimeSpan ExpiresIn,
    /// <summary>Optional richer URL — may pre-fill the code and hint at the right account.</summary>
    string? VerificationUriComplete = null,
    /// <summary>Login (account name) the user is expected to sign in as.</summary>
    string? SuggestedLogin = null);
