using System.Collections.Generic;

namespace Sirius.Updater;

/// <summary>
/// Descriptor of a release picked by an <see cref="Sources.IUpdateSource"/>.
/// Backend-agnostic so non-GitHub sources can plug in cleanly.
/// </summary>
public sealed record UpdateRelease(
    string Version,
    bool IsPreRelease,
    string? Notes,
    IReadOnlyList<UpdateAsset> Assets,
    string? SourceTag = null);

/// <summary>
/// Single downloadable file attached to an <see cref="UpdateRelease"/>.
/// <see cref="ApiDownloadUri"/> is preferred when set — for GitHub private
/// repos the public <c>browser_download_url</c> serves an HTML page, not
/// the binary.
/// </summary>
public sealed record UpdateAsset(
    string Name,
    long Size,
    string? ApiDownloadUri,
    string? BrowserDownloadUri);

/// <summary>
/// Context passed to <see cref="SiriusUpdaterOptions.AssetSelector"/> for
/// fully custom asset selection. The default selector uses
/// <see cref="SiriusUpdaterOptions.AssetNamePattern"/>.
/// </summary>
public sealed record UpdateAssetSelectionContext(
    UpdateRelease Release,
    string Architecture,
    string ProductName,
    string AssetBaseName);
