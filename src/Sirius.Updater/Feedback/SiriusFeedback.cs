using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Auth;
using Sirius.Updater.Diagnostics;
using Sirius.Updater.Feedback.AttachmentStorage;
using Sirius.Updater.Feedback.Sinks;
using Sirius.Updater.Feedback.Ui;
using Sirius.Updater.Hosting;
using Sirius.Updater.Sources;
using Sirius.Updater.TokenStore;

namespace Sirius.Updater.Feedback;

/// <summary>
/// Main entry point for Sirius Feedback. Construct once at startup
/// with a configured <see cref="SiriusFeedbackOptions"/>, then call
/// <see cref="SubmitAsync(FeedbackRequest, IProgress{FeedbackProgress}?, CancellationToken)"/>
/// from a "Send feedback" button.
///
/// Typical wiring:
/// <code>
/// var feedback = new SiriusFeedback(new SiriusFeedbackOptions
/// {
///     Repository  = "contoso/my-app",
///     ProductName = "MyApp",                // share token cache with updater
///     BodyTemplate = "### What happened\n\n",
/// });
/// feedback = feedback.WithWinUI(rootElement);            // compose dialog
/// feedback = feedback.WithAuthUi(updaterUi);             // re-use the updater's sign-in dialog
/// // ...
/// var result = await feedback.SubmitAsync(new FeedbackRequest
/// {
///     Attachments = new[]
///     {
///         FeedbackAttachment.FromFile(logPath, "app.log"),
///         FeedbackAttachment.FromBytes("screenshot.png", pngBytes),
///     },
/// });
/// </code>
/// </summary>
public sealed class SiriusFeedback : IDisposable
{
    readonly SiriusFeedbackOptions _options;
    readonly IFeedbackSink         _sink;
    readonly IAttachmentStorage    _storage;
    readonly IAuthenticator        _authenticator;
    readonly ITokenStore           _tokenStore;
    readonly IFeedbackUi           _ui;
    readonly Sirius.Updater.Ui.IUpdateUi? _authUi;
    readonly IUpdaterLog           _log;
    readonly IHostApplication      _host;
    readonly SemaphoreSlim         _gate = new(1, 1);
    readonly Lazy<HttpClient>      _http;

    public SiriusFeedback(SiriusFeedbackOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        _log           = options.Services.Log           ?? NullUpdaterLog.Instance;
        _host          = new PackagedHostApplication();
        _ui            = options.Services.Ui            ?? NullFeedbackUi.Instance;
        _authUi        = options.Services.AuthUi;
        _sink          = options.Services.Sink          ?? new GitHubIssuesSink(options.InlineTextCapBytes, _log);
        _storage       = options.Services.Storage       ?? new GitHubRepoBranchStorage(_log);
        _authenticator = options.Services.Authenticator ?? new GitHubDeviceFlowAuthenticator(
                            options.OAuthClientId, options.OAuthScopes, _log);
        _tokenStore    = options.Services.TokenStore    ?? BuildDefaultTokenStore(options, _log);

        _http = new Lazy<HttpClient>(() =>
        {
            var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            hc.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            return hc;
        });
    }

    /// <summary>The "owner/repo" issues are posted to.</summary>
    public string Repository => _options.Repository;

    /// <summary>The "owner/repo" attachments are uploaded to.</summary>
    public string AttachmentsRepository => _options.AttachmentsRepository ?? _options.Repository;

    /// <summary>The branch used for attachment commits.</summary>
    public string AttachmentsBranch => _options.AttachmentsBranch;

    // ── service-bag clones ──────────────────────────────────────────────

    /// <summary>Return a clone with the supplied feedback UI wired in.</summary>
    public SiriusFeedback WithUi(IFeedbackUi ui)
    {
        if (ui is null) throw new ArgumentNullException(nameof(ui));
        return CloneWith(s => s with { Ui = ui });
    }

    /// <summary>Return a clone with the supplied auth UI wired in.
    /// Typically the same <c>ContentDialogUpdateUi</c> the updater
    /// uses, so device-code sign-in has consistent UX.</summary>
    public SiriusFeedback WithAuthUi(Sirius.Updater.Ui.IUpdateUi authUi)
    {
        if (authUi is null) throw new ArgumentNullException(nameof(authUi));
        return CloneWith(s => s with { AuthUi = authUi });
    }

    /// <summary>
    /// Reuse the supplied updater's token store, authenticator, and
    /// auth UI. Equivalent to manually setting <see cref="SiriusFeedbackServices.TokenStore"/>,
    /// <see cref="SiriusFeedbackServices.Authenticator"/>, and
    /// <see cref="SiriusFeedbackServices.AuthUi"/> at construction.
    /// </summary>
    public SiriusFeedback SharingAuthWith(SiriusUpdater updater)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));
        return CloneWith(s => s with
        {
            TokenStore    = updater.TokenStore,
            Authenticator = updater.Authenticator,
            AuthUi        = updater.Ui,
        });
    }

    SiriusFeedback CloneWith(Func<SiriusFeedbackServices, SiriusFeedbackServices> mutate)
    {
        var current = new SiriusFeedbackServices
        {
            Sink          = _sink,
            Storage       = _storage,
            TokenStore    = _tokenStore,
            Authenticator = _authenticator,
            AuthUi        = _authUi,
            Ui            = _ui,
            Log           = _log,
        };
        var next = mutate(current);        var clone = new SiriusFeedbackOptions
        {
            Repository                = _options.Repository,
            AttachmentsRepository     = _options.AttachmentsRepository,
            AttachmentsBranch         = _options.AttachmentsBranch,
            ProductName               = _options.ProductName,
            BodyTemplate              = _options.BodyTemplate,
            DefaultLabels             = _options.DefaultLabels,
            OAuthClientId             = _options.OAuthClientId,
            OAuthScopes               = _options.OAuthScopes,
            PersistToken              = _options.PersistToken,
            TokenMaxAge               = _options.TokenMaxAge,
            TokenStorageDirectory     = _options.TokenStorageDirectory,
            EnvironmentTokenVariables = _options.EnvironmentTokenVariables,
            UserAgent                 = _options.UserAgent,
            InlineTextCapBytes        = _options.InlineTextCapBytes,
            MaxAttachmentBytes        = _options.MaxAttachmentBytes,
            MaxTotalAttachmentBytes   = _options.MaxTotalAttachmentBytes,
            Services                  = next,
        };
        return new SiriusFeedback(clone);
    }

    // ── main API ────────────────────────────────────────────────────────

    /// <summary>
    /// Run the feedback flow end-to-end. Opens the compose UI,
    /// gathers user input, signs in if needed, uploads attachments,
    /// posts the issue, and returns the URL + number.
    /// </summary>
    public async Task<FeedbackResult> SubmitAsync(
        FeedbackRequest? request = null,
        IProgress<FeedbackProgress>? progress = null,
        CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        IFeedbackUiSession? session = null;
        try
        {
            request ??= new FeedbackRequest();
            var attachments = request.Attachments ?? Array.Empty<FeedbackAttachment>();
            var startedAt   = DateTimeOffset.UtcNow;
            var ctx = new FeedbackContext(
                Repository:             _options.Repository,
                AttachmentsRepository:  AttachmentsRepository,
                AttachmentsBranch:      _options.AttachmentsBranch,
                ProductName:            _options.ProductName,
                SubmissionId:           Guid.NewGuid().ToString("N").Substring(0, 8),
                StartedAtUtc:           startedAt);

            // 1. Compose phase (UI gathers title/body + final include flags).
            session = await _ui.PresentAsync(new FeedbackComposeContext(
                ProductName:           _options.ProductName,
                Repository:            _options.Repository,
                AttachmentsRepository: ctx.AttachmentsRepository,
                AttachmentsBranch:     ctx.AttachmentsBranch,
                InitialTitle:          request.Title,
                InitialBody:           request.Body ?? RenderBodyTemplate(),
                Attachments:           attachments), cancel).ConfigureAwait(false);

            Report(progress, FeedbackStage.Composing, "Waiting for the user…");
            FeedbackComposeOutcome composed;
            try
            {
                composed = await session.ComposeAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Failure(attachments, "USER_CANCELLED", "Feedback cancelled.");
            }

            // 2. Classify attachments (inline vs upload vs omit) and enforce caps.
            var (toUpload, classifications) = ClassifyAttachments(composed.Attachments);

            // 3. Acquire token (cached or interactive).
            session.TransitionToSubmitting("Submitting feedback…", "Signing in to GitHub…");
            Report(progress, FeedbackStage.SigningIn, "Signing in to GitHub…");
            string? token;
            try
            {
                token = await AcquireTokenAsync(session, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                session.ShowError("Sign-in cancelled", "Try again when you're ready.");
                return Failure(composed.Attachments, "AUTH_CANCELLED", "GitHub sign-in was cancelled.");
            }
            catch (Exception ex)
            {
                _log.Error("feedback-auth", $"Auth failed: {ex.Message}", ex);
                session.ShowError("Couldn't sign in", ex.Message);
                return Failure(composed.Attachments, "AUTH_FAILED", ex.Message);
            }

            // 4. Upload attachments (progress + per-file failure tolerance).
            session.ReportProgress("Uploading attachments…", null, null);
            Report(progress, FeedbackStage.Uploading, "Uploading attachments…");
            IReadOnlyList<UploadedAttachment> uploaded;
            if (toUpload.Count == 0)
            {
                uploaded = Array.Empty<UploadedAttachment>();
            }
            else
            {
                try
                {
                    uploaded = await _storage.UploadAsync(_http.Value, token, ctx, toUpload,
                                    new ProgressBridge(progress), cancel).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn("feedback-storage", $"Storage threw: {ex.Message}", ex);
                    var fallback = new List<UploadedAttachment>(toUpload.Count);
                    foreach (var a in toUpload)
                        fallback.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                            AttachmentDisposition.Omitted, Reason: $"upload failed: {ex.Message}"));
                    uploaded = fallback;
                }
            }

            // 5. Merge classification results: inline + upload + omit.
            var merged = MergeDispositions(classifications, uploaded);

            // 6. POST the issue (one retry on auth failure with fresh token).
            session.ReportProgress("Filing issue on GitHub…", null, null);
            Report(progress, FeedbackStage.Posting, "Filing issue on GitHub…");
            string issueUrl;
            int    issueNumber;
            try
            {
                (issueUrl, issueNumber) = await PostWithAuthRetryAsync(session, ctx, request, composed, merged, token, cancel)
                                            .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                session.ShowError("Cancelled", "Submission cancelled.");
                return new FeedbackResult(Success: false, IssueUrl: null, IssueNumber: null,
                    Attachments: merged, ErrorCode: "USER_CANCELLED", Message: "Submission cancelled.");
            }
            catch (Exception ex)
            {
                _log.Error("feedback-sink", $"Issue post failed: {ex.Message}", ex);
                session.ShowError("Couldn't file the issue", ex.Message);
                return new FeedbackResult(Success: false, IssueUrl: null, IssueNumber: null,
                    Attachments: merged, ErrorCode: "POST_FAILED", Message: ex.Message);
            }

            session.ShowSuccess(issueUrl, issueNumber, merged);
            Report(progress, FeedbackStage.Done, "Feedback submitted", Detail: issueUrl);
            _log.Info("feedback", $"Submitted issue #{issueNumber}: {issueUrl}");
            return new FeedbackResult(Success: true, IssueUrl: issueUrl, IssueNumber: issueNumber,
                Attachments: merged, ErrorCode: null,
                Message: $"Filed #{issueNumber} at {issueUrl}");
        }
        finally
        {
            // Don't auto-close — the UI's success/error state owns the
            // dialog from here. Caller can close via DisposeAsync on the
            // session if they want to dismiss programmatically. We keep
            // a reference long enough for the success state to render,
            // and let the dialog be dismissed by the user.
            _ = session; // keep variable used
            _gate.Release();
        }
    }

    // ── token acquisition ───────────────────────────────────────────────

    async Task<string?> AcquireTokenAsync(IFeedbackUiSession session, CancellationToken cancel)
    {
        var token = await _tokenStore.LoadAsync(cancel).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token)) return token;

        // No cached token — run interactive auth via the configured auth UI.
        var result = await _authenticator.AuthenticateAsync(
            _http.Value, _authUi, suggestedLogin: GuessLogin(_options.Repository), cancel)
            .ConfigureAwait(false);
        if (result is null || string.IsNullOrEmpty(result.Token))
        {
            throw new InvalidOperationException(
                $"Authentication required to post to {_options.Repository} and no UI is available. " +
                $"Either set one of {string.Join(", ", _options.EnvironmentTokenVariables)} or attach an IUpdateUi " +
                "via SiriusFeedback.WithAuthUi() / SharingAuthWith(updater).");
        }
        await _tokenStore.SaveAsync(result.Token, cancel).ConfigureAwait(false);

        // Close the auth dialog — feedback UI takes over for progress display.
        if (result.UiSession is not null)
        {
            try { await result.UiSession.CloseAsync().ConfigureAwait(false); } catch { }
        }
        return result.Token;
    }

    static string? GuessLogin(string ownerRepo)
    {
        var slash = ownerRepo.IndexOf('/');
        return slash > 0 ? ownerRepo[..slash] : null;
    }

    // ── classification ──────────────────────────────────────────────────

    (List<FeedbackAttachment> ToUpload, List<UploadedAttachment> Decisions) ClassifyAttachments(
        IReadOnlyList<FeedbackAttachment> attachments)
    {
        var toUpload  = new List<FeedbackAttachment>();
        var decisions = new List<UploadedAttachment>(attachments.Count);
        long runningTotal = 0;
        foreach (var a in attachments)
        {
            if (!a.Include)
            {
                decisions.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                    AttachmentDisposition.Omitted, Reason: "opted out by user"));
                continue;
            }
            if (_options.MaxAttachmentBytes > 0 && a.Size > _options.MaxAttachmentBytes)
            {
                decisions.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                    AttachmentDisposition.Omitted,
                    Reason: $"too large: {FormatBytes(a.Size)} > {FormatBytes(_options.MaxAttachmentBytes)}"));
                continue;
            }
            // Inline small text.
            if (a.Kind == FeedbackAttachmentKind.Text &&
                _options.InlineTextCapBytes > 0 && a.Size <= _options.InlineTextCapBytes)
            {
                decisions.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind, AttachmentDisposition.Inlined));
                continue;
            }
            // Enforce total cap.
            if (_options.MaxTotalAttachmentBytes > 0 && runningTotal + a.Size > _options.MaxTotalAttachmentBytes)
            {
                decisions.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                    AttachmentDisposition.Omitted,
                    Reason: $"total upload cap reached ({FormatBytes(_options.MaxTotalAttachmentBytes)})"));
                continue;
            }
            runningTotal += a.Size;
            toUpload.Add(a);
            // Placeholder decision — replaced after upload returns.
            decisions.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind, AttachmentDisposition.Uploaded));
        }
        return (toUpload, decisions);
    }

    static IReadOnlyList<UploadedAttachment> MergeDispositions(
        IReadOnlyList<UploadedAttachment> initial, IReadOnlyList<UploadedAttachment> uploaded)
    {
        if (uploaded.Count == 0) return initial;
        var idxByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < initial.Count; i++)
        {
            if (initial[i].Disposition != AttachmentDisposition.Uploaded) continue;
            // Use FIRST unconsumed entry for each filename.
            if (!idxByName.ContainsKey(initial[i].FileName))
                idxByName[initial[i].FileName] = i;
        }
        var merged = new UploadedAttachment[initial.Count];
        for (var i = 0; i < initial.Count; i++) merged[i] = initial[i];
        foreach (var u in uploaded)
        {
            if (idxByName.TryGetValue(u.FileName, out var idx))
            {
                merged[idx] = u;
                idxByName.Remove(u.FileName);
            }
        }
        return merged;
    }

    // ── post with auth retry ────────────────────────────────────────────

    async Task<(string IssueUrl, int IssueNumber)> PostWithAuthRetryAsync(
        IFeedbackUiSession session, FeedbackContext ctx, FeedbackRequest request,
        FeedbackComposeOutcome composed, IReadOnlyList<UploadedAttachment> uploaded,
        string? token, CancellationToken cancel)
    {
        var effective = new FeedbackRequest
        {
            Title       = composed.Title,
            Body        = composed.Body,
            Labels      = MergeLabels(request.Labels, _options.DefaultLabels),
            Assignees   = request.Assignees,
            Attachments = composed.Attachments,
            Diagnostics = request.Diagnostics,
        };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _sink.PostAsync(_http.Value, token, ctx, effective, uploaded, cancel)
                                  .ConfigureAwait(false);
            }
            catch (SourceAuthRequiredException) when (attempt == 0)
            {
                _log.Info("feedback-auth", "Issue post requires fresh auth — clearing cache and retrying once.");
                await _tokenStore.ClearAsync(cancel).ConfigureAwait(false);
                token = await AcquireTokenAsync(session, cancel).ConfigureAwait(false);
            }
        }
    }

    static IReadOnlyList<string>? MergeLabels(IReadOnlyList<string>? a, IReadOnlyList<string> b)
    {
        if ((a is null || a.Count == 0) && b.Count == 0) return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        if (a is not null) foreach (var l in a) if (seen.Add(l)) list.Add(l);
        foreach (var l in b) if (seen.Add(l)) list.Add(l);
        return list;
    }

    // ── body template ──────────────────────────────────────────────────

    string? RenderBodyTemplate()
    {
        var t = _options.BodyTemplate;
        if (string.IsNullOrEmpty(t)) return null;
        return t.Replace("{ProductName}",  _options.ProductName, StringComparison.Ordinal)
                .Replace("{Version}",       _host.CurrentVersion?.ToString() ?? "unknown", StringComparison.Ordinal)
                .Replace("{OS}",            Environment.OSVersion.VersionString, StringComparison.Ordinal)
                .Replace("{Architecture}",  _host.Architecture ?? "unknown", StringComparison.Ordinal);
    }

    // ── housekeeping ────────────────────────────────────────────────────

    static FeedbackResult Failure(IReadOnlyList<FeedbackAttachment> attachments, string code, string message)
    {
        var placeholders = new UploadedAttachment[attachments.Count];
        for (var i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i];
            placeholders[i] = new UploadedAttachment(a.FileName, a.Size, a.Kind,
                AttachmentDisposition.Omitted, Reason: "submission failed before upload");
        }
        return new FeedbackResult(Success: false, IssueUrl: null, IssueNumber: null,
            Attachments: placeholders, ErrorCode: code, Message: message);
    }

    static void Report(IProgress<FeedbackProgress>? p, FeedbackStage stage, string title,
                       string? Detail = null, double? Percent = null)
    {
        try { p?.Report(new FeedbackProgress(stage, title, Detail, Percent)); } catch { /* swallow */ }
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024)                return $"{bytes} B";
        if (bytes < 1024L * 1024)        return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }

    static ITokenStore BuildDefaultTokenStore(SiriusFeedbackOptions options, IUpdaterLog log)
    {
        var layers = new List<ITokenStore> { new InMemoryTokenStore() };
        if (options.EnvironmentTokenVariables.Count > 0)
            layers.Add(new EnvironmentTokenStore(options.EnvironmentTokenVariables));
        if (options.PersistToken)
        {
            var dir = options.TokenStorageDirectory ?? ResolveDefaultTokenDir(options.ProductName);
            // EXACT same entropy formula the updater uses — so a sign-in
            // through either module is picked up by the other.
            var entropy = $"SiriusUpdater/{options.ProductName}/v1";
            layers.Add(new DpapiTokenStore(
                directory: dir,
                entropy:   entropy,
                maxAge:    options.TokenMaxAge,
                log:       log));
        }
        return new LayeredTokenStore(layers, log);
    }

    static string ResolveDefaultTokenDir(string productName)
    {
        try
        {
            var local = global::Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            if (!string.IsNullOrEmpty(local)) return Path.Combine(local, "sirius-updater");
        }
        catch { /* unpackaged */ }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            productName.Replace(" ", string.Empty),
            "sirius-updater");
    }

    public void Dispose()
    {
        try { if (_http.IsValueCreated) _http.Value.Dispose(); } catch { }
        try { _gate.Dispose(); } catch { }
    }

    // Adapter so storage can emit FeedbackProgress directly into the
    // caller's IProgress.
    sealed class ProgressBridge : IProgress<FeedbackProgress>
    {
        readonly IProgress<FeedbackProgress>? _inner;
        public ProgressBridge(IProgress<FeedbackProgress>? inner) => _inner = inner;
        public void Report(FeedbackProgress value)
        {
            try { _inner?.Report(value); } catch { /* swallow */ }
        }
    }
}
