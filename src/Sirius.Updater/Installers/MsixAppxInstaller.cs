using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Diagnostics;
using Sirius.Updater.Hosting;

namespace Sirius.Updater.Installers;

/// <summary>
/// Default installer for signed MSIX / MSIXBUNDLE packages on Windows.
///
/// Strategy: write a tiny PowerShell helper to a temp folder, launch it
/// detached, return immediately, and let the helper:
/// <list type="number">
///   <item><c>Start-Sleep 3</c> — gives the host UI a chance to paint
///         "Updating…" and the current process a moment to clean up.</item>
///   <item><c>Add-AppxPackage -ForceApplicationShutdown
///         -ForceUpdateFromAnyVersion</c> — silent install (no UAC, no
///         installer UI) provided the publisher cert is already trusted
///         on the machine; tears down the running host as a side
///         effect.</item>
///   <item><c>Start explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c> —
///         relaunches the new version under its packaged identity.</item>
///   <item><c>Remove-Item</c> — cleans up the staged MSIX.</item>
/// </list>
/// Any helper failure is captured to
/// <c>%TEMP%\sirius-updater.error.log</c> for post-mortem.
///
/// "Silent" depends on the publisher cert being in
/// <c>LocalMachine\TrustedPeople</c>; the first install of the host
/// (per the consumer's docs) establishes that. If trust is missing,
/// <c>Add-AppxPackage</c> throws and the helper's error log explains.
/// </summary>
public sealed class MsixAppxInstaller : IPackageInstaller
{
    readonly IUpdaterLog _log;
    readonly TimeSpan _fallbackExit;

    public MsixAppxInstaller(IUpdaterLog? log = null, TimeSpan? fallbackExitDelay = null)
    {
        _log          = log ?? NullUpdaterLog.Instance;
        _fallbackExit = fallbackExitDelay ?? TimeSpan.FromSeconds(5);
    }

    public Task<InstallHandoffResult> LaunchInstallAsync(
        string packagePath, IHostApplication host, CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
        {
            return Task.FromResult(new InstallHandoffResult(
                false, "PACKAGE_MISSING",
                $"Downloaded package '{packagePath}' is missing."));
        }
        if (!host.IsPackaged)
        {
            return Task.FromResult(new InstallHandoffResult(
                false, "NOT_PACKAGED",
                "Self-install requires the host to be running as a packaged MSIX app."));
        }

        var dir = Path.GetDirectoryName(packagePath) ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);

        var escapedMsix = packagePath.Replace("'", "''");
        var aumid       = host.Aumid ?? string.Empty;
        var relaunch    = string.IsNullOrEmpty(aumid)
            ? string.Empty
            : $"Start-Process 'explorer.exe' 'shell:AppsFolder\\{aumid}'";

        var script =
$@"$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
Start-Sleep -Seconds 3
try {{
    Add-AppxPackage -Path '{escapedMsix}' -ForceApplicationShutdown -ForceUpdateFromAnyVersion
}} catch {{
    $err = $_ | Out-String
    [System.IO.File]::WriteAllText((Join-Path $env:TEMP 'sirius-updater.error.log'), $err)
    throw
}}
{relaunch}
Remove-Item '{escapedMsix}' -ErrorAction SilentlyContinue
";
        var helperPath = Path.Combine(dir, "sirius-install.ps1");
        File.WriteAllText(helperPath, script);
        _log.Info("installer", $"Helper script written → {helperPath}");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helperPath}\"",
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            _log.Error("installer", $"Helper launch failed: {ex.Message}", ex);
            return Task.FromResult(new InstallHandoffResult(
                false, "HELPER_LAUNCH_FAILED", ex.Message));
        }

        // Belt-and-braces: Add-AppxPackage -ForceApplicationShutdown
        // *should* kill us, but AV scans can delay it. Schedule an
        // explicit exit so the helper's relaunch step doesn't race a
        // still-running instance.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_fallbackExit, CancellationToken.None).ConfigureAwait(false);
                Environment.Exit(0);
            }
            catch { /* best effort */ }
        });

        return Task.FromResult(new InstallHandoffResult(true, null,
            "Install handoff started; host will close and relaunch."));
    }
}
