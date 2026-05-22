using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.Auth;

/// <summary>
/// Low-level GitHub OAuth Device Flow client. Pure HTTP — no UI, no
/// caching. Used by <see cref="GitHubDeviceFlowAuthenticator"/>.
///
/// Phase 1 — <see cref="StartAsync"/> posts to
/// <c>https://github.com/login/device/code</c> to obtain a user code
/// + device code + verification URL.
///
/// Phase 2 — <see cref="PollAsync"/> repeatedly posts to
/// <c>https://github.com/login/oauth/access_token</c> at the
/// server-specified interval until the user approves, denies, or the
/// code expires.
/// </summary>
internal static class GitHubDeviceFlowClient
{
    static readonly Uri DeviceCodeUri  = new("https://github.com/login/device/code");
    static readonly Uri AccessTokenUri = new("https://github.com/login/oauth/access_token");

    public static async Task<DeviceCodeResponse> StartAsync(
        HttpClient http, string clientId, string scopes, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUri)
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("scope",     scopes),
            }),
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await http.SendAsync(req, cancel).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var dc = await resp.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: cancel).ConfigureAwait(false)
                 ?? throw new InvalidOperationException("GitHub returned an empty device-code response.");
        if (string.IsNullOrEmpty(dc.UserCode) || string.IsNullOrEmpty(dc.DeviceCode) || string.IsNullOrEmpty(dc.VerificationUri))
            throw new InvalidOperationException("GitHub device-code response was missing required fields.");
        return dc;
    }

    public static async Task<string> PollAsync(
        HttpClient http, string clientId, DeviceCodeResponse dc, CancellationToken cancel)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(dc.Interval, 5));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(dc.ExpiresIn, 60));

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancel).ConfigureAwait(false);

            TokenResponse? tr;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, AccessTokenUri)
                {
                    Content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("client_id",  clientId),
                        new KeyValuePair<string, string>("device_code", dc.DeviceCode!),
                        new KeyValuePair<string, string>("grant_type",  "urn:ietf:params:oauth:grant-type:device_code"),
                    }),
                };
                req.Headers.Accept.Clear();
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var resp = await http.SendAsync(req, cancel).ConfigureAwait(false);
                // GitHub returns 200 even for pending-state errors; 5xx
                // we just retry on the next interval.
                if (!resp.IsSuccessStatusCode) continue;
                tr = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancel).ConfigureAwait(false);
            }
            catch (HttpRequestException) { continue; }
            catch (TaskCanceledException) when (!cancel.IsCancellationRequested) { continue; }

            if (tr is null) continue;
            if (!string.IsNullOrEmpty(tr.AccessToken)) return tr.AccessToken!;

            switch (tr.Error)
            {
                case "authorization_pending": continue;
                case "slow_down":             interval += TimeSpan.FromSeconds(5); continue;
                case "expired_token":
                    throw new InvalidOperationException(
                        "GitHub one-time code expired before authorization. Try again.");
                case "access_denied":
                    throw new InvalidOperationException(
                        "Authorization denied in GitHub.");
                default:
                    if (!string.IsNullOrEmpty(tr.Error))
                        throw new InvalidOperationException($"GitHub OAuth error: {tr.Error} — {tr.ErrorDescription}");
                    continue;
            }
        }

        throw new TimeoutException(
            "GitHub one-time code expired before authorization completed.");
    }

    internal sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")]      public string? DeviceCode      { get; set; }
        [JsonPropertyName("user_code")]        public string? UserCode        { get; set; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("expires_in")]       public int     ExpiresIn       { get; set; }
        [JsonPropertyName("interval")]         public int     Interval        { get; set; }
    }

    internal sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]      public string? AccessToken      { get; set; }
        [JsonPropertyName("token_type")]        public string? TokenType        { get; set; }
        [JsonPropertyName("scope")]             public string? Scope            { get; set; }
        [JsonPropertyName("error")]             public string? Error            { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}
