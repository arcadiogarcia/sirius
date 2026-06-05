using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Diagnostics;
using Sirius.Updater.Sources;

namespace Sirius.Updater.Feedback.Sinks;

/// <summary>
/// Default feedback sink: composes the issue body from the request +
/// uploaded attachments and POSTs <c>/repos/{owner/repo}/issues</c>.
///
/// Body composition rules:
/// <list type="bullet">
///   <item>User-supplied <c>Body</c> first.</item>
///   <item>"Attachments" section — uploaded files linked, images
///         embedded as <c>![](raw)</c>, inlined text in fenced blocks,
///         omitted entries listed with the reason.</item>
///   <item>"Diagnostics" section — key/value pairs from
///         <see cref="FeedbackRequest.Diagnostics"/>.</item>
///   <item>Footer — small "Submitted via Sirius Feedback" line so
///         issues filed via the tool are recognisable on the GitHub
///         side.</item>
/// </list>
/// </summary>
public sealed class GitHubIssuesSink : IFeedbackSink
{
    readonly IUpdaterLog _log;
    readonly int _inlineTextCapBytes;

    public GitHubIssuesSink(int inlineTextCapBytes, IUpdaterLog? log = null)
    {
        if (inlineTextCapBytes < 0) throw new ArgumentOutOfRangeException(nameof(inlineTextCapBytes));
        _inlineTextCapBytes = inlineTextCapBytes;
        _log = log ?? NullUpdaterLog.Instance;
    }

    public async Task<(string IssueUrl, int IssueNumber)> PostAsync(
        HttpClient http,
        string? token,
        FeedbackContext ctx,
        FeedbackRequest request,
        IReadOnlyList<UploadedAttachment> uploaded,
        CancellationToken cancel)
    {
        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0)
            throw new ArgumentException("Feedback title is empty — refusing to file an empty issue.");

        var body = ComposeBody(ctx, request, uploaded);

        var payload = new IssueRequest
        {
            Title     = title,
            Body      = body,
            Labels    = request.Labels?.Count > 0 ? new List<string>(request.Labels) : null,
            Assignees = request.Assignees?.Count > 0 ? new List<string>(request.Assignees) : null,
        };

        var uri = new Uri($"https://api.github.com/repos/{ctx.Repository.Trim('/')}/issues");
        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, cancel).ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 403 or 404)
        {
            var detail = await SafeReadAsync(resp, cancel).ConfigureAwait(false);
            throw new SourceAuthRequiredException(
                $"GitHub denied issue POST to {ctx.Repository} (status {(int)resp.StatusCode}): {detail}");
        }
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(resp, cancel).ConfigureAwait(false);
            throw new HttpRequestException(
                $"GitHub issue POST failed: {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
        }

        var dto = await resp.Content.ReadFromJsonAsync<IssueDto>(cancellationToken: cancel).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("GitHub returned an empty issue payload.");
        if (string.IsNullOrEmpty(dto.HtmlUrl) || dto.Number <= 0)
            throw new InvalidOperationException("GitHub returned an issue with no URL/number.");
        _log.Info("feedback-sink", $"Filed issue #{dto.Number} at {dto.HtmlUrl}");
        return (dto.HtmlUrl!, dto.Number);
    }

    // ── body composition ────────────────────────────────────────────────

    internal string ComposeBody(FeedbackContext ctx, FeedbackRequest request,
                                IReadOnlyList<UploadedAttachment> uploaded)
    {
        var sb = new StringBuilder();
        var userBody = (request.Body ?? string.Empty).TrimEnd();
        if (userBody.Length > 0)
        {
            sb.Append(userBody);
            sb.Append("\n\n");
        }

        // Attachments section — only when there's something to say.
        var (atts, omitted) = SplitForDisplay(uploaded);
        var inlineAtts = new List<(UploadedAttachment Att, FeedbackAttachment Source)>();

        // Match uploaded entries with the corresponding source FeedbackAttachment
        // so we can inline the text for the "inlined" disposition. The caller
        // hasn't given us a map; we re-derive by filename. Names within a single
        // submission are unique enough — but if not, the first match wins.
        var sourceByName = new Dictionary<string, FeedbackAttachment>(StringComparer.Ordinal);
        if (request.Attachments is not null)
        {
            foreach (var a in request.Attachments)
            {
                if (!sourceByName.ContainsKey(a.FileName))
                    sourceByName[a.FileName] = a;
            }
        }

        if (atts.Count > 0 || omitted.Count > 0)
        {
            sb.Append("### Attachments\n\n");
            foreach (var a in atts)
            {
                switch (a.Disposition)
                {
                    case AttachmentDisposition.Uploaded when a.Kind == FeedbackAttachmentKind.Image:
                        sb.Append("**").Append(EscapeMd(a.FileName)).Append("** — ").Append(FormatBytes(a.Size)).Append("\n\n");
                        sb.Append("![").Append(EscapeMd(a.FileName)).Append("](").Append(a.Url).Append(")\n\n");
                        break;

                    case AttachmentDisposition.Uploaded:
                        sb.Append("- [").Append(EscapeMd(a.FileName)).Append("](").Append(a.Url).Append(") — ")
                          .Append(FormatBytes(a.Size)).Append("\n");
                        break;

                    case AttachmentDisposition.Inlined:
                        // Inlined text — find the source and emit a fenced block.
                        if (sourceByName.TryGetValue(a.FileName, out var src))
                            inlineAtts.Add((a, src));
                        break;
                }
            }

            // Inlined text comes after the list, each in its own collapsed block.
            foreach (var (a, src) in inlineAtts)
            {
                var text  = TryDecodeUtf8(src.ReadAllBytes());
                var fence = ChooseFence(text);
                sb.Append("\n<details><summary>").Append(EscapeMd(a.FileName))
                  .Append(" — ").Append(FormatBytes(a.Size)).Append("</summary>\n\n")
                  .Append(fence).Append(LanguageHintForName(a.FileName)).Append('\n')
                  .Append(text);
                if (!text.EndsWith('\n')) sb.Append('\n');
                sb.Append(fence).Append("\n</details>\n");
            }

            if (omitted.Count > 0)
            {
                sb.Append("\n> ⚠️ ").Append(omitted.Count).Append(" attachment(s) could not be included:\n");
                foreach (var o in omitted)
                {
                    sb.Append("> - `").Append(EscapeMd(o.FileName)).Append("` (")
                      .Append(FormatBytes(o.Size)).Append(") — ").Append(o.Reason ?? "no reason").Append("\n");
                }
            }
            sb.Append('\n');
        }

        if (request.Diagnostics is { Count: > 0 })
        {
            sb.Append("### Diagnostics\n\n");
            foreach (var kv in request.Diagnostics)
            {
                sb.Append("- **").Append(EscapeMd(kv.Key)).Append("**: ").Append(EscapeMd(kv.Value ?? "")).Append('\n');
            }
            sb.Append('\n');
        }

        sb.Append("---\n");
        sb.Append("<sub>Submitted via Sirius Feedback for ").Append(EscapeMd(ctx.ProductName))
          .Append(" · ").Append(ctx.StartedAtUtc.ToString("yyyy-MM-dd HH:mm:ss")).Append("Z · `")
          .Append(ctx.SubmissionId).Append("`</sub>\n");
        return sb.ToString();
    }

    static (List<UploadedAttachment> Active, List<UploadedAttachment> Omitted)
        SplitForDisplay(IReadOnlyList<UploadedAttachment> attachments)
    {
        var active  = new List<UploadedAttachment>(attachments.Count);
        var omitted = new List<UploadedAttachment>();
        foreach (var a in attachments)
        {
            if (a.Disposition == AttachmentDisposition.Omitted) omitted.Add(a);
            else                                                active.Add(a);
        }
        return (active, omitted);
    }

    static string TryDecodeUtf8(byte[] bytes)
    {
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return "(binary content – could not decode as UTF-8)"; }
    }

    static string ChooseFence(string content)
    {
        // Pick the shortest backtick run not present in the body, minimum 3.
        var max = 2;
        var run = 0;
        foreach (var ch in content)
        {
            if (ch == '`') { run++; if (run > max) max = run; }
            else run = 0;
        }
        return new string('`', max + 1);
    }

    static string LanguageHintForName(string name)
    {
        var ext = Path.GetExtension(name)?.ToLowerInvariant() ?? string.Empty;
        return ext switch
        {
            ".json" => "json",
            ".xml"  => "xml",
            ".yaml" or ".yml" => "yaml",
            ".cs"   => "csharp",
            ".ts"   => "typescript",
            ".js"   => "javascript",
            ".py"   => "python",
            ".sql"  => "sql",
            ".md"   => "markdown",
            ".html" => "html",
            ".css"  => "css",
            _ => "",
        };
    }

    static string EscapeMd(string s)
    {
        // Conservative — only escape characters that would actually break
        // the surrounding markdown patterns we use (links, list items).
        return s.Replace("[", "\\[").Replace("]", "\\]");
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024)            return $"{bytes} B";
        if (bytes < 1024L * 1024)    return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }

    static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken cancel)
    {
        try { return await resp.Content.ReadAsStringAsync(cancel).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────

    sealed class IssueRequest
    {
        [JsonPropertyName("title")]     public string?       Title     { get; set; }
        [JsonPropertyName("body")]      public string?       Body      { get; set; }
        [JsonPropertyName("labels")]    public List<string>? Labels    { get; set; }
        [JsonPropertyName("assignees")] public List<string>? Assignees { get; set; }
    }
    sealed class IssueDto
    {
        [JsonPropertyName("number")]   public int     Number  { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }
}
