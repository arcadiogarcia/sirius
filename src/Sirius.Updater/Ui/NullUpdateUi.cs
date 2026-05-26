using System;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Auth;

namespace Sirius.Updater.Ui;

/// <summary>
/// Default sentinel <see cref="IUpdateUi"/> used when a consumer
/// constructs <see cref="SiriusUpdater"/> without wiring a real UI.
/// It does not render anything. Its <see cref="PresentAsync"/> throws
/// <see cref="InvalidOperationException"/> on the assumption that
/// reaching this point is a configuration bug — Device-Flow auth needs
/// a way to show the user code, and a silent hang is the worst possible
/// failure mode for a self-updater.
///
/// <para>
/// Reaching <see cref="PresentAsync"/> requires <em>all</em> of these to
/// be true:
/// </para>
/// <list type="bullet">
///   <item>The configured source returned a <see cref="SourceAuthRequiredException"/>
///         (private repo / 401 / 403 / 404).</item>
///   <item>No cached token was available — neither the
///         <see cref="TokenStore.DpapiTokenStore"/> nor the
///         <see cref="TokenStore.EnvironmentTokenStore"/> produced one.</item>
///   <item>The consumer never called
///         <c>updater.WithWinUI(anchor)</c> (or otherwise supplied a
///         non-null <see cref="IUpdateUi"/>).</item>
/// </list>
/// <para>
/// All other paths — public repos, cached tokens, env-var tokens, CI —
/// finish before <see cref="PresentAsync"/> is ever invoked, so the
/// throw never fires for legitimate scenarios. Consumers who genuinely
/// want a silent Device-Flow poll (e.g. a console app that prints the
/// user code itself) should set
/// <see cref="SiriusUpdaterServices.Ui"/> to <c>null</c> explicitly;
/// the authenticator falls back to silent polling when
/// <see cref="IUpdateUi"/> is null, which is a different (and
/// intentional) path from <see cref="NullUpdateUi"/>.
/// </para>
/// </summary>
public sealed class NullUpdateUi : IUpdateUi
{
    public static readonly NullUpdateUi Instance = new();

    public Task<IUpdateUiSession> PresentAsync(DeviceCodeInfo info, CancellationToken cancel)
    {
        throw new InvalidOperationException(
            "Sirius needs to show a GitHub Device-Flow sign-in code, but no " +
            "IUpdateUi is configured on this SiriusUpdater (the default " +
            "NullUpdateUi cannot render). To fix this:\n" +
            "  • WinUI apps: call updater.WithWinUI(anchor) once you have a " +
            "FrameworkElement in the visual tree.\n" +
            "  • Other apps: implement IUpdateUi and pass it via " +
            "SiriusUpdaterOptions.Services.Ui (or updater.WithUi(...)).\n" +
            "  • Headless flows: supply a token via one of " +
            "SiriusUpdaterOptions.EnvironmentTokenVariables (defaults: " +
            "GH_TOKEN, GITHUB_TOKEN), or set Services.Ui = null explicitly " +
            "to opt into silent Device-Flow polling.");
    }
}
