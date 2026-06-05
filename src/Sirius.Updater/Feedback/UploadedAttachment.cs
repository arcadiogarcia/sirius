namespace Sirius.Updater.Feedback;

/// <summary>
/// What happened to a single attachment.
/// </summary>
public enum AttachmentDisposition
{
    /// <summary>Inlined into the issue body as a fenced code block
    /// (text under the inline cap).</summary>
    Inlined,
    /// <summary>Uploaded to the attachments storage and linked from the
    /// issue body. <see cref="UploadedAttachment.Url"/> is the raw URL.</summary>
    Uploaded,
    /// <summary>Excluded by user opt-out, per-file size cap, total-size
    /// cap, or upload failure. <see cref="UploadedAttachment.Reason"/>
    /// carries the why.</summary>
    Omitted,
}

/// <summary>
/// Per-attachment outcome reported on <see cref="FeedbackResult"/>.
/// </summary>
public sealed record UploadedAttachment(
    string FileName,
    long Size,
    FeedbackAttachmentKind Kind,
    AttachmentDisposition Disposition,
    string? Url = null,
    string? Reason = null);
