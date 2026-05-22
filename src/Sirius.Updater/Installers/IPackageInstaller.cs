using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Hosting;

namespace Sirius.Updater.Installers;

/// <summary>
/// Result of <see cref="IPackageInstaller.LaunchInstallAsync"/>.
/// </summary>
public sealed record InstallHandoffResult(bool HandoffStarted, string? Error, string? Message);

/// <summary>
/// Installs a downloaded package and (optionally) relaunches the host.
/// The default <see cref="MsixAppxInstaller"/> uses <c>Add-AppxPackage</c>
/// + <c>shell:AppsFolder</c>. Custom installers can plug in for
/// alternative deployment shapes (e.g. AppInstaller streams).
/// </summary>
public interface IPackageInstaller
{
    /// <summary>
    /// Spawns the install helper in a detached process and returns
    /// immediately. The running host should expect to be terminated by
    /// the installer shortly.
    /// </summary>
    /// <param name="packagePath">Local path to the downloaded payload.</param>
    /// <param name="host">Hosting context — used for AUMID-based relaunch.</param>
    Task<InstallHandoffResult> LaunchInstallAsync(
        string packagePath,
        IHostApplication host,
        CancellationToken cancel = default);
}
