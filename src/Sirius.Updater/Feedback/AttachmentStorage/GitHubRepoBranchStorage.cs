using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Diagnostics;
using Sirius.Updater.Sources;

namespace Sirius.Updater.Feedback.AttachmentStorage;

/// <summary>
/// Default attachment storage: uploads files as one commit to a
/// dedicated branch on a GitHub repo, using the Git Data API
/// (<c>POST /repos/{owner/repo}/git/blobs|trees|commits</c> +
/// <c>PATCH /git/refs/heads/{branch}</c>).
///
/// Why this shape:
/// <list type="bullet">
///   <item>Single commit per submission keeps history readable.</item>
///   <item>Orphan branch so attachments never appear in <c>main</c>'s
///         git log or diffs.</item>
///   <item>Web URLs (<c>github.com/{owner/repo}/raw/{sha}/{path}</c>)
///         are treated as first-class image sources by GitHub's issue
///         renderer, so screenshots render inline. We deliberately
///         avoid <c>raw.githubusercontent.com</c> because that host
///         requires a signed query token for private/EMU repos and so
///         404s in the browser; the <c>github.com/.../raw/...</c> form
///         redirects via session cookies and works for both public and
///         private repos.</item>
///   <item>Works against the same repo as the issue OR a dedicated
///         feedback-attachments repo (set via
///         <see cref="SiriusFeedbackOptions.AttachmentsRepository"/>).</item>
/// </list>
///
/// On first use, the branch is created as an orphan with a seed
/// <c>README.md</c> explaining the branch's purpose. Subsequent
/// submissions extend that branch.
///
/// Per-attachment failures (auth denied for write, blob too large,
/// network blip) are converted to <see cref="AttachmentDisposition.Omitted"/>
/// entries so the issue still goes out with a clear "(could not
/// upload)" note instead of failing the entire submission.
/// </summary>
public sealed class GitHubRepoBranchStorage : IAttachmentStorage
{
    readonly IUpdaterLog _log;

    public GitHubRepoBranchStorage(IUpdaterLog? log = null)
    {
        _log = log ?? NullUpdaterLog.Instance;
    }

    public async Task<IReadOnlyList<UploadedAttachment>> UploadAsync(
        HttpClient http,
        string? token,
        FeedbackContext ctx,
        IReadOnlyList<FeedbackAttachment> attachments,
        IProgress<FeedbackProgress>? progress,
        CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(token))
        {
            // We need a token to write commits. Surface this so each
            // attachment shows up as omitted with a clear reason.
            var stub = new List<UploadedAttachment>(attachments.Count);
            foreach (var a in attachments)
            {
                stub.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                    AttachmentDisposition.Omitted, Reason: "not signed in to GitHub"));
            }
            return stub;
        }

        // 1. Resolve (or create) the branch tip.
        string parentCommitSha;
        string parentTreeSha;
        try
        {
            (parentCommitSha, parentTreeSha) = await EnsureBranchAsync(http, token, ctx, cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("feedback-storage", $"Could not prepare branch {ctx.AttachmentsBranch}: {ex.Message}", ex);
            // Could not even create the branch — degrade everything.
            var stub = new List<UploadedAttachment>(attachments.Count);
            foreach (var a in attachments)
            {
                stub.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                    AttachmentDisposition.Omitted, Reason: $"attachment branch unavailable: {ex.Message}"));
            }
            return stub;
        }

        // 2. Upload each attachment as a blob (base64). Track per-file
        //    outcomes so we can fall back to "omitted" on individual failures.
        var pathPrefix = BuildPathPrefix(ctx);
        var blobShas   = new Dictionary<string, string>(StringComparer.Ordinal);
        var outcomes   = new List<UploadedAttachment>(attachments.Count);
        var totalIncluded = 0;
        foreach (var a in attachments) if (a.Include) totalIncluded++;

        var i = 0;
        foreach (var a in attachments)
        {
            if (!a.Include)
            {
                outcomes.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                    AttachmentDisposition.Omitted, Reason: "opted out by user"));
                continue;
            }

            i++;
            try
            {
                progress?.Report(new FeedbackProgress(
                    FeedbackStage.Uploading,
                    $"Uploading {a.FileName}",
                    Detail: totalIncluded > 1 ? $"file {i} of {totalIncluded}" : null,
                    Percent: totalIncluded > 0 ? (double)(i - 1) / totalIncluded * 100.0 : null));

                var bytes = a.ReadAllBytes();
                var sha   = await CreateBlobAsync(http, token, ctx.AttachmentsRepository,
                                                   bytes, cancel).ConfigureAwait(false);
                var relativePath = pathPrefix + "/" + a.FileName;
                blobShas[relativePath] = sha;

                var rawUrl = BuildRawUrl(ctx.AttachmentsRepository, ctx.AttachmentsBranch, relativePath);
                outcomes.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                    AttachmentDisposition.Uploaded, Url: rawUrl));
            }
            catch (Exception ex)
            {
                _log.Warn("feedback-storage", $"Upload failed for {a.FileName}: {ex.Message}", ex);
                outcomes.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                    AttachmentDisposition.Omitted, Reason: $"upload failed: {ex.Message}"));
            }
        }

        if (blobShas.Count == 0) return outcomes; // nothing to commit

        // 3. Create a tree (extension of the parent tree) holding the new blobs.
        try
        {
            var treeSha   = await CreateTreeAsync(http, token, ctx.AttachmentsRepository,
                                                  parentTreeSha, blobShas, cancel).ConfigureAwait(false);
            var commitSha = await CreateCommitAsync(http, token, ctx.AttachmentsRepository,
                $"Feedback attachments {ctx.SubmissionId} ({DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z)",
                treeSha, parentCommitSha, cancel).ConfigureAwait(false);
            await UpdateRefAsync(http, token, ctx.AttachmentsRepository, ctx.AttachmentsBranch,
                                 commitSha, cancel).ConfigureAwait(false);

            // Rewrite URLs to point at the actual commit SHA — these
            // are immutable, so even if the branch later moves the
            // attachments stay reachable.
            for (var idx = 0; idx < outcomes.Count; idx++)
            {
                var o = outcomes[idx];
                if (o.Disposition != AttachmentDisposition.Uploaded) continue;
                var relativePath = pathPrefix + "/" + o.FileName;
                if (!blobShas.ContainsKey(relativePath)) continue;
                var immutableUrl = BuildRawUrl(ctx.AttachmentsRepository, commitSha, relativePath);
                outcomes[idx] = o with { Url = immutableUrl };
            }
        }
        catch (Exception ex)
        {
            _log.Warn("feedback-storage", $"Commit failed: {ex.Message}", ex);
            // Blobs are uploaded but unreachable until the commit lands —
            // mark as omitted with a clear reason so the issue body
            // doesn't link to dead URLs.
            for (var idx = 0; idx < outcomes.Count; idx++)
            {
                var o = outcomes[idx];
                if (o.Disposition != AttachmentDisposition.Uploaded) continue;
                outcomes[idx] = new UploadedAttachment(o.FileName, o.Size, o.Kind,
                    AttachmentDisposition.Omitted, Reason: $"commit failed: {ex.Message}");
            }
        }

        return outcomes;
    }

    // ── branch / ref management ──────────────────────────────────────────

    async Task<(string CommitSha, string TreeSha)> EnsureBranchAsync(
        HttpClient http, string token, FeedbackContext ctx, CancellationToken cancel)
    {
        // Try to read the branch ref. 404 → create the branch as orphan.
        var refUri = ApiUri(ctx.AttachmentsRepository, $"git/ref/heads/{Uri.EscapeDataString(ctx.AttachmentsBranch)}");
        var (status, refDto) = await TryGetAsync<RefDto>(http, token, refUri, cancel).ConfigureAwait(false);
        if (status == HttpStatusCode.OK && refDto?.Object is { } obj && !string.IsNullOrEmpty(obj.Sha))
        {
            var commit = await SendJsonAsync<CommitDto>(http, token, HttpMethod.Get,
                ApiUri(ctx.AttachmentsRepository, $"git/commits/{obj.Sha}"), body: null, cancel).ConfigureAwait(false);
            if (commit?.Tree?.Sha is { Length: > 0 } treeSha)
                return (obj.Sha, treeSha);
            throw new InvalidOperationException("Branch tip commit has no tree.");
        }
        if (status != HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Unexpected status looking up branch ref: {(int)status}");
        }

        // Branch doesn't exist — bootstrap it as an orphan with a seed README.
        var seedReadme = BuildSeedReadme(ctx);
        var blobSha    = await CreateBlobAsync(http, token, ctx.AttachmentsRepository,
                            System.Text.Encoding.UTF8.GetBytes(seedReadme), cancel).ConfigureAwait(false);
        var treeReq    = new TreeRequest
        {
            Tree = new[]
            {
                new TreeEntry { Path = "README.md", Mode = "100644", Type = "blob", Sha = blobSha },
            },
        };
        var tree = await SendJsonAsync<TreeDto>(http, token, HttpMethod.Post,
            ApiUri(ctx.AttachmentsRepository, "git/trees"), treeReq, cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tree creation returned no body.");

        var commitReq = new CommitRequest
        {
            Message = $"Initialise {ctx.AttachmentsBranch} (Sirius Feedback)",
            Tree    = tree.Sha!,
            Parents = Array.Empty<string>(),
        };
        var commitDto = await SendJsonAsync<CommitDto>(http, token, HttpMethod.Post,
            ApiUri(ctx.AttachmentsRepository, "git/commits"), commitReq, cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Commit creation returned no body.");

        var createRefReq = new CreateRefRequest
        {
            Ref = $"refs/heads/{ctx.AttachmentsBranch}",
            Sha = commitDto.Sha!,
        };
        _ = await SendJsonAsync<RefDto>(http, token, HttpMethod.Post,
            ApiUri(ctx.AttachmentsRepository, "git/refs"), createRefReq, cancel).ConfigureAwait(false);
        _log.Info("feedback-storage", $"Created orphan branch {ctx.AttachmentsBranch} on {ctx.AttachmentsRepository} ({commitDto.Sha![..7]})");
        return (commitDto.Sha!, tree.Sha!);
    }

    static string BuildSeedReadme(FeedbackContext ctx) =>
$@"# Feedback attachments

This branch holds files that users attached when they submitted feedback
about **{ctx.ProductName}** via Sirius. Each commit corresponds to one
submission and lands under
`attachments/{{yyyy}}/{{mm}}/{{dd}}/{{submission-id}}/`.

Issues live on the default branch — this branch only stores the
attachments those issues link to. It's an orphan branch with no shared
history, so it won't show up in `git log` on any other branch.
";

    // ── blob / tree / commit / ref ───────────────────────────────────────

    async Task<string> CreateBlobAsync(HttpClient http, string token, string ownerRepo,
                                       byte[] content, CancellationToken cancel)
    {
        var req = new BlobRequest
        {
            Content  = Convert.ToBase64String(content),
            Encoding = "base64",
        };
        var dto = await SendJsonAsync<BlobDto>(http, token, HttpMethod.Post,
            ApiUri(ownerRepo, "git/blobs"), req, cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Blob creation returned no body.");
        if (string.IsNullOrEmpty(dto.Sha))
            throw new InvalidOperationException("Blob creation returned no sha.");
        return dto.Sha!;
    }

    async Task<string> CreateTreeAsync(HttpClient http, string token, string ownerRepo,
                                       string baseTreeSha, IReadOnlyDictionary<string, string> blobShas,
                                       CancellationToken cancel)
    {
        var entries = new List<TreeEntry>(blobShas.Count);
        foreach (var kv in blobShas)
        {
            entries.Add(new TreeEntry
            {
                Path = kv.Key,
                Mode = "100644",
                Type = "blob",
                Sha  = kv.Value,
            });
        }
        var req = new TreeRequest { BaseTree = baseTreeSha, Tree = entries };
        var dto = await SendJsonAsync<TreeDto>(http, token, HttpMethod.Post,
            ApiUri(ownerRepo, "git/trees"), req, cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tree creation returned no body.");
        return dto.Sha ?? throw new InvalidOperationException("Tree creation returned no sha.");
    }

    async Task<string> CreateCommitAsync(HttpClient http, string token, string ownerRepo,
                                         string message, string treeSha, string parentSha,
                                         CancellationToken cancel)
    {
        var req = new CommitRequest
        {
            Message = message,
            Tree    = treeSha,
            Parents = new[] { parentSha },
        };
        var dto = await SendJsonAsync<CommitDto>(http, token, HttpMethod.Post,
            ApiUri(ownerRepo, "git/commits"), req, cancel).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Commit creation returned no body.");
        return dto.Sha ?? throw new InvalidOperationException("Commit creation returned no sha.");
    }

    async Task UpdateRefAsync(HttpClient http, string token, string ownerRepo,
                              string branch, string commitSha, CancellationToken cancel)
    {
        var req = new UpdateRefRequest { Sha = commitSha, Force = false };
        _ = await SendJsonAsync<RefDto>(http, token, new HttpMethod("PATCH"),
            ApiUri(ownerRepo, $"git/refs/heads/{Uri.EscapeDataString(branch)}"),
            req, cancel).ConfigureAwait(false);
    }

    // ── HTTP plumbing (mirrors GitHubReleaseSource) ─────────────────────

    static Uri ApiUri(string ownerRepo, string subPath) =>
        new($"https://api.github.com/repos/{ownerRepo.Trim('/')}/{subPath}");

    // Use github.com/{owner}/{repo}/raw/{ref}/{path} rather than
    // raw.githubusercontent.com/{...}. For PUBLIC repos both work
    // identically — the former just 302-redirects to the latter. For
    // PRIVATE / EMU repos, however, raw.githubusercontent.com requires
    // a signed `?token=...` query parameter and otherwise 404s in the
    // browser (even when the viewer is logged in for the repo), which
    // breaks inline image embedding and link clicks. The github.com
    // host inherits the viewer's session cookies and rewrites to a
    // short-lived signed raw URL, so both <img src> and plain <a href>
    // render correctly in the issue UI.
    static string BuildRawUrl(string ownerRepo, string commitOrBranch, string relativePath) =>
        $"https://github.com/{ownerRepo.Trim('/')}/raw/{Uri.EscapeDataString(commitOrBranch)}/" +
        string.Join('/', Array.ConvertAll(relativePath.Split('/'), Uri.EscapeDataString));

    static string BuildPathPrefix(FeedbackContext ctx) =>
        $"attachments/{ctx.StartedAtUtc:yyyy}/{ctx.StartedAtUtc:MM}/{ctx.StartedAtUtc:dd}/{ctx.SubmissionId}";

    static async Task<(HttpStatusCode, T?)> TryGetAsync<T>(
        HttpClient http, string token, Uri uri, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(req, cancel).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return (HttpStatusCode.NotFound, default);
        if (!resp.IsSuccessStatusCode)
        {
            if (IsAuthFailure(resp.StatusCode))
                throw new SourceAuthRequiredException($"GitHub denied access (status {(int)resp.StatusCode}).");
            return (resp.StatusCode, default);
        }
        var body = await resp.Content.ReadFromJsonAsync<T>(cancellationToken: cancel).ConfigureAwait(false);
        return (resp.StatusCode, body);
    }

    static async Task<T?> SendJsonAsync<T>(HttpClient http, string token, HttpMethod method,
                                            Uri uri, object? body, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(method, uri);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = JsonContent.Create(body);
        using var resp = await http.SendAsync(req, cancel).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(resp, cancel).ConfigureAwait(false);
            if (IsAuthFailure(resp.StatusCode))
                throw new SourceAuthRequiredException(
                    $"GitHub denied access (status {(int)resp.StatusCode}): {detail}");
            throw new HttpRequestException(
                $"GitHub API {method.Method} {uri} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
        }
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: cancel).ConfigureAwait(false);
    }

    static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken cancel)
    {
        try { return await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    static bool IsAuthFailure(HttpStatusCode code) =>
        code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    // ── DTOs ─────────────────────────────────────────────────────────────

    sealed class BlobRequest
    {
        [JsonPropertyName("content")]  public string Content  { get; set; } = "";
        [JsonPropertyName("encoding")] public string Encoding { get; set; } = "base64";
    }
    sealed class BlobDto
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
    }
    sealed class TreeRequest
    {
        // Omit when null — GitHub's Git Data API tolerates null for
        // base_tree on the orphan-branch seed commit, but matching the
        // IssueRequest convention is safer if the API ever tightens.
        [JsonPropertyName("base_tree"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BaseTree { get; set; }
        [JsonPropertyName("tree")]      public IReadOnlyList<TreeEntry> Tree { get; set; } = Array.Empty<TreeEntry>();
    }
    sealed class TreeEntry
    {
        [JsonPropertyName("path")] public string  Path { get; set; } = "";
        [JsonPropertyName("mode")] public string  Mode { get; set; } = "100644";
        [JsonPropertyName("type")] public string  Type { get; set; } = "blob";
        [JsonPropertyName("sha")]  public string? Sha  { get; set; }
    }
    sealed class TreeDto
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
    }
    sealed class CommitRequest
    {
        [JsonPropertyName("message")] public string             Message { get; set; } = "";
        [JsonPropertyName("tree")]    public string             Tree    { get; set; } = "";
        [JsonPropertyName("parents")] public IReadOnlyList<string> Parents { get; set; } = Array.Empty<string>();
    }
    sealed class CommitDto
    {
        [JsonPropertyName("sha")]  public string?       Sha  { get; set; }
        [JsonPropertyName("tree")] public TreeRefDto?   Tree { get; set; }
    }
    sealed class TreeRefDto
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
    }
    sealed class CreateRefRequest
    {
        [JsonPropertyName("ref")] public string Ref { get; set; } = "";
        [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    }
    sealed class UpdateRefRequest
    {
        [JsonPropertyName("sha")]   public string Sha   { get; set; } = "";
        [JsonPropertyName("force")] public bool   Force { get; set; }
    }
    sealed class RefDto
    {
        [JsonPropertyName("ref")]    public string?    RefName { get; set; }
        [JsonPropertyName("object")] public RefObject? Object  { get; set; }
    }
    sealed class RefObject
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
    }
}
