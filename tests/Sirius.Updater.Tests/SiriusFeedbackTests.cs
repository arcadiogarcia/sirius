using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Feedback;
using Sirius.Updater.Feedback.AttachmentStorage;
using Sirius.Updater.Feedback.Sinks;
using Sirius.Updater.Feedback.Ui;
using Sirius.Updater.TokenStore;
using Xunit;

namespace Sirius.Updater.Tests;

public class SiriusFeedbackTests
{
    [Fact]
    public void Options_validate_throws_on_missing_repository()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SiriusFeedbackOptions { Repository = "no-slash" }.Validate());
        Assert.Contains("owner/repo", ex.Message);
    }

    [Fact]
    public void Options_validate_throws_on_bad_attachments_repo()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SiriusFeedbackOptions { Repository = "o/r", AttachmentsRepository = "no-slash" }.Validate());
        Assert.Contains("owner/repo", ex.Message);
    }

    [Fact]
    public void Sink_inlines_small_text_in_collapsed_block()
    {
        var sink = new GitHubIssuesSink(inlineTextCapBytes: 64 * 1024);
        var ctx  = NewContext();
        var att  = FeedbackAttachment.FromText("note.log", "hello world");
        var req  = new FeedbackRequest
        {
            Title = "t", Body = "body",
            Attachments = new[] { att },
        };
        var uploaded = new[]
        {
            new UploadedAttachment("note.log", att.Size, FeedbackAttachmentKind.Text,
                AttachmentDisposition.Inlined),
        };
        var body = sink.ComposeBody(ctx, req, uploaded);
        Assert.Contains("<details>", body);
        Assert.Contains("hello world", body);
        Assert.Contains("Submitted via Sirius Feedback for MyApp", body);
    }

    [Fact]
    public void Sink_embeds_image_as_inline_markdown()
    {
        var sink = new GitHubIssuesSink(inlineTextCapBytes: 0);
        var ctx  = NewContext();
        var att  = FeedbackAttachment.FromBytes("shot.png", new byte[] { 1, 2, 3 });
        var uploaded = new[]
        {
            new UploadedAttachment("shot.png", att.Size, FeedbackAttachmentKind.Image,
                AttachmentDisposition.Uploaded, Url: "https://raw.example.com/shot.png"),
        };
        var body = sink.ComposeBody(ctx, new FeedbackRequest { Title = "t", Attachments = new[] { att } }, uploaded);
        Assert.Contains("![shot.png](https://raw.example.com/shot.png)", body);
    }

    [Fact]
    public void Sink_lists_omitted_attachments_with_reason()
    {
        var sink = new GitHubIssuesSink(inlineTextCapBytes: 0);
        var ctx  = NewContext();
        var att  = FeedbackAttachment.FromBytes("big.bin", new byte[] { 0 }, FeedbackAttachmentKind.Binary);
        var uploaded = new[]
        {
            new UploadedAttachment("big.bin", 200_000_000, FeedbackAttachmentKind.Binary,
                AttachmentDisposition.Omitted, Reason: "too large: 190 MB > 50 MB"),
        };
        var body = sink.ComposeBody(ctx, new FeedbackRequest { Title = "t", Attachments = new[] { att } }, uploaded);
        Assert.Contains("could not be included", body);
        Assert.Contains("too large", body);
    }

    [Fact]
    public void Sink_chooses_fence_longer_than_any_backtick_run_in_content()
    {
        var sink = new GitHubIssuesSink(inlineTextCapBytes: 64 * 1024);
        var ctx  = NewContext();
        var nasty = "before ``` middle ```` end";   // longest run is 4
        var att   = FeedbackAttachment.FromText("a.md", nasty);
        var uploaded = new[]
        {
            new UploadedAttachment("a.md", att.Size, FeedbackAttachmentKind.Text, AttachmentDisposition.Inlined),
        };
        var body = sink.ComposeBody(ctx, new FeedbackRequest { Title = "t", Attachments = new[] { att } }, uploaded);
        Assert.Contains("`````", body); // 5-backtick fence
    }

    [Fact]
    public async Task Storage_returns_omitted_when_no_token()
    {
        var storage = new GitHubRepoBranchStorage();
        var ctx     = NewContext();
        var att     = FeedbackAttachment.FromBytes("note.txt", new byte[] { 1 });
        var result  = await storage.UploadAsync(new HttpClient(), token: null, ctx,
            new[] { att }, progress: null, CancellationToken.None);
        Assert.Single(result);
        Assert.Equal(AttachmentDisposition.Omitted, result[0].Disposition);
        Assert.Contains("not signed in", result[0].Reason);
    }

    [Fact]
    public async Task Submit_with_headless_ui_returns_filed_issue_using_fake_sink()
    {
        var fakeSink    = new FakeSink();
        var fakeStorage = new InMemoryStorage();
        var feedback = new SiriusFeedback(new SiriusFeedbackOptions
        {
            Repository  = "owner/repo",
            ProductName = "MyApp",
            PersistToken = false,
            Services = new SiriusFeedbackServices
            {
                Sink          = fakeSink,
                Storage       = fakeStorage,
                TokenStore    = new InMemoryTokenStore { /* preload via Save below */ },
                Authenticator = new FakeAuthenticator("seeded-token"),
                Ui            = new HeadlessFeedback(
                    new FeedbackComposeOutcome("My title", "body text", Array.Empty<FeedbackAttachment>())),
            },
        });
        // Preload a token to skip auth UI.
        await feedback.SubmitAsync(new FeedbackRequest()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fakeSink.PostCount);
    }

    [Fact]
    public async Task Submit_classifies_attachments_into_inline_upload_omit()
    {
        var fakeSink    = new FakeSink();
        var fakeStorage = new InMemoryStorage();
        var atts = new[]
        {
            FeedbackAttachment.FromText("small.log", "tiny"),
            FeedbackAttachment.FromBytes("big.bin", new byte[2_000_000], FeedbackAttachmentKind.Binary),
            FeedbackAttachment.FromBytes("huge.bin", new byte[10_000_000], FeedbackAttachmentKind.Binary),
        };
        var feedback = new SiriusFeedback(new SiriusFeedbackOptions
        {
            Repository  = "owner/repo",
            ProductName = "MyApp",
            PersistToken = false,
            InlineTextCapBytes = 1_000_000,
            MaxAttachmentBytes = 5_000_000,   // huge.bin exceeds this
            Services = new SiriusFeedbackServices
            {
                Sink          = fakeSink,
                Storage       = fakeStorage,
                TokenStore    = SeededStore("seeded-token"),
                Authenticator = new FakeAuthenticator("seeded-token"),
                Ui            = new HeadlessFeedback(
                    new FeedbackComposeOutcome("t", "b", atts)),
            },
        });
        var r = await feedback.SubmitAsync(new FeedbackRequest { Attachments = atts });
        Assert.True(r.Success, r.Message);
        Assert.Equal(3, r.Attachments.Count);
        Assert.Equal(AttachmentDisposition.Inlined, r.Attachments[0].Disposition);
        Assert.Equal(AttachmentDisposition.Uploaded, r.Attachments[1].Disposition);
        Assert.Equal(AttachmentDisposition.Omitted,  r.Attachments[2].Disposition);
        Assert.Contains("too large", r.Attachments[2].Reason ?? "");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    static FeedbackContext NewContext() => new(
        Repository:            "owner/repo",
        AttachmentsRepository: "owner/repo",
        AttachmentsBranch:     "feedback-attachments",
        ProductName:           "MyApp",
        SubmissionId:          "abc12345",
        StartedAtUtc:          new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero));

    static ITokenStore SeededStore(string token)
    {
        var s = new InMemoryTokenStore();
        s.SaveAsync(token).GetAwaiter().GetResult();
        return s;
    }

    sealed class FakeSink : IFeedbackSink
    {
        public int PostCount { get; private set; }
        public Task<(string IssueUrl, int IssueNumber)> PostAsync(
            HttpClient http, string? token, FeedbackContext context, FeedbackRequest request,
            IReadOnlyList<UploadedAttachment> uploaded, CancellationToken cancel)
        {
            PostCount++;
            return Task.FromResult(("https://github.example/owner/repo/issues/1", 1));
        }
    }

    sealed class InMemoryStorage : IAttachmentStorage
    {
        public Task<IReadOnlyList<UploadedAttachment>> UploadAsync(
            HttpClient http, string? token, FeedbackContext context,
            IReadOnlyList<FeedbackAttachment> attachments,
            IProgress<FeedbackProgress>? progress, CancellationToken cancel)
        {
            var list = new List<UploadedAttachment>(attachments.Count);
            foreach (var a in attachments)
            {
                list.Add(new UploadedAttachment(a.FileName, a.Size, a.Kind,
                    AttachmentDisposition.Uploaded, Url: $"https://raw.example/{a.FileName}"));
            }
            return Task.FromResult<IReadOnlyList<UploadedAttachment>>(list);
        }
    }

    sealed class FakeAuthenticator : Sirius.Updater.Auth.IAuthenticator
    {
        readonly string _token;
        public FakeAuthenticator(string token) { _token = token; }
        public Task<Sirius.Updater.Auth.AuthenticationResult?> AuthenticateAsync(
            HttpClient http, Sirius.Updater.Ui.IUpdateUi? ui, string? suggestedLogin,
            CancellationToken cancel)
            => Task.FromResult<Sirius.Updater.Auth.AuthenticationResult?>(
                new Sirius.Updater.Auth.AuthenticationResult(_token, UiSession: null));
    }

    sealed class HeadlessFeedback : IFeedbackUi
    {
        readonly FeedbackComposeOutcome _outcome;
        public HeadlessFeedback(FeedbackComposeOutcome outcome) { _outcome = outcome; }
        public Task<IFeedbackUiSession> PresentAsync(FeedbackComposeContext context, CancellationToken cancel)
            => Task.FromResult<IFeedbackUiSession>(new HeadlessFeedbackUiSession(_outcome));
    }
}
