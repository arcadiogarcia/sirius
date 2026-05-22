using System.Reflection;

namespace Sirius.Updater;

/// <summary>
/// Library-wide constants. Kept in one place so the test suite can pin
/// them and consumers can reference them without copy-pasting magic
/// strings.
/// </summary>
public static class SiriusUpdaterDefaults
{
    /// <summary>
    /// GitHub CLI's public OAuth app client id. Using it means the consent
    /// screen the user sees during Device Flow is the familiar "GitHub
    /// CLI" page — no need for each consuming app to register its own
    /// OAuth app just to read private release assets.
    /// Source: <see href="https://github.com/cli/cli/blob/trunk/internal/authflow/flow.go"/>.
    /// </summary>
    public const string GitHubCliOAuthClientId = "178c6fc778ccc68e1d6a";

    /// <summary>
    /// Default User-Agent header. GitHub's API requires one; using the
    /// library identity makes our traffic easy to spot in audit logs.
    /// </summary>
    public static string DefaultUserAgent { get; } =
        $"SiriusUpdater/{typeof(SiriusUpdaterDefaults).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0"}";
}
