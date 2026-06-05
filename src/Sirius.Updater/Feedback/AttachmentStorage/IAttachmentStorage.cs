using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.Feedback.AttachmentStorage;

/// <summary>
/// Pluggable storage for feedback attachments. The default
/// <see cref="GitHubRepoBranchStorage"/> uploads files as a single
/// commit to an orphan branch on a GitHub repo. Consumers with their
/// own blob store (Azure Blob, S3, internal CDN) can implement this
/// interface to redirect uploads there without touching the rest of the
/// feedback pipeline.
/// </summary>
public interface IAttachmentStorage
{
    /// <summary>
    /// Upload the supplied attachments. Implementations should respect
    /// caller cancellation, never throw on a single-attachment failure
    /// (return an <see cref="UploadedAttachment"/> with
    /// <see cref="AttachmentDisposition.Omitted"/> instead so the sink
    /// can degrade gracefully), and progress-report through
    /// <paramref name="progress"/> when supplied.
    /// </summary>
    /// <param name="http">Shared HttpClient.</param>
    /// <param name="token">GitHub token from the auth pipeline. May be
    /// null for unauthenticated storage backends.</param>
    /// <param name="context">Submission context (repo, branch, ids).</param>
    /// <param name="attachments">Attachments to upload. Implementations
    /// should preserve order in the returned list and skip entries with
    /// <see cref="FeedbackAttachment.Include"/> = false.</param>
    /// <param name="progress">Optional progress sink.</param>
    Task<IReadOnlyList<UploadedAttachment>> UploadAsync(
        HttpClient http,
        string? token,
        FeedbackContext context,
        IReadOnlyList<FeedbackAttachment> attachments,
        System.IProgress<FeedbackProgress>? progress,
        CancellationToken cancel);
}
