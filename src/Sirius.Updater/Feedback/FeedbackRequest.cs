using System.Collections.Generic;

namespace Sirius.Updater.Feedback;

/// <summary>
/// One feedback submission. Either fill in <see cref="Title"/> and
/// <see cref="Body"/> programmatically (silent submit) or leave them
/// null/empty and let the compose UI gather them from the user (typical
/// "Send feedback" button flow).
/// </summary>
public sealed class FeedbackRequest
{
    /// <summary>Pre-filled issue title. The compose UI shows this in an
    /// editable text box. Required at submit time — the UI enforces
    /// non-empty.</summary>
    public string? Title { get; init; }

    /// <summary>Pre-filled issue body. Markdown. May contain the
    /// configured <c>BodyTemplate</c> already merged in, or be left null
    /// to use the template as-is.</summary>
    public string? Body { get; init; }

    /// <summary>Labels to apply on creation. Merged with
    /// <see cref="SiriusFeedbackOptions.DefaultLabels"/>; duplicates are
    /// removed (case-insensitive).</summary>
    public IReadOnlyList<string>? Labels { get; init; }

    /// <summary>Optional assignees (GitHub usernames). Most consumer
    /// apps leave this empty and rely on repo-level routing.</summary>
    public IReadOnlyList<string>? Assignees { get; init; }

    /// <summary>Attachments to include. The user can opt out per-file in
    /// the compose UI.</summary>
    public IReadOnlyList<FeedbackAttachment>? Attachments { get; init; }

    /// <summary>Optional extra context (key/value) appended as a
    /// "Diagnostics" section in the issue body. Useful for static
    /// info like OS version, locale, screen resolution, etc.</summary>
    public IReadOnlyDictionary<string, string>? Diagnostics { get; init; }
}
