using Sirius.Updater.Auth;
using Sirius.Updater.Diagnostics;
using Sirius.Updater.Hosting;
using Sirius.Updater.Installers;
using Sirius.Updater.Sources;
using Sirius.Updater.TokenStore;
using Sirius.Updater.Ui;

namespace Sirius.Updater;

/// <summary>
/// Optional advanced overrides. Each property is null by default — the
/// updater fills in a built-in implementation in that case. Set any of
/// these to swap in a custom strategy without subclassing.
/// </summary>
public sealed class SiriusUpdaterServices
{
    /// <summary>Source of "what's the latest version" — defaults to
    /// <c>GitHubReleaseSource</c>.</summary>
    public IUpdateSource? Source { get; init; }

    /// <summary>Token store chain — defaults to
    /// <c>LayeredTokenStore(InMemory, [Environment], [Dpapi])</c>.</summary>
    public ITokenStore? TokenStore { get; init; }

    /// <summary>Interactive auth — defaults to
    /// <c>GitHubDeviceFlowAuthenticator</c>.</summary>
    public IAuthenticator? Authenticator { get; init; }

    /// <summary>Installer for downloaded packages — defaults to
    /// <c>MsixAppxInstaller</c>.</summary>
    public IPackageInstaller? Installer { get; init; }

    /// <summary>Hosting context (current version, AUMID, restart) —
    /// defaults to <c>PackagedHostApplication</c>.</summary>
    public IHostApplication? Host { get; init; }

    /// <summary>
    /// Update UI — defaults to <see cref="NullUpdateUi"/>, a fail-fast
    /// sentinel that throws <see cref="InvalidOperationException"/> from
    /// its <c>PresentAsync</c> rather than silently hanging on a
    /// Device-Flow poll the user can't see. Set this to a real UI (the
    /// WinUI integration ships <c>ContentDialogUpdateUi</c>) before
    /// triggering an update that may require interactive auth. Set it
    /// explicitly to <c>null</c> to opt into silent headless polling
    /// (e.g. a console host that prints the user code itself).
    /// </summary>
    public IUpdateUi? Ui { get; init; }

    /// <summary>Diagnostic log sink — defaults to
    /// <c>NullUpdaterLog</c>.</summary>
    public IUpdaterLog? Log { get; init; }
}
