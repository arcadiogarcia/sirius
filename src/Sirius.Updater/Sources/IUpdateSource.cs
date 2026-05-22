using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.Sources;

/// <summary>
/// Abstracts the "what's the latest release" lookup. The default
/// <see cref="GitHubReleaseSource"/> talks to the GitHub Releases API.
/// Custom sources can plug in alternative backends (Azure Artifacts,
/// internal mirrors, etc.) without touching the rest of the pipeline.
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// Identifies the channel for diagnostic strings (e.g. "github").
    /// </summary>
    string ChannelId { get; }

    /// <summary>
    /// Owner / login hint used when the auth UI needs to suggest which
    /// account the user should sign in as. Null when the source has no
    /// such concept.
    /// </summary>
    string? SuggestedLogin { get; }

    /// <summary>
    /// Fetches the latest release descriptor. <paramref name="token"/>
    /// is the access token to present (may be null for an anonymous
    /// call). Implementations should throw
    /// <see cref="SourceAuthRequiredException"/> on 401/403/404 so the
    /// caller can drive an interactive auth flow and retry.
    /// </summary>
    Task<UpdateRelease> GetLatestAsync(
        HttpClient http,
        string? token,
        bool includePreReleases,
        CancellationToken cancel);

    /// <summary>
    /// Builds the absolute URI the caller should download an asset
    /// from. Default implementations prefer authenticated API endpoints
    /// for private-repo support.
    /// </summary>
    Uri ResolveAssetDownloadUri(UpdateAsset asset);
}

/// <summary>
/// Thrown by <see cref="IUpdateSource"/> implementations when the
/// remote returns an auth failure. The orchestrator catches this and
/// drives the configured <see cref="Auth.IAuthenticator"/> before
/// retrying once.
/// </summary>
public sealed class SourceAuthRequiredException : Exception
{
    public SourceAuthRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}
