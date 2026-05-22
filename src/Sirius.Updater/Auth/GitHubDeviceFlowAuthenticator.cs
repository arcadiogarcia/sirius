using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Diagnostics;
using Sirius.Updater.Ui;

namespace Sirius.Updater.Auth;

/// <summary>
/// Default authenticator: walks the user through GitHub's OAuth Device
/// Flow against a configured public OAuth app (defaults to GitHub CLI's
/// so the consent screen is the familiar gh-CLI one). On success, the
/// returned <see cref="AuthenticationResult"/> includes the live
/// <see cref="IUpdateUiSession"/> so the caller can morph it from
/// "Waiting for authorization…" straight into "Downloading…" without
/// dismissing the dialog.
/// </summary>
public sealed class GitHubDeviceFlowAuthenticator : IAuthenticator
{
    readonly string _clientId;
    readonly string _scopes;
    readonly IUpdaterLog _log;

    public GitHubDeviceFlowAuthenticator(string clientId, string scopes, IUpdaterLog? log = null)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client id required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(scopes))   throw new ArgumentException("Scopes required.", nameof(scopes));
        _clientId = clientId;
        _scopes   = scopes;
        _log      = log ?? NullUpdaterLog.Instance;
    }

    public async Task<AuthenticationResult?> AuthenticateAsync(
        HttpClient http, IUpdateUi? ui, string? suggestedLogin, CancellationToken cancel)
    {
        var dc = await GitHubDeviceFlowClient.StartAsync(http, _clientId, _scopes, cancel).ConfigureAwait(false);
        _log.Info("device-flow", $"Started GitHub Device Flow (verification_uri={dc.VerificationUri}, expires_in={dc.ExpiresIn})");

        var hintedUri = BuildHintedUri(dc.VerificationUri!, dc.UserCode!, suggestedLogin);
        var info = new DeviceCodeInfo(
            UserCode:                dc.UserCode!,
            VerificationUri:         dc.VerificationUri!,
            ExpiresIn:               TimeSpan.FromSeconds(dc.ExpiresIn),
            VerificationUriComplete: hintedUri,
            SuggestedLogin:          suggestedLogin);

        // Headless path: no UI — poll silently. Used by CI and tests.
        if (ui is null)
        {
            var t = await GitHubDeviceFlowClient.PollAsync(http, _clientId, dc, cancel).ConfigureAwait(false);
            return new AuthenticationResult(t, UiSession: null);
        }

        IUpdateUiSession? session = null;
        try
        {
            session = await ui.PresentAsync(info, cancel).ConfigureAwait(false);

            // Race the poll against the user dismissing the dialog.
            using var pollHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            foreach (var h in http.DefaultRequestHeaders)
                pollHttp.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);

            var poll        = GitHubDeviceFlowClient.PollAsync(pollHttp, _clientId, dc, cancel);
            var dismissTask = session.UserDismissed;
            var finished    = await Task.WhenAny(poll, dismissTask).ConfigureAwait(false);
            if (finished == dismissTask)
            {
                await dismissTask.ConfigureAwait(false); // throws OCE
                throw new OperationCanceledException("GitHub sign-in dismissed.", cancel);
            }
            var token = await poll.ConfigureAwait(false);

            // Don't close the session — caller will drive it through the
            // download + install progress views.
            return new AuthenticationResult(token, session);
        }
        catch
        {
            if (session is not null)
            {
                try { await session.CloseAsync().ConfigureAwait(false); } catch { /* dispose race */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Synthesize a GitHub verification_uri_complete that both pre-fills
    /// the user code AND nudges the sign-in page toward a specific
    /// account (the suggested login). GitHub honours both
    /// <c>?login=</c> on the sign-in page and <c>user_code=</c> on the
    /// device page. Falls back to the bare verification URI on any
    /// parse error.
    /// </summary>
    static string? BuildHintedUri(string verificationUri, string userCode, string? suggestedLogin)
    {
        if (string.IsNullOrWhiteSpace(verificationUri) || string.IsNullOrWhiteSpace(userCode)) return null;
        try
        {
            var devicePath = new Uri(verificationUri).AbsolutePath;
            var returnTo   = $"{devicePath}?user_code={Uri.EscapeDataString(userCode)}";
            if (string.IsNullOrWhiteSpace(suggestedLogin))
            {
                return $"{new Uri(verificationUri).GetLeftPart(UriPartial.Authority)}/login?return_to=" +
                       Uri.EscapeDataString(returnTo);
            }
            return $"{new Uri(verificationUri).GetLeftPart(UriPartial.Authority)}/login?login=" +
                   Uri.EscapeDataString(suggestedLogin) +
                   "&return_to=" + Uri.EscapeDataString(returnTo);
        }
        catch
        {
            return null;
        }
    }
}
