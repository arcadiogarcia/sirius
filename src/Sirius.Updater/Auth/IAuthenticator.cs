using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Ui;

namespace Sirius.Updater.Auth;

/// <summary>
/// Drives interactive authentication when a token is needed but none
/// is cached (or the cached one is invalid). The default
/// <see cref="GitHubDeviceFlowAuthenticator"/> implementation walks the
/// user through the GitHub OAuth Device Flow.
/// </summary>
public interface IAuthenticator
{
    /// <summary>
    /// Acquire a token interactively. Implementations should drive the
    /// supplied <paramref name="ui"/> for user feedback when not null;
    /// when null they should still complete (returning a token) for
    /// headless flows (env-only auth, CI).
    /// </summary>
    /// <param name="http">Shared HttpClient — caller controls UA + timeout.</param>
    /// <param name="ui">Optional UI surface for displaying the device code.</param>
    /// <param name="suggestedLogin">Account hint to surface in the UI.</param>
    /// <returns>Active session (token + handle to the UI dialog the
    /// caller can morph into progress UX), or null if no UI is available
    /// and no token could be acquired headlessly.</returns>
    Task<AuthenticationResult?> AuthenticateAsync(
        HttpClient http,
        IUpdateUi? ui,
        string? suggestedLogin,
        CancellationToken cancel);
}

/// <summary>
/// Outcome of a successful interactive auth. The
/// <see cref="UiSession"/> may be null when the auth ran headlessly
/// (no UI available); when non-null, the caller owns it and is
/// responsible for morphing it into "Downloading…" / "Installing…"
/// progress before closing it.
/// </summary>
public sealed record AuthenticationResult(string Token, IUpdateUiSession? UiSession);
