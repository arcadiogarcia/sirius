using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.Feedback.Sinks;

/// <summary>
/// Endpoint that turns a composed request + uploaded attachments into a
/// posted issue. The only built-in implementation is
/// <see cref="GitHubIssuesSink"/>, but a consumer who wants to mirror
/// feedback into Linear / Jira / their own bug tracker can supply their
/// own.
/// </summary>
public interface IFeedbackSink
{
    /// <summary>
    /// Post the issue. Implementations should throw
    /// <see cref="Sirius.Updater.Sources.SourceAuthRequiredException"/>
    /// when the token is missing / invalid so the facade can re-run
    /// auth once before giving up.
    /// </summary>
    /// <returns>
    /// (issue url, issue number) on success. Implementations may also
    /// throw to surface other failures; the facade converts them into
    /// <see cref="FeedbackResult"/> with <c>POST_FAILED</c>.
    /// </returns>
    Task<(string IssueUrl, int IssueNumber)> PostAsync(
        HttpClient http,
        string? token,
        FeedbackContext context,
        FeedbackRequest request,
        IReadOnlyList<UploadedAttachment> uploaded,
        CancellationToken cancel);
}
