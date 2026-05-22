using System;
using System.Runtime.InteropServices;

namespace Sirius.Updater.Hosting;

/// <summary>
/// Hosting context that decouples the updater from <c>Package.Current</c>
/// — testable, and lets unpackaged consumers (rare for self-update, but
/// useful in tools) supply their own version/AUMID.
/// </summary>
public interface IHostApplication
{
    /// <summary>Currently-installed version of the host app.</summary>
    Version CurrentVersion { get; }

    /// <summary>
    /// Application User Model ID used to relaunch the new build after
    /// install (<c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c>).
    /// Null when running unpackaged; the installer falls back to a
    /// no-relaunch flow in that case.
    /// </summary>
    string? Aumid { get; }

    /// <summary>True when the process is running inside an MSIX
    /// package. Self-update is only meaningful in this case.</summary>
    bool IsPackaged { get; }

    /// <summary>Current process CPU architecture, used to pick the
    /// matching MSIX asset.</summary>
    string Architecture { get; }
}

/// <summary>
/// Default <see cref="IHostApplication"/> backed by Windows
/// <c>Package.Current</c>. Falls back gracefully when the process is
/// unpackaged so the constructor doesn't throw at startup.
/// </summary>
public sealed class PackagedHostApplication : IHostApplication
{
    readonly Version _version;
    readonly string? _aumid;

    public PackagedHostApplication()
    {
        try
        {
            var pkg = global::Windows.ApplicationModel.Package.Current;
            var v   = pkg.Id.Version;
            _version    = new Version(v.Major, v.Minor, v.Build, v.Revision);
            _aumid      = $"{pkg.Id.FamilyName}!App";
            IsPackaged  = true;
        }
        catch
        {
            // Unpackaged — fall back to the entry assembly version so
            // CheckAsync still returns something meaningful.
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            _version   = asm?.GetName().Version ?? new Version(0, 0, 0, 0);
            _aumid     = null;
            IsPackaged = false;
        }
        Architecture = ResolveArch();
    }

    public Version CurrentVersion => _version;
    public string? Aumid          => _aumid;
    public bool    IsPackaged     { get; }
    public string  Architecture   { get; }

    static string ResolveArch() => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
        System.Runtime.InteropServices.Architecture.X86   => "x86",
        _                                                 => "x64",
    };
}
