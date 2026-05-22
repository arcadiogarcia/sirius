using System;

namespace Sirius.Updater.Internal;

internal static class VersionParsing
{
    /// <summary>Parse "v1.2.3" / "1.2.3" tolerantly; returns null on failure.</summary>
    public static Version? ParseTagOrNull(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(s, out var v) ? v : null;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024)        return $"{bytes / 1024.0:0.#} KiB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MiB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB";
    }
}
