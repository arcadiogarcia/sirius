using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.Sources;

/// <summary>
/// GitHub Releases-backed source. Handles both public and private
/// repositories uniformly: asset downloads go through the
/// <c>/repos/{owner}/{repo}/releases/assets/{id}</c> API endpoint with
/// <c>Accept: application/octet-stream</c>, which serves the binary
/// (with redirects to the storage layer) and works for both visibility
/// modes. We never use the public <c>browser_download_url</c> path
/// because for private repos it returns the HTML download page, not
/// the file.
///
/// 401/403/404 are normalised to
/// <see cref="SourceAuthRequiredException"/> so the orchestrator can
/// kick off a Device-Flow retry. 404 in particular is the "you signed
/// in but the token can't see this repo" signal for private repos.
/// </summary>
public sealed class GitHubReleaseSource : IUpdateSource
{
    readonly string _ownerRepo;
    readonly Uri    _latestUri;

    public GitHubReleaseSource(string ownerRepo)
    {
        if (string.IsNullOrWhiteSpace(ownerRepo) || !ownerRepo.Contains('/'))
            throw new ArgumentException("Expected 'owner/repo'.", nameof(ownerRepo));

        _ownerRepo = ownerRepo.Trim('/');
        _latestUri = new Uri($"https://api.github.com/repos/{_ownerRepo}/releases/latest");
    }

    public string ChannelId => "github";

    public string? SuggestedLogin
    {
        get
        {
            var slash = _ownerRepo.IndexOf('/');
            return slash > 0 ? _ownerRepo.Substring(0, slash) : null;
        }
    }

    public async Task<UpdateRelease> GetLatestAsync(
        HttpClient http, string? token, bool includePreReleases, CancellationToken cancel)
    {
        try
        {
            // /releases/latest already returns only the latest non-prerelease,
            // non-draft. When the consumer opts into pre-releases we list all
            // and pick the first whose tag parses as a version.
            GitHubReleaseDto? release;
            if (includePreReleases)
            {
                var all = await SendJsonAsync<List<GitHubReleaseDto>>(http, token,
                    new Uri($"https://api.github.com/repos/{_ownerRepo}/releases?per_page=20"), cancel)
                    .ConfigureAwait(false);
                release = SelectLatest(all);
            }
            else
            {
                release = await SendJsonAsync<GitHubReleaseDto>(http, token, _latestUri, cancel).ConfigureAwait(false);
            }

            if (release is null)
                throw new InvalidOperationException("GitHub returned an empty response.");
            return ToModel(release);
        }
        catch (HttpRequestException ex) when (IsAuthFailure(ex.StatusCode))
        {
            throw new SourceAuthRequiredException(
                $"GitHub denied access to {_ownerRepo} (status {(int?)ex.StatusCode ?? 0}).", ex);
        }
    }

    public Uri ResolveAssetDownloadUri(UpdateAsset asset)
    {
        if (asset is null) throw new ArgumentNullException(nameof(asset));
        if (!string.IsNullOrEmpty(asset.ApiDownloadUri))     return new Uri(asset.ApiDownloadUri);
        if (!string.IsNullOrEmpty(asset.BrowserDownloadUri)) return new Uri(asset.BrowserDownloadUri);
        throw new InvalidOperationException($"Asset '{asset.Name}' has no download URL.");
    }

    // ── internals ────────────────────────────────────────────────────────

    static async Task<T?> SendJsonAsync<T>(HttpClient http, string? token, Uri uri, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        using var resp = await http.SendAsync(req, cancel).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: cancel).ConfigureAwait(false);
    }

    static bool IsAuthFailure(HttpStatusCode? code) =>
        code is HttpStatusCode.Unauthorized
             or HttpStatusCode.Forbidden
             or HttpStatusCode.NotFound;

    static GitHubReleaseDto? SelectLatest(IEnumerable<GitHubReleaseDto>? releases)
    {
        if (releases is null) return null;
        GitHubReleaseDto? best = null;
        Version? bestVersion = null;
        foreach (var r in releases)
        {
            if (r is null || r.Draft) continue;
            if (!TryParseTag(r.TagName, out var v)) continue;
            if (bestVersion is null || v > bestVersion)
            {
                bestVersion = v;
                best        = r;
            }
        }
        return best;
    }

    UpdateRelease ToModel(GitHubReleaseDto dto)
    {
        var version = (dto.TagName ?? string.Empty).TrimStart('v', 'V');
        var assets  = new List<UpdateAsset>();
        if (dto.Assets is not null)
        {
            foreach (var a in dto.Assets)
            {
                if (string.IsNullOrEmpty(a.Name)) continue;
                var apiUri = a.Id > 0
                    ? $"https://api.github.com/repos/{_ownerRepo}/releases/assets/{a.Id}"
                    : null;
                assets.Add(new UpdateAsset(
                    Name:               a.Name!,
                    Size:               a.Size,
                    ApiDownloadUri:     apiUri,
                    BrowserDownloadUri: a.BrowserDownloadUrl));
            }
        }
        return new UpdateRelease(
            Version:       version,
            IsPreRelease:  dto.Prerelease,
            Notes:         dto.Body,
            Assets:        assets,
            SourceTag:     dto.TagName);
    }

    static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrEmpty(tag)) return false;
        var s = tag.TrimStart('v', 'V');
        return Version.TryParse(s, out version!);
    }

    sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]   public string?              TagName    { get; set; }
        [JsonPropertyName("name")]       public string?              Name       { get; set; }
        [JsonPropertyName("body")]       public string?              Body       { get; set; }
        [JsonPropertyName("prerelease")] public bool                 Prerelease { get; set; }
        [JsonPropertyName("draft")]      public bool                 Draft      { get; set; }
        [JsonPropertyName("assets")]     public List<GitHubAssetDto>? Assets    { get; set; }
    }

    sealed class GitHubAssetDto
    {
        [JsonPropertyName("id")]                   public long    Id                  { get; set; }
        [JsonPropertyName("name")]                 public string? Name                { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl  { get; set; }
        [JsonPropertyName("size")]                 public long    Size                { get; set; }
    }
}
