using System;
using System.IO;

namespace Sirius.Updater.Feedback;

/// <summary>
/// Classification of an attachment that drives how
/// <see cref="Sirius.Updater.Feedback.Sinks.GitHubIssuesSink"/> embeds it
/// in the issue body.
/// <list type="bullet">
///   <item><see cref="Text"/> — UTF-8 text. Small payloads (≤ the
///         configured inline cap) are inlined as fenced code blocks;
///         larger ones are uploaded and linked.</item>
///   <item><see cref="Image"/> — png/jpg/gif/webp/bmp/svg. Always
///         uploaded; embedded as <c>![alt](url)</c> so GitHub renders
///         them inline in the issue.</item>
///   <item><see cref="Binary"/> — anything else. Uploaded and linked.</item>
/// </list>
/// </summary>
public enum FeedbackAttachmentKind
{
    Text,
    Image,
    Binary,
}

/// <summary>
/// A single file to include with a feedback submission. Use one of the
/// static factories — they handle kind inference and lazy IO so the
/// caller doesn't have to read whole files into memory before knowing
/// whether the attachment will actually be uploaded.
/// </summary>
public sealed class FeedbackAttachment
{
    readonly Func<byte[]> _read;

    FeedbackAttachment(string fileName, string mediaType, long size,
                      FeedbackAttachmentKind kind, Func<byte[]> read,
                      string? description = null)
    {
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("File name required.", nameof(fileName));
        FileName    = Path.GetFileName(fileName);
        MediaType   = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType;
        Size        = size;
        Kind        = kind;
        Description = description;
        _read       = read ?? throw new ArgumentNullException(nameof(read));
    }

    /// <summary>File name as it'll appear in the issue body and the
    /// uploaded path. Any directory components in the source path are
    /// stripped — uploads always land under
    /// <c>attachments/{date}/{guid}/{fileName}</c>.</summary>
    public string FileName { get; }

    /// <summary>MIME type. Used to set the Content-Type header on
    /// uploads and to choose between Image/Binary/Text rendering when
    /// the caller doesn't pre-classify.</summary>
    public string MediaType { get; }

    /// <summary>Size in bytes. Reported in the issue body next to the
    /// link so reviewers know what they're about to download.</summary>
    public long Size { get; }

    /// <summary>Classification governing how this attachment is
    /// rendered in the issue body.</summary>
    public FeedbackAttachmentKind Kind { get; }

    /// <summary>Optional short human description shown next to the
    /// attachment in the compose UI ("Last 5 minutes of app log",
    /// "Window screenshot at error time", etc.).</summary>
    public string? Description { get; init; }

    /// <summary>True when the attachment is enabled for upload. The
    /// compose UI flips this when the user un-checks an attachment.
    /// Defaults to true.</summary>
    public bool Include { get; set; } = true;

    /// <summary>Read the bytes. Cheap-by-default callers (text inline,
    /// image previews) should buffer the result; not memoised here to
    /// keep the model immutable-from-the-outside.</summary>
    public byte[] ReadAllBytes() => _read();

    // ── factories ────────────────────────────────────────────────────────

    /// <summary>Wrap a file already on disk. The file is read lazily so
    /// large logs don't bloat the process before the user actually
    /// submits.</summary>
    public static FeedbackAttachment FromFile(string path, string? fileName = null,
                                              FeedbackAttachmentKind? kind = null,
                                              string? mediaType = null,
                                              string? description = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path required.", nameof(path));
        var fi    = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException($"Attachment file not found: {path}", path);
        var name  = fileName ?? fi.Name;
        var k     = kind ?? GuessKindFromName(name);
        var mt    = mediaType ?? GuessMediaTypeFromName(name, k);
        return new FeedbackAttachment(name, mt, fi.Length, k, () => File.ReadAllBytes(path), description);
    }

    /// <summary>Wrap UTF-8 text content. Always classified as
    /// <see cref="FeedbackAttachmentKind.Text"/> so the sink can decide
    /// inline-vs-upload based on size.</summary>
    public static FeedbackAttachment FromText(string fileName, string content,
                                              string? description = null)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FeedbackAttachment(fileName, GuessMediaTypeFromName(fileName, FeedbackAttachmentKind.Text),
            bytes.LongLength, FeedbackAttachmentKind.Text, () => bytes, description);
    }

    /// <summary>Wrap raw bytes already in memory.</summary>
    public static FeedbackAttachment FromBytes(string fileName, byte[] bytes,
                                               FeedbackAttachmentKind? kind = null,
                                               string? mediaType = null,
                                               string? description = null)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        var k  = kind ?? GuessKindFromName(fileName);
        var mt = mediaType ?? GuessMediaTypeFromName(fileName, k);
        return new FeedbackAttachment(fileName, mt, bytes.LongLength, k, () => bytes, description);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    internal static FeedbackAttachmentKind GuessKindFromName(string name)
    {
        var ext = Path.GetExtension(name)?.ToLowerInvariant() ?? string.Empty;
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => FeedbackAttachmentKind.Image,
            ".txt" or ".log" or ".md" or ".json" or ".xml" or ".yaml" or ".yml"
                or ".csv" or ".ini" or ".cfg" or ".config" or ".cs" or ".ts"
                or ".js"  or ".py"  or ".html" or ".css" or ".sql" => FeedbackAttachmentKind.Text,
            _ => FeedbackAttachmentKind.Binary,
        };
    }

    internal static string GuessMediaTypeFromName(string name, FeedbackAttachmentKind kind)
    {
        var ext = Path.GetExtension(name)?.ToLowerInvariant() ?? string.Empty;
        return ext switch
        {
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"  => "image/gif",
            ".webp" => "image/webp",
            ".bmp"  => "image/bmp",
            ".svg"  => "image/svg+xml",
            ".json" => "application/json",
            ".xml"  => "application/xml",
            ".csv"  => "text/csv",
            ".md"   => "text/markdown",
            ".html" => "text/html",
            ".log" or ".txt" or ".yaml" or ".yml" or ".ini" or ".cfg" or ".config"
                or ".cs" or ".ts" or ".js" or ".py" or ".css" or ".sql" => "text/plain",
            ".zip"  => "application/zip",
            _ => kind == FeedbackAttachmentKind.Text ? "text/plain" : "application/octet-stream",
        };
    }
}
