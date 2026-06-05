using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Sirius.Updater.Feedback;
using Sirius.Updater.Feedback.Ui;

namespace Sirius.Updater.WinUI.Feedback;

/// <summary>
/// Live dialog session for <see cref="ContentDialogFeedbackUi"/>.
/// State-machine: Compose → Submitting → Submitted | Error.
/// </summary>
internal sealed class FeedbackDialogSession : IFeedbackUiSession
{
    readonly DispatcherQueue _queue;
    readonly TaskCompletionSource<FeedbackComposeOutcome> _composed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<bool> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    ContentDialog? _dialog;

    // Compose state.
    Panel?    _composeRoot;
    TextBox?  _titleBox;
    TextBox?  _bodyBox;
    StackPanel? _attachmentsPanel;
    List<AttachmentEntry> _attachmentEntries = new();
    FeedbackComposeContext? _context;

    // Submitting state.
    Panel?       _submittingRoot;
    TextBlock?   _submittingTitle;
    TextBlock?   _submittingSubtitle;
    ProgressBar? _submittingBar;

    // Result state.
    Panel?     _resultRoot;
    TextBlock? _resultTitle;
    TextBlock? _resultSubtitle;
    HyperlinkButton? _resultLink;

    bool _composedRaised;

    public FeedbackDialogSession(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public Task<FeedbackComposeOutcome> ComposeAsync(CancellationToken cancel) => _composed.Task;

    public void Build(XamlRoot root, FeedbackComposeContext context, CancellationToken cancel)
    {
        _context = context;
        var stack = new Grid { MinWidth = 460 };
        _composeRoot   = BuildComposePanel(context);
        _submittingRoot = BuildSubmittingPanel();
        _resultRoot    = BuildResultPanel();
        _submittingRoot.Visibility = Visibility.Collapsed;
        _resultRoot.Visibility     = Visibility.Collapsed;
        stack.Children.Add(_composeRoot);
        stack.Children.Add(_submittingRoot);
        stack.Children.Add(_resultRoot);

        _dialog = new ContentDialog
        {
            Title              = $"Send feedback about {context.ProductName}",
            Content            = stack,
            PrimaryButtonText  = "Submit",
            CloseButtonText    = "Cancel",
            DefaultButton      = ContentDialogButton.Primary,
            XamlRoot           = root,
            IsPrimaryButtonEnabled = HasNonEmptyTitle(context.InitialTitle),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_dialog, "Sirius_FeedbackDialog");

        _dialog.PrimaryButtonClick += (s, args) =>
        {
            // Intercept — keep the dialog open by holding the deferral
            // until we've captured input and the caller transitions to
            // the submitting view.
            var deferral = args.GetDeferral();
            try
            {
                var title = _titleBox?.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(title))
                {
                    args.Cancel = true;
                    return;
                }
                var body = _bodyBox?.Text ?? string.Empty;
                // Apply include flags to the source attachments in-place.
                foreach (var entry in _attachmentEntries)
                {
                    entry.Source.Include = entry.IncludeCheckbox.IsChecked == true;
                }
                var outcome = new FeedbackComposeOutcome(title, body,
                    Array.AsReadOnly(_attachmentEntries.ConvertAll(e => e.Source).ToArray()));
                _composedRaised = true;
                _composed.TrySetResult(outcome);
            }
            finally
            {
                deferral.Complete();
            }
        };

        if (cancel.CanBeCanceled)
        {
            cancel.Register(() => _queue.TryEnqueue(() =>
            {
                try { _dialog?.Hide(); } catch { }
            }));
        }

        _ = _dialog.ShowAsync().AsTask().ContinueWith(_ =>
        {
            if (!_composedRaised)
            {
                _composed.TrySetException(new OperationCanceledException("Feedback cancelled."));
            }
            _closed.TrySetResult(true);
        }, TaskScheduler.Default);
    }

    public void FailBuild(Exception ex)
    {
        _composed.TrySetException(ex);
        _closed.TrySetResult(true);
    }

    // ── state transitions ──────────────────────────────────────────────

    public void TransitionToSubmitting(string title, string? subtitle)
    {
        _queue.TryEnqueue(() =>
        {
            try
            {
                if (_dialog is null) return;
                _dialog.PrimaryButtonText        = string.Empty;
                _dialog.IsPrimaryButtonEnabled   = false;
                _dialog.IsSecondaryButtonEnabled = false;
                _dialog.CloseButtonText          = string.Empty;
                _dialog.Title                    = "Submitting feedback";
                if (_composeRoot    is not null) _composeRoot.Visibility    = Visibility.Collapsed;
                if (_resultRoot     is not null) _resultRoot.Visibility     = Visibility.Collapsed;
                if (_submittingRoot is not null) _submittingRoot.Visibility = Visibility.Visible;
                if (_submittingTitle is not null) _submittingTitle.Text     = title ?? string.Empty;
                if (_submittingSubtitle is not null) _submittingSubtitle.Text = subtitle ?? string.Empty;
                if (_submittingBar is not null) _submittingBar.IsIndeterminate = true;
            }
            catch { }
        });
    }

    public void ReportProgress(string? title, string? subtitle, double? percent)
    {
        _queue.TryEnqueue(() =>
        {
            try
            {
                if (_submittingTitle is not null && title is not null) _submittingTitle.Text = title;
                if (_submittingSubtitle is not null && subtitle is not null) _submittingSubtitle.Text = subtitle;
                if (_submittingBar is not null)
                {
                    if (percent is { } p)
                    {
                        _submittingBar.IsIndeterminate = false;
                        _submittingBar.Minimum         = 0;
                        _submittingBar.Maximum         = 100;
                        _submittingBar.Value           = Math.Clamp(p, 0, 100);
                    }
                    else
                    {
                        _submittingBar.IsIndeterminate = true;
                    }
                }
            }
            catch { }
        });
    }

    public void ShowSuccess(string issueUrl, int issueNumber, IReadOnlyList<UploadedAttachment> attachments)
    {
        _queue.TryEnqueue(() =>
        {
            try
            {
                if (_dialog is null) return;
                _dialog.Title              = "Feedback submitted";
                _dialog.CloseButtonText    = "Close";
                _dialog.IsSecondaryButtonEnabled = true;
                if (_composeRoot    is not null) _composeRoot.Visibility    = Visibility.Collapsed;
                if (_submittingRoot is not null) _submittingRoot.Visibility = Visibility.Collapsed;
                if (_resultRoot     is not null) _resultRoot.Visibility     = Visibility.Visible;
                if (_resultTitle is not null)
                    _resultTitle.Text    = $"Thanks! Filed as issue #{issueNumber}.";
                if (_resultSubtitle is not null)
                {
                    var uploaded = 0; var inlined = 0; var omitted = 0;
                    foreach (var a in attachments)
                    {
                        switch (a.Disposition)
                        {
                            case AttachmentDisposition.Uploaded: uploaded++; break;
                            case AttachmentDisposition.Inlined:  inlined++;  break;
                            case AttachmentDisposition.Omitted:  omitted++;  break;
                        }
                    }
                    var bits = new List<string>();
                    if (uploaded > 0) bits.Add($"{uploaded} file(s) uploaded");
                    if (inlined  > 0) bits.Add($"{inlined} inlined");
                    if (omitted  > 0) bits.Add($"{omitted} omitted");
                    _resultSubtitle.Text = bits.Count > 0 ? string.Join(" · ", bits) : "Issue created on GitHub.";
                }
                if (_resultLink is not null)
                {
                    _resultLink.Content    = $"View issue #{issueNumber} on GitHub";
                    _resultLink.NavigateUri = SafeUri(issueUrl);
                    _resultLink.Visibility = Visibility.Visible;
                    _resultLink.Click -= OnOpenIssueLink;
                    _resultLink.Click += OnOpenIssueLink;
                    _resultLink.Tag = issueUrl;
                }
            }
            catch { }
        });
    }

    public void ShowError(string title, string? message)
    {
        _queue.TryEnqueue(() =>
        {
            try
            {
                if (_dialog is null) return;
                _dialog.Title           = title ?? "Feedback failed";
                _dialog.CloseButtonText = "Close";
                _dialog.IsSecondaryButtonEnabled = true;
                _dialog.PrimaryButtonText = string.Empty;
                _dialog.IsPrimaryButtonEnabled = false;
                if (_composeRoot    is not null) _composeRoot.Visibility    = Visibility.Collapsed;
                if (_submittingRoot is not null) _submittingRoot.Visibility = Visibility.Collapsed;
                if (_resultRoot     is not null) _resultRoot.Visibility     = Visibility.Visible;
                if (_resultTitle is not null) _resultTitle.Text    = title    ?? "Feedback failed";
                if (_resultSubtitle is not null) _resultSubtitle.Text = message ?? string.Empty;
                if (_resultLink is not null)
                {
                    _resultLink.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        });
    }

    public Task CloseAsync()
    {
        _queue.TryEnqueue(() =>
        {
            try { _dialog?.Hide(); } catch { }
        });
        return _closed.Task;
    }

    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync().ConfigureAwait(false); } catch { }
    }

    // ── XAML construction ──────────────────────────────────────────────

    Panel BuildComposePanel(FeedbackComposeContext context)
    {
        var stack = new StackPanel { Spacing = 12, MinWidth = 460 };

        // Brief destination note up top — privacy + transparency.
        var dest = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style        = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground   = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        dest.Inlines.Add(new Run { Text = "Filed as a GitHub issue at " });
        dest.Inlines.Add(BuildRepoLink(context.Repository, $"https://github.com/{context.Repository}/issues"));
        if (!string.Equals(context.Repository, context.AttachmentsRepository, StringComparison.OrdinalIgnoreCase))
        {
            dest.Inlines.Add(new Run { Text = ". Attachments uploaded to " });
            dest.Inlines.Add(BuildRepoLink($"{context.AttachmentsRepository} ({context.AttachmentsBranch})",
                $"https://github.com/{context.AttachmentsRepository}/tree/{Uri.EscapeDataString(context.AttachmentsBranch)}"));
            dest.Inlines.Add(new Run { Text = "." });
        }
        else if (context.Attachments.Count > 0)
        {
            dest.Inlines.Add(new Run { Text = " (attachments uploaded to the " });
            dest.Inlines.Add(BuildRepoLink(context.AttachmentsBranch,
                $"https://github.com/{context.AttachmentsRepository}/tree/{Uri.EscapeDataString(context.AttachmentsBranch)}"));
            dest.Inlines.Add(new Run { Text = " branch)." });
        }
        else
        {
            dest.Inlines.Add(new Run { Text = "." });
        }
        stack.Children.Add(dest);

        // Title
        var titleLabel = new TextBlock { Text = "Title", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        stack.Children.Add(titleLabel);
        _titleBox = new TextBox
        {
            Text             = context.InitialTitle ?? string.Empty,
            PlaceholderText  = "Short summary",
            AcceptsReturn    = false,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_titleBox, "Sirius_FeedbackTitle");
        _titleBox.TextChanged += (_, _) =>
        {
            if (_dialog is not null)
                _dialog.IsPrimaryButtonEnabled = HasNonEmptyTitle(_titleBox?.Text);
        };
        stack.Children.Add(_titleBox);

        // Body
        var bodyLabel = new TextBlock
        {
            Text   = "Description",
            Margin = new Thickness(0, 4, 0, 0),
            Style  = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        stack.Children.Add(bodyLabel);
        _bodyBox = new TextBox
        {
            Text             = context.InitialBody ?? string.Empty,
            PlaceholderText  = "Describe what happened, what you expected, and how to reproduce. Supports markdown.",
            AcceptsReturn    = true,
            TextWrapping     = TextWrapping.Wrap,
            MinHeight        = 160,
            MaxHeight        = 260,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_bodyBox, "Sirius_FeedbackBody");
        ScrollViewer.SetVerticalScrollBarVisibility(_bodyBox, ScrollBarVisibility.Auto);
        stack.Children.Add(_bodyBox);

        // Attachments
        if (context.Attachments.Count > 0)
        {
            var attLabel = new TextBlock
            {
                Text   = $"Attachments ({context.Attachments.Count})",
                Margin = new Thickness(0, 4, 0, 0),
                Style  = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            };
            stack.Children.Add(attLabel);

            _attachmentsPanel = new StackPanel { Spacing = 6 };
            foreach (var a in context.Attachments)
            {
                var entry = BuildAttachmentRow(a);
                _attachmentEntries.Add(entry);
                _attachmentsPanel.Children.Add(entry.Container);
            }
            var attScroll = new ScrollViewer
            {
                Content                       = _attachmentsPanel,
                MaxHeight                     = 200,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            stack.Children.Add(attScroll);
        }

        return stack;
    }

    static Hyperlink BuildRepoLink(string text, string url)
    {
        var link = new Hyperlink { NavigateUri = SafeUri(url) };
        link.Inlines.Add(new Run { Text = text });
        return link;
    }

    AttachmentEntry BuildAttachmentRow(FeedbackAttachment a)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var check = new CheckBox { IsChecked = a.Include, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        Grid.SetColumn(check, 0);
        row.Children.Add(check);

        // Thumbnail for images (lazy — best-effort, doesn't block).
        var thumbContainer = new Border
        {
            Width             = 40,
            Height            = 40,
            Margin            = new Thickness(4, 0, 8, 0),
            CornerRadius      = new CornerRadius(4),
            Background        = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (a.Kind == FeedbackAttachmentKind.Image)
        {
            try
            {
                var bytes = a.ReadAllBytes();
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var w = ras.AsStreamForWrite())
                {
                    w.Write(bytes, 0, bytes.Length);
                    w.Flush();
                }
                ras.Seek(0);
                _ = bmp.SetSourceAsync(ras);
                thumbContainer.Child = new Image
                {
                    Source  = bmp,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                };
            }
            catch
            {
                thumbContainer.Child = MakeKindGlyph(a.Kind);
            }
        }
        else
        {
            thumbContainer.Child = MakeKindGlyph(a.Kind);
        }
        Grid.SetColumn(thumbContainer, 1);
        row.Children.Add(thumbContainer);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = a.FileName, TextTrimming = TextTrimming.CharacterEllipsis };
        info.Children.Add(name);
        var meta = new TextBlock
        {
            Text       = BuildMetaLine(a),
            Style      = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        info.Children.Add(meta);
        if (!string.IsNullOrEmpty(a.Description))
        {
            info.Children.Add(new TextBlock
            {
                Text       = a.Description,
                Style      = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }
        Grid.SetColumn(info, 2);
        row.Children.Add(info);

        return new AttachmentEntry(a, check, row);
    }

    static FontIcon MakeKindGlyph(FeedbackAttachmentKind kind) => new()
    {
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        Glyph      = kind switch
        {
            FeedbackAttachmentKind.Image  => "\uEB9F", // image
            FeedbackAttachmentKind.Text   => "\uE8A5", // document
            _                              => "\uE7C3", // file
        },
        FontSize   = 18,
        VerticalAlignment   = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    static string BuildMetaLine(FeedbackAttachment a)
    {
        var kindLabel = a.Kind switch
        {
            FeedbackAttachmentKind.Image  => "image",
            FeedbackAttachmentKind.Text   => "text",
            _                              => "binary",
        };
        return $"{kindLabel} · {FormatBytes(a.Size)}";
    }

    Panel BuildSubmittingPanel()
    {
        var p = new StackPanel { Spacing = 8, MinWidth = 460 };
        _submittingTitle = new TextBlock
        {
            Text = "Submitting…",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        p.Children.Add(_submittingTitle);
        _submittingSubtitle = new TextBlock
        {
            Text = string.Empty,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        p.Children.Add(_submittingSubtitle);
        _submittingBar = new ProgressBar
        {
            IsIndeterminate = true,
            Margin = new Thickness(0, 8, 0, 0),
        };
        p.Children.Add(_submittingBar);
        return p;
    }

    Panel BuildResultPanel()
    {
        var p = new StackPanel { Spacing = 8, MinWidth = 460 };
        _resultTitle = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        p.Children.Add(_resultTitle);
        _resultSubtitle = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        p.Children.Add(_resultSubtitle);
        _resultLink = new HyperlinkButton
        {
            Visibility = Visibility.Collapsed,
            Padding    = new Thickness(0, 4, 0, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_resultLink, "Sirius_FeedbackIssueLink");
        p.Children.Add(_resultLink);
        return p;
    }

    static void OnOpenIssueLink(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton btn && btn.Tag is string url)
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
        }
    }

    static Uri? SafeUri(string url)
    {
        try { return new Uri(url); } catch { return null; }
    }

    static bool HasNonEmptyTitle(string? s) => !string.IsNullOrWhiteSpace(s);

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024)                return $"{bytes} B";
        if (bytes < 1024L * 1024)        return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }

    sealed record AttachmentEntry(FeedbackAttachment Source, CheckBox IncludeCheckbox, FrameworkElement Container);
}
