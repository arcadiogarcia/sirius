using System.Collections.Generic;

namespace Sirius.Updater.Feedback;

/// <summary>
/// Outcome of a <see cref="SiriusFeedback.SubmitAsync(FeedbackRequest, System.IProgress{FeedbackProgress}?, System.Threading.CancellationToken)"/>
/// call. Mirrors the pattern of <c>UpdateInstallResult</c> — never
/// throws on the success-path consumer, errors are surfaced via
/// <see cref="Success"/> + <see cref="ErrorCode"/> + <see cref="Message"/>.
/// </summary>
/// <param name="Success">True when the issue was posted (even if some
/// attachments degraded to "could not upload").</param>
/// <param name="IssueUrl">Browser URL of the created issue
/// (e.g. <c>https://github.com/owner/repo/issues/42</c>). Null on
/// failure.</param>
/// <param name="IssueNumber">Issue number. Null on failure.</param>
/// <param name="Attachments">Per-attachment upload outcomes, including
/// inlined and omitted ones.</param>
/// <param name="ErrorCode">Stable machine-readable error code
/// (e.g. <c>AUTH_CANCELLED</c>, <c>POST_FAILED</c>) — null on success.</param>
/// <param name="Message">Human-readable description. On success a short
/// confirmation; on failure an explanation suitable for direct display.</param>
public sealed record FeedbackResult(
    bool Success,
    string? IssueUrl,
    int? IssueNumber,
    IReadOnlyList<UploadedAttachment> Attachments,
    string? ErrorCode = null,
    string? Message = null);
