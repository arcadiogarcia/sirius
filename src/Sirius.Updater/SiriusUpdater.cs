using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Auth;
using Sirius.Updater.Diagnostics;
using Sirius.Updater.Hosting;
using Sirius.Updater.Installers;
using Sirius.Updater.Internal;
using Sirius.Updater.Sources;
using Sirius.Updater.TokenStore;
using Sirius.Updater.Ui;

namespace Sirius.Updater;

/// <summary>
/// Main entry point for Sirius Updater. Construct once at startup with
/// a configured <see cref="SiriusUpdaterOptions"/>, then call
/// <see cref="CheckAsync"/> / <see cref="UpdateAsync"/> from a UI
/// affordance (e.g. a "Check for updates" button).
///
/// Typical wiring:
/// <code>
/// var updater = new SiriusUpdater(new SiriusUpdaterOptions
/// {
///     Repository  = "myorg/myapp",
///     ProductName = "MyApp",
/// });
/// // (WinUI integration) wire the dialog to a XamlRoot:
/// var ui = ContentDialogUpdateUi.CreateAndAttach(rootElement);
/// updater = updater.WithUi(ui);
/// // ...
/// var check = await updater.CheckAsync();
/// if (check.UpdateAvailable) await updater.UpdateAsync();
/// </code>
/// </summary>
public sealed class SiriusUpdater : IDisposable
{
    readonly SiriusUpdaterOptions _options;
    readonly IUpdateSource     _source;
    readonly IAuthenticator    _authenticator;
    readonly IPackageInstaller _installer;
    readonly IHostApplication  _host;
    readonly ITokenStore       _tokenStore;
    readonly IUpdaterLog       _log;
    readonly IUpdateUi         _ui;
    readonly SemaphoreSlim     _gate = new(1, 1);
    readonly Lazy<HttpClient>  _http;

    IUpdateUiSession? _activeSession;

    /// <summary>Construct an updater with the given options.</summary>
    public SiriusUpdater(SiriusUpdaterOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        _log           = options.Services.Log           ?? NullUpdaterLog.Instance;
        _host          = options.Services.Host          ?? new PackagedHostApplication();
        _ui            = options.Services.Ui            ?? NullUpdateUi.Instance;
        _source        = options.Services.Source        ?? new GitHubReleaseSource(options.Repository);
        _authenticator = options.Services.Authenticator ?? new GitHubDeviceFlowAuthenticator(
            options.OAuthClientId, options.OAuthScopes, _log);
        _installer     = options.Services.Installer     ?? new MsixAppxInstaller(_log);
        _tokenStore    = options.Services.TokenStore    ?? BuildDefaultTokenStore(options, _log);

        _http = new Lazy<HttpClient>(() =>
        {
            var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            hc.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            return hc;
        });

        BuildDate = BuildInfo.ReadBuildDate(Assembly.GetEntryAssembly()!)
                    ?? BuildInfo.ReadBuildDate(typeof(SiriusUpdater).Assembly);
    }

    /// <summary>Currently-installed version of the host app.</summary>
    public Version CurrentVersion => _host.CurrentVersion;

    /// <summary>UTC build date stamped into the entry assembly, if any.</summary>
    public DateTimeOffset? BuildDate { get; }

    /// <summary>Channel id for diagnostic display.</summary>
    public string ChannelId => _source.ChannelId;

    /// <summary>The active token store. Exposed so sibling Sirius
    /// modules (e.g. <see cref="Sirius.Updater.Feedback.SiriusFeedback"/>)
    /// can share the same cache.</summary>
    public Sirius.Updater.TokenStore.ITokenStore TokenStore => _tokenStore;

    /// <summary>The active authenticator. Exposed so sibling Sirius
    /// modules can share the same Device-Flow client + scopes.</summary>
    public Sirius.Updater.Auth.IAuthenticator Authenticator => _authenticator;

    /// <summary>The active update UI. Exposed so sibling modules can
    /// route their own auth flow through the same dialog.</summary>
    public Sirius.Updater.Ui.IUpdateUi Ui => _ui;

    /// <summary>
    /// Returns a non-rendering UX-less updater clone with the supplied UI
    /// wired in. Useful for late binding when the UI isn't available at
    /// construction time (e.g. before the main window is loaded).
    /// </summary>
    public SiriusUpdater WithUi(IUpdateUi ui)
    {
        if (ui is null) throw new ArgumentNullException(nameof(ui));
        var clone = new SiriusUpdaterOptions
        {
            Repository                = _options.Repository,
            ProductName               = _options.ProductName,
            AssetNamePattern          = _options.AssetNamePattern,
            AssetBaseName             = _options.AssetBaseName,
            AssetSelector             = _options.AssetSelector,
            OAuthClientId             = _options.OAuthClientId,
            OAuthScopes               = _options.OAuthScopes,
            PersistToken              = _options.PersistToken,
            TokenMaxAge               = _options.TokenMaxAge,
            TokenStorageDirectory     = _options.TokenStorageDirectory,
            StagingDirectory          = _options.StagingDirectory,
            EnvironmentTokenVariables = _options.EnvironmentTokenVariables,
            IncludePreReleases        = _options.IncludePreReleases,
            UserAgent                 = _options.UserAgent,
            Services = new SiriusUpdaterServices
            {
                Source        = _source,
                TokenStore    = _tokenStore,
                Authenticator = _authenticator,
                Installer     = _installer,
                Host          = _host,
                Log           = _log,
                Ui            = ui,
            },
        };
        return new SiriusUpdater(clone);
    }

    /// <summary>
    /// Asks the configured source whether a newer release is available.
    /// Pure read — no disk side effects, no install. May trigger an
    /// interactive Device Flow if the source requires auth and no token
    /// is cached.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var current = CurrentVersion;
            try
            {
                var release = await GetReleaseWithAuthAsync(cancel).ConfigureAwait(false);
                if (release is null)
                {
                    await DismissActiveSessionAsync().ConfigureAwait(false);
                    return new UpdateCheckResult(false, false, current, null, null,
                        "EMPTY_RESPONSE", "Update source returned no data.");
                }

                var latest = VersionParsing.ParseTagOrNull(release.Version)
                             ?? VersionParsing.ParseTagOrNull(release.SourceTag);
                if (latest is null)
                {
                    await DismissActiveSessionAsync().ConfigureAwait(false);
                    return new UpdateCheckResult(false, false, current, null, null,
                        "INVALID_VERSION", $"Could not parse '{release.Version}' as a version.");
                }

                if (latest <= current)
                {
                    await DismissActiveSessionAsync().ConfigureAwait(false);
                    return UpdateCheckResult.UpToDate(current);
                }

                var asset = SelectAsset(release);
                if (asset is null)
                {
                    await DismissActiveSessionAsync().ConfigureAwait(false);
                    return new UpdateCheckResult(true, true, current, latest, null,
                        "ASSET_NOT_FOUND",
                        $"Release {release.Version} has no matching MSIX asset for {_host.Architecture}.");
                }

                // Keep any auth dialog alive — the user is likely about to
                // call UpdateAsync, and reusing the session keeps progress
                // continuous instead of flashing the dialog away.
                ReportSession("Update available",
                    $"Preparing to download {_options.ProductName} v{latest}…", null);

                return new UpdateCheckResult(true, true, current, latest, asset.Name,
                    Error: null,
                    Message: $"Update {current} → {latest} available.");
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                return new UpdateCheckResult(false, false, current, null, null,
                    "AUTH_CANCELLED",
                    "Sign-in was cancelled. Try again when you're ready.");
            }
            catch (Exception ex)
            {
                await DismissActiveSessionAsync().ConfigureAwait(false);
                _log.Error("check", $"Check failed: {ex.Message}", ex);
                return new UpdateCheckResult(false, false, current, null, null,
                    "CHECK_FAILED", ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads the latest release asset and hands off to the installer.
    /// On success returns a result with <see cref="UpdateInstallResult.HandoffStarted"/>
    /// true; the calling process should expect to be terminated by the
    /// installer within a few seconds.
    /// </summary>
    /// <param name="progress">Optional progress sink. The same updates
    /// flow into the active UI session.</param>
    public async Task<UpdateInstallResult> UpdateAsync(
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var current = CurrentVersion;
            try
            {
                Report(progress, UpdateStage.Checking, "Checking for updates…", null, null);

                var release = await GetReleaseWithAuthAsync(cancel).ConfigureAwait(false);
                if (release is null)
                {
                    await DismissActiveSessionAsync().ConfigureAwait(false);
                    return new UpdateInstallResult(false, false, current, null,
                        "EMPTY_RESPONSE", "Update source returned no data.");
                }

                var latest = VersionParsing.ParseTagOrNull(release.Version);
                if (latest is null)
                {
                    await DismissActiveSessionAsync().ConfigureAwait(false);
                    return new UpdateInstallResult(false, false, current, null,
                        "INVALID_VERSION", $"Could not parse '{release.Version}' as a version.");
                }
                if (latest <= current)
                {
                    await DismissActiveSessionAsync().ConfigureAwait(false);
                    return new UpdateInstallResult(true, false, current, latest, null,
                        $"Already on the latest version ({current}).");
                }

                var asset = SelectAsset(release);
                if (asset is null)
                {
                    await DismissActiveSessionAsync().ConfigureAwait(false);
                    return new UpdateInstallResult(false, false, current, latest,
                        "ASSET_NOT_FOUND",
                        $"Release {release.Version} has no matching MSIX asset for {_host.Architecture}.");
                }

                var dest = await DownloadAsync(release, asset, latest, progress, cancel).ConfigureAwait(false);

                Report(progress, UpdateStage.Preparing,
                    $"Preparing installer for {_options.ProductName} v{latest}…", null, 100);
                TransitionSession($"Installing {_options.ProductName} v{latest}",
                    $"{_options.ProductName} will close and relaunch automatically…");

                var handoff = await _installer.LaunchInstallAsync(dest, _host, cancel).ConfigureAwait(false);
                if (!handoff.HandoffStarted)
                {
                    await DismissActiveSessionAsync().ConfigureAwait(false);
                    return new UpdateInstallResult(false, false, current, latest,
                        handoff.Error ?? "HANDOFF_FAILED", handoff.Message);
                }

                Report(progress, UpdateStage.Installing,
                    $"Installing {_options.ProductName} v{latest}…",
                    $"{_options.ProductName} will close and relaunch automatically.", null);

                return new UpdateInstallResult(true, true, current, latest, null,
                    $"Update from {current} to {latest} in progress.");
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                return new UpdateInstallResult(false, false, current, null,
                    "AUTH_CANCELLED",
                    "Sign-in was cancelled. Try again when you're ready.");
            }
            catch (Exception ex)
            {
                await DismissActiveSessionAsync().ConfigureAwait(false);
                _log.Error("install", $"Install failed: {ex.Message}", ex);
                return new UpdateInstallResult(false, false, current, null,
                    "INSTALL_FAILED", ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── internals ────────────────────────────────────────────────────────

    UpdateAsset? SelectAsset(UpdateRelease release)
    {
        var baseName = string.IsNullOrEmpty(_options.AssetBaseName)
            ? _options.ProductName.Replace(" ", string.Empty)
            : _options.AssetBaseName!;
        var ctx = new UpdateAssetSelectionContext(
            Release:       release,
            Architecture:  _host.Architecture,
            ProductName:   _options.ProductName,
            AssetBaseName: baseName);
        return _options.AssetSelector is { } custom
            ? custom(ctx)
            : DefaultAssetSelector.Select(ctx, _options.AssetNamePattern);
    }

    async Task<UpdateRelease?> GetReleaseWithAuthAsync(CancellationToken cancel)
    {
        var token = await _tokenStore.LoadAsync(cancel).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _source.GetLatestAsync(_http.Value, token, _options.IncludePreReleases, cancel)
                    .ConfigureAwait(false);
            }
            catch (SourceAuthRequiredException ex) when (attempt == 0)
            {
                _log.Info("auth", $"Auth required for {_source.ChannelId} — running authenticator");
                // Drop the stale token so we don't replay it forever.
                await _tokenStore.ClearAsync(cancel).ConfigureAwait(false);
                var result = await _authenticator.AuthenticateAsync(
                    _http.Value, _ui, _source.SuggestedLogin, cancel).ConfigureAwait(false);
                if (result is null)
                {
                    throw new InvalidOperationException(
                        $"Authentication required for '{_options.Repository}' and no UI is available. " +
                        $"Set one of {string.Join(", ", _options.EnvironmentTokenVariables)} or attach an IUpdateUi.", ex);
                }
                token = result.Token;
                await _tokenStore.SaveAsync(token, cancel).ConfigureAwait(false);
                _activeSession = result.UiSession;
                _activeSession?.TransitionToProgress(
                    "Signed in",
                    $"Preparing {_options.ProductName} update…");
            }
            catch (SourceAuthRequiredException)
            {
                // Second failure with a fresh token: bail.
                await _tokenStore.ClearAsync(cancel).ConfigureAwait(false);
                throw;
            }
        }
    }

    async Task<string> DownloadAsync(
        UpdateRelease release, UpdateAsset asset, Version target,
        IProgress<UpdateProgress>? progress, CancellationToken cancel)
    {
        var stagingDir = _options.StagingDirectory
            ?? Path.Combine(Path.GetTempPath(), "sirius-updater",
                            _options.ProductName.Replace(" ", string.Empty));
        Directory.CreateDirectory(stagingDir);

        // Strip any directory components from the asset name — a
        // malicious / malformed name shouldn't escape the staging dir
        // via Path.Combine normalising a relative path.
        var safeName = Path.GetFileName(asset.Name);
        if (string.IsNullOrEmpty(safeName) ||
            (!safeName.EndsWith(".msix",       StringComparison.OrdinalIgnoreCase) &&
             !safeName.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Asset name '{asset.Name}' is not a valid MSIX file name.");
        }
        var dest = Path.Combine(stagingDir, safeName);

        var token = await _tokenStore.LoadAsync(cancel).ConfigureAwait(false);
        var uri   = _source.ResolveAssetDownloadUri(asset);

        ReportSession($"Downloading {_options.ProductName} v{target}",
            "Fetching the installer…", 0);
        Report(progress, UpdateStage.Downloading,
            $"Downloading {_options.ProductName} v{target}",
            "Fetching the installer…", 0);

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/octet-stream");
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var resp = await _http.Value.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel)
            .ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 403 or 404)
        {
            await _tokenStore.ClearAsync(cancel).ConfigureAwait(false);
            throw new SourceAuthRequiredException(
                $"Download denied for '{asset.Name}' (status {(int)resp.StatusCode}).");
        }
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength;
        await using var net = await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var fs  = File.Create(dest);
        await CopyWithProgressAsync(net, fs, totalBytes, target, progress, cancel).ConfigureAwait(false);

        ReportSession($"Downloaded {_options.ProductName} v{target}", "Preparing installer…", 100);
        return dest;
    }

    async Task CopyWithProgressAsync(
        Stream source, Stream dest, long? totalBytes, Version target,
        IProgress<UpdateProgress>? progress, CancellationToken cancel)
    {
        const int bufferSize = 81920;
        var buffer = new byte[bufferSize];
        long copied = 0;
        const long uiUpdateBytes = 64L * 1024;
        var lastUiBytes = -uiUpdateBytes;
        var lastUiTime  = DateTimeOffset.UtcNow;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, bufferSize), cancel).ConfigureAwait(false);
            if (read <= 0) break;
            await dest.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            copied += read;

            var now = DateTimeOffset.UtcNow;
            if (copied - lastUiBytes < uiUpdateBytes &&
                (now - lastUiTime).TotalMilliseconds < 150) continue;
            lastUiBytes = copied;
            lastUiTime  = now;

            string title = $"Downloading {_options.ProductName} v{target}";
            if (totalBytes is { } total && total > 0)
            {
                var pct    = (double)copied * 100.0 / total;
                var detail = $"{VersionParsing.FormatBytes(copied)} of {VersionParsing.FormatBytes(total)} ({pct:0.#}%)";
                ReportSession(title, detail, pct);
                Report(progress, UpdateStage.Downloading, title, detail, pct, copied, total);
            }
            else
            {
                var detail = $"{VersionParsing.FormatBytes(copied)} downloaded…";
                ReportSession(title, detail, null);
                Report(progress, UpdateStage.Downloading, title, detail, null, copied, null);
            }
        }
    }

    void ReportSession(string? title, string? subtitle, double? percent)
    {
        try { _activeSession?.ReportProgress(title, subtitle, percent); }
        catch { /* UI updates must never escape */ }
    }

    void TransitionSession(string title, string? subtitle)
    {
        try { _activeSession?.TransitionToProgress(title, subtitle); }
        catch { /* UI updates must never escape */ }
    }

    async Task DismissActiveSessionAsync()
    {
        var s = _activeSession;
        _activeSession = null;
        if (s is null) return;
        try { await s.CloseAsync().ConfigureAwait(false); }
        catch { /* dispose races are fine */ }
    }

    static void Report(IProgress<UpdateProgress>? progress, UpdateStage stage,
                       string title, string? detail, double? percent,
                       long? bytes = null, long? total = null)
    {
        try { progress?.Report(new UpdateProgress(stage, title, detail, percent, bytes, total)); }
        catch { /* progress sinks must not break the pipeline */ }
    }

    static ITokenStore BuildDefaultTokenStore(SiriusUpdaterOptions options, IUpdaterLog log)
    {
        var layers = new System.Collections.Generic.List<ITokenStore>
        {
            new InMemoryTokenStore(),
        };
        if (options.EnvironmentTokenVariables.Count > 0)
        {
            layers.Add(new EnvironmentTokenStore(options.EnvironmentTokenVariables));
        }
        if (options.PersistToken)
        {
            var dir = options.TokenStorageDirectory
                      ?? ResolveDefaultTokenDir(options.ProductName);
            // Entropy mixes product name so two Sirius-using apps don't
            // read each other's tokens even if both run as the same user.
            var entropy = $"SiriusUpdater/{options.ProductName}/v1";
            layers.Add(new DpapiTokenStore(
                directory: dir,
                entropy:   entropy,
                maxAge:    options.TokenMaxAge,
                log:       log));
        }
        return new LayeredTokenStore(layers, log);
    }

    static string ResolveDefaultTokenDir(string productName)
    {
        try
        {
            var local = global::Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            if (!string.IsNullOrEmpty(local))
                return Path.Combine(local, "sirius-updater");
        }
        catch { /* unpackaged */ }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            productName.Replace(" ", string.Empty),
            "sirius-updater");
    }

    public void Dispose()
    {
        try { if (_http.IsValueCreated) _http.Value.Dispose(); } catch { }
        try { _gate.Dispose(); } catch { }
    }
}
