using System;
using System.Collections.Generic;

namespace Sirius.Updater.Feedback;

/// <summary>
/// Strongly-typed configuration for <see cref="SiriusFeedback"/>. All
/// behavioural overrides go through here. The only required field is
/// <see cref="Repository"/>.
///
/// Defaults are tuned to "share auth + token cache with the matching
/// <see cref="SiriusUpdater"/>" — set <see cref="ProductName"/> to the
/// same value used by the updater and both modules transparently share
/// the cached GitHub token (same DPAPI directory + entropy).
/// </summary>
public sealed class SiriusFeedbackOptions
{
    /// <summary>
    /// GitHub "owner/repo" where the issue is filed. Required.
    /// </summary>
    public required string Repository { get; init; }

    /// <summary>
    /// "owner/repo" used as the destination for attachments. Defaults
    /// to <see cref="Repository"/> — i.e. attachments land in the same
    /// repo (on a dedicated <see cref="AttachmentsBranch"/>). Set to a
    /// dedicated feedback-attachments repo when end users don't have
    /// write access to the main repo, or when you want to keep storage
    /// fully isolated.
    /// </summary>
    public string? AttachmentsRepository { get; init; }

    /// <summary>
    /// Branch on <see cref="AttachmentsRepository"/> to commit
    /// attachments to. Auto-created as an orphan branch with a seed
    /// README on first use. Defaults to <c>feedback-attachments</c>.
    /// </summary>
    public string AttachmentsBranch { get; init; } = "feedback-attachments";

    /// <summary>
    /// Human-readable product name. Used in UI strings and the seed
    /// branch README. Should match the matching
    /// <see cref="SiriusUpdater"/>'s product name so they share the
    /// same token cache.
    /// </summary>
    public string ProductName { get; init; } = "Application";

    /// <summary>
    /// Markdown template pre-filled into the compose dialog's body
    /// when the caller doesn't supply one. Token replacement is
    /// performed for <c>{ProductName}</c>, <c>{Version}</c>,
    /// <c>{OS}</c>, <c>{Architecture}</c>.
    /// </summary>
    public string? BodyTemplate { get; init; }

    /// <summary>
    /// Labels applied to every issue. Merged with the per-request
    /// labels; duplicates removed (case-insensitive).
    /// </summary>
    public IReadOnlyList<string> DefaultLabels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// OAuth client id used for GitHub Device Flow. Defaults to the
    /// GitHub CLI app so the consent screen is the familiar gh-CLI one
    /// — no need to register your own OAuth application. Override for a
    /// branded consent UX.
    /// </summary>
    public string OAuthClientId { get; init; } = SiriusUpdaterDefaults.GitHubCliOAuthClientId;

    /// <summary>
    /// Scopes requested when interactive auth is needed. <c>repo</c>
    /// covers both private and public repos for issue creation +
    /// attachment commits. Same scope the updater requests, so the
    /// shared token cache works for both.
    /// </summary>
    public string OAuthScopes { get; init; } = "repo";

    /// <summary>
    /// Whether to persist the GitHub Device-Flow token across launches.
    /// Defaults to true. Uses the same DPAPI cache as
    /// <see cref="SiriusUpdater"/> when
    /// <see cref="ProductName"/> matches the updater's product name.
    /// </summary>
    public bool PersistToken { get; init; } = true;

    /// <summary>
    /// Defense-in-depth expiry on the cached token. Defaults to 30 days.
    /// </summary>
    public TimeSpan TokenMaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Directory where the encrypted token cache lives. Defaults to
    /// the MSIX <c>LocalState</c> folder (packaged) or
    /// <c>%LocalAppData%\&lt;ProductName&gt;\sirius-updater</c> — the
    /// same path the updater uses, so a single sign-in services both.
    /// </summary>
    public string? TokenStorageDirectory { get; init; }

    /// <summary>
    /// Environment variables (in order of preference) used as fallback
    /// API tokens before falling through to interactive Device Flow.
    /// </summary>
    public IReadOnlyList<string> EnvironmentTokenVariables { get; init; } = new[]
    {
        "GH_TOKEN",
        "GITHUB_TOKEN",
    };

    /// <summary>
    /// User-Agent string sent to the GitHub API. Defaults to
    /// <c>SiriusUpdater/&lt;version&gt;</c>.
    /// </summary>
    public string UserAgent { get; init; } = SiriusUpdaterDefaults.DefaultUserAgent;

    /// <summary>
    /// Inline cap: text attachments smaller than this are embedded as
    /// fenced code blocks in the issue body instead of being uploaded.
    /// Defaults to 64 KiB. Set to 0 to disable inlining entirely.
    /// </summary>
    public int InlineTextCapBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Per-attachment hard cap. Attachments larger than this are
    /// omitted from the submission with a "(omitted: too large)" note.
    /// Defaults to 50 MiB. Set to 0 for no cap (subject to GitHub's
    /// own ~100 MB blob limit).
    /// </summary>
    public long MaxAttachmentBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>
    /// Sum-of-uploads cap across one submission. Once exceeded, the
    /// remaining attachments are omitted with a "(omitted: total cap
    /// reached)" note. Defaults to 100 MiB.
    /// </summary>
    public long MaxTotalAttachmentBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>
    /// Optional advanced overrides. Leave null to use the built-in
    /// implementations.
    /// </summary>
    public SiriusFeedbackServices Services { get; init; } = new();

    /// <summary>Validate after construction. Throws on invalid input.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Repository) || !Repository.Contains('/'))
            throw new ArgumentException(
                $"SiriusFeedbackOptions.Repository must be in 'owner/repo' format (was '{Repository}').",
                nameof(Repository));

        if (!string.IsNullOrEmpty(AttachmentsRepository) && !AttachmentsRepository!.Contains('/'))
            throw new ArgumentException(
                $"SiriusFeedbackOptions.AttachmentsRepository must be in 'owner/repo' format (was '{AttachmentsRepository}').",
                nameof(AttachmentsRepository));

        if (string.IsNullOrWhiteSpace(AttachmentsBranch))
            throw new ArgumentException("SiriusFeedbackOptions.AttachmentsBranch must be non-empty.",
                nameof(AttachmentsBranch));

        if (TokenMaxAge <= TimeSpan.Zero)
            throw new ArgumentException("SiriusFeedbackOptions.TokenMaxAge must be positive.", nameof(TokenMaxAge));

        if (InlineTextCapBytes < 0)
            throw new ArgumentException("SiriusFeedbackOptions.InlineTextCapBytes must be >= 0.", nameof(InlineTextCapBytes));

        if (MaxAttachmentBytes < 0)
            throw new ArgumentException("SiriusFeedbackOptions.MaxAttachmentBytes must be >= 0.", nameof(MaxAttachmentBytes));

        if (MaxTotalAttachmentBytes < 0)
            throw new ArgumentException("SiriusFeedbackOptions.MaxTotalAttachmentBytes must be >= 0.", nameof(MaxTotalAttachmentBytes));
    }
}
