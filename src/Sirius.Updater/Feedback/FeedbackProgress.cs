namespace Sirius.Updater.Feedback;

/// <summary>
/// Coarse-grained stage of a feedback submission, used by the UI to
/// switch panels and by the <c>IProgress&lt;T&gt;</c> hook so consumers
/// can mirror status into their own log / status bar.
/// </summary>
public enum FeedbackStage
{
    /// <summary>Waiting for the user to fill in title/body and click Submit.</summary>
    Composing,
    /// <summary>Acquiring (or already-cached) GitHub token.</summary>
    SigningIn,
    /// <summary>Uploading attachments to the storage backend.</summary>
    Uploading,
    /// <summary>POSTing the issue itself.</summary>
    Posting,
    /// <summary>Final success state — issue created, URL available.</summary>
    Done,
    /// <summary>Final failure state.</summary>
    Failed,
}

/// <summary>
/// Progress tick emitted during <c>SubmitAsync</c>.
/// </summary>
public sealed record FeedbackProgress(
    FeedbackStage Stage,
    string Title,
    string? Detail = null,
    double? Percent = null,
    long? BytesTransferred = null,
    long? BytesTotal = null);
