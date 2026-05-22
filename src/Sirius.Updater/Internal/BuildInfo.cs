using System;
using System.IO;
using System.Reflection;

namespace Sirius.Updater.Internal;

/// <summary>
/// Reads build metadata (currently just the UTC build date) baked into
/// a caller's assembly via <c>[AssemblyMetadata("SiriusBuildDate", "...")]</c>.
/// Hosts wire this up by adding an <c>AssemblyMetadata</c> ItemGroup
/// in their csproj — see the README for the snippet.
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// Returns the build date stamped into the given assembly via the
    /// <c>SiriusBuildDate</c> metadata key (ISO-8601 / round-trip).
    /// Returns null when no such metadata exists.
    /// </summary>
    public static DateTimeOffset? ReadBuildDate(Assembly assembly)
    {
        if (assembly is null) return null;
        try
        {
            foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (!string.Equals(attr.Key, "SiriusBuildDate", StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(attr.Value)) continue;
                if (DateTimeOffset.TryParse(
                        attr.Value,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var v))
                {
                    return v;
                }
            }
        }
        catch
        {
            // Reflection failures are non-fatal; the date is purely a
            // diagnostic affordance.
        }
        // Fall back to the assembly file's last-write time on disk —
        // not a perfect substitute, but usually close.
        try
        {
            var path = assembly.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch { /* ignore */ }
        return null;
    }
}
