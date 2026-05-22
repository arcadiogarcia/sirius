using System;
using System.Collections.Generic;

namespace Sirius.Updater;

/// <summary>
/// Strongly-typed configuration for <see cref="SiriusUpdater"/>. All
/// behavioural overrides go through here so a single constructor call is
/// enough to wire up the updater for both common and advanced scenarios.
///
/// The only required field is <see cref="Repository"/>. Everything else
/// has a sensible default chosen for the most common case: a sideloaded
/// MSIX-packaged WinUI 3 application that ships releases as per-arch
/// MSIX assets on GitHub Releases.
///
/// Options instances are intended to be constructed once at startup and
/// treated as immutable thereafter. Mutating after passing to the
/// updater yields undefined behaviour.
/// </summary>
public sealed class SiriusUpdaterOptions
{
    /// <summary>
    /// GitHub "owner/repo" path that hosts the releases. Required.
    /// Forks should set this to their own fork so the updater self-updates
    /// from the same line of source the user is running.
    /// </summary>
    public required string Repository { get; init; }

    /// <summary>
    /// Human-readable product name. Used purely for UI strings — e.g.
    /// "Downloading MyApp v1.2.3" in the progress dialog and toast
    /// messages. Defaults to <c>"Application"</c> if not set.
    /// </summary>
    public string ProductName { get; init; } = "Application";

    /// <summary>
    /// Template that resolves to the GitHub release asset name the updater
    /// should prefer. Supported placeholders:
    /// <list type="bullet">
    ///   <item><c>{name}</c> — <see cref="AssetBaseName"/> (defaults to
    ///         <see cref="ProductName"/> with spaces stripped).</item>
    ///   <item><c>{version}</c> — release version, no leading <c>v</c>.</item>
    ///   <item><c>{arch}</c> — <c>x64</c> / <c>ARM64</c> / <c>x86</c>
    ///         matching the current process architecture.</item>
    /// </list>
    /// If no asset matches the rendered name, the updater falls back to
    /// the first <c>*.msix</c> / <c>*.msixbundle</c> attached to the
    /// release. Set <see cref="AssetSelector"/> for fully custom logic.
    /// </summary>
    public string AssetNamePattern { get; init; } = "{name}_{version}_{arch}.msix";

    /// <summary>
    /// Override the <c>{name}</c> token in <see cref="AssetNamePattern"/>.
    /// Defaults to <see cref="ProductName"/> with whitespace removed,
    /// which matches the convention <c>MyApp_1.2.3_x64.msix</c>.
    /// </summary>
    public string? AssetBaseName { get; init; }

    /// <summary>
    /// Fully custom asset selection. Receives the parsed release and
    /// returns the asset to download (or null if none is suitable). When
    /// set, <see cref="AssetNamePattern"/> is ignored.
    /// </summary>
    public Func<UpdateAssetSelectionContext, UpdateAsset?>? AssetSelector { get; init; }

    /// <summary>
    /// OAuth client id used for the GitHub Device Flow. Defaults to the
    /// public GitHub CLI app id so the consent screen says "GitHub CLI"
    /// — no need to register your own OAuth application for private-repo
    /// updates to work. Override to point at your own OAuth app for
    /// branded consent UX.
    /// </summary>
    public string OAuthClientId { get; init; } = SiriusUpdaterDefaults.GitHubCliOAuthClientId;

    /// <summary>
    /// Scope string requested during the Device Flow. <c>repo</c> is the
    /// minimum that lets a user with private-repo access download release
    /// assets. For public-only repos any token (or none) works; the
    /// updater first attempts unauthenticated calls and only escalates
    /// to interactive auth on a 401/403/404.
    /// </summary>
    public string OAuthScopes { get; init; } = "repo";

    /// <summary>
    /// Whether to persist the GitHub Device-Flow token across launches so
    /// users who update repeatedly don't have to walk the device-code
    /// dance every time. Persisted via Windows DPAPI under the current
    /// user. Defaults to true; turn off for ephemeral environments.
    /// </summary>
    public bool PersistToken { get; init; } = true;

    /// <summary>
    /// Maximum age the persistent token cache will honour before forcing
    /// a fresh sign-in. GitHub Device-Flow tokens issued via the gh-CLI
    /// public app don't carry server-side expiry, so this is a
    /// defense-in-depth cap. Defaults to 30 days.
    /// </summary>
    public TimeSpan TokenMaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Directory where the encrypted token cache lives. Defaults to the
    /// MSIX package's <c>LocalState</c> folder when running packaged, or
    /// <c>%LocalAppData%\&lt;ProductName&gt;\sirius-updater</c> when not.
    /// </summary>
    public string? TokenStorageDirectory { get; init; }

    /// <summary>
    /// Directory used for staging downloaded MSIX payloads. Defaults to
    /// <c>%TEMP%\sirius-updater\&lt;ProductName&gt;</c>. The directory is
    /// created on demand and pruned by the install helper after a
    /// successful install.
    /// </summary>
    public string? StagingDirectory { get; init; }

    /// <summary>
    /// Environment variables (in order of preference) that the updater
    /// will read as fallback API tokens before falling through to
    /// interactive Device Flow. Empty disables environment auth entirely.
    /// </summary>
    public IReadOnlyList<string> EnvironmentTokenVariables { get; init; } = new[]
    {
        "GH_TOKEN",
        "GITHUB_TOKEN",
    };

    /// <summary>
    /// Whether to include releases marked as pre-release / draft when
    /// determining "latest". Off by default — most consumers want stable
    /// channel behaviour.
    /// </summary>
    public bool IncludePreReleases { get; init; }

    /// <summary>
    /// User-Agent string sent to the GitHub API. GitHub requires every
    /// caller to identify itself; defaults to
    /// <c>SiriusUpdater/&lt;version&gt;</c>.
    /// </summary>
    public string UserAgent { get; init; } = SiriusUpdaterDefaults.DefaultUserAgent;

    /// <summary>
    /// Optional advanced overrides. Leave null to use the built-in
    /// implementations; supply your own to swap in custom sources,
    /// installers, token stores, UIs, or host bindings (handy for tests
    /// and forked release backends).
    /// </summary>
    public SiriusUpdaterServices Services { get; init; } = new();

    /// <summary>
    /// Validates the options after construction. Throws
    /// <see cref="ArgumentException"/> for missing/invalid fields. Called
    /// by <see cref="SiriusUpdater"/>'s constructor — callers don't
    /// normally need to invoke this directly.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Repository) || !Repository.Contains('/'))
        {
            throw new ArgumentException(
                $"SiriusUpdaterOptions.Repository must be in 'owner/repo' format (was '{Repository}').",
                nameof(Repository));
        }
        if (string.IsNullOrWhiteSpace(AssetNamePattern))
        {
            throw new ArgumentException(
                "SiriusUpdaterOptions.AssetNamePattern must be a non-empty pattern.",
                nameof(AssetNamePattern));
        }
        if (TokenMaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "SiriusUpdaterOptions.TokenMaxAge must be positive.",
                nameof(TokenMaxAge));
        }
    }
}
