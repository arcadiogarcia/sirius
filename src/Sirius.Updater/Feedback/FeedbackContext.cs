using System;

namespace Sirius.Updater.Feedback;

/// <summary>
/// Per-submission context handed to storage and sink implementations.
/// Stable for the duration of one <c>SubmitAsync</c> call.
/// </summary>
/// <param name="Repository">"owner/repo" the issue itself is being
/// posted to.</param>
/// <param name="AttachmentsRepository">"owner/repo" the attachments are
/// uploaded to (may differ from <paramref name="Repository"/> when the
/// consumer configures a dedicated attachments repo).</param>
/// <param name="AttachmentsBranch">Branch on
/// <paramref name="AttachmentsRepository"/> that holds attachments.</param>
/// <param name="ProductName">Product name from the options, surfaced in
/// UI strings.</param>
/// <param name="SubmissionId">Per-submission short id used in upload
/// paths (<c>attachments/{yyyy}/{mm}/{dd}/{SubmissionId}/...</c>) so the
/// raw URLs are unique even when filenames collide.</param>
/// <param name="StartedAtUtc">UTC timestamp the submission started at.</param>
public sealed record FeedbackContext(
    string Repository,
    string AttachmentsRepository,
    string AttachmentsBranch,
    string ProductName,
    string SubmissionId,
    DateTimeOffset StartedAtUtc);
