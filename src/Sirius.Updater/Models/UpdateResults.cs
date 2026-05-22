using System;

namespace Sirius.Updater;

/// <summary>
/// Outcome of <see cref="SiriusUpdater.CheckAsync"/>. A "Success" check
/// means the update source was reachable and gave us a parseable answer;
/// it does not imply that an update is available — that's
/// <see cref="UpdateAvailable"/>.
/// </summary>
public sealed record UpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    Version Current,
    Version? Latest,
    string? AssetName,
    string? Error,
    string? Message)
{
    /// <summary>Convenience factory for the "already up to date" path.</summary>
    public static UpdateCheckResult UpToDate(Version current) =>
        new(true, false, current, current, null, null,
            $"Already on the latest version ({current}).");
}

/// <summary>
/// Outcome of <see cref="SiriusUpdater.UpdateAsync"/>. When
/// <see cref="HandoffStarted"/> is true the calling process should
/// expect to be terminated by the installer within a few seconds and
/// relaunched in the new version.
/// </summary>
public sealed record UpdateInstallResult(
    bool Success,
    bool HandoffStarted,
    Version Current,
    Version? Target,
    string? Error,
    string? Message);
