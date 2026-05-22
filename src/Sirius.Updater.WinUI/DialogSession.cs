using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sirius.Updater.Auth;
using Sirius.Updater.Ui;

namespace Sirius.Updater.WinUI;

/// <summary>
/// Live dialog session backing <see cref="ContentDialogUpdateUi"/>.
/// Public so consumers can hook the dialog directly in advanced
/// scenarios; intended to be created by the UI factory only.
/// </summary>
internal sealed class DialogSession : IUpdateUiSession
{
    readonly DispatcherQueue _queue;
    readonly TaskCompletionSource<bool> _userDismissed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<bool> _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    ContentDialog? _dialog;
    StackPanel?    _authPanel;
    StackPanel?    _progressPanel;
    TextBlock?     _progressTitle;
    TextBlock?     _progressSubtitle;
    ProgressBar?   _progressBar;
    bool           _inProgressMode;
    bool           _closeRequested;

    public DialogSession(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public Task UserDismissed => _userDismissed.Task;

    public void Build(XamlRoot root, DeviceCodeInfo info, CancellationToken cancel)
    {
        _progressPanel = BuildProgressPanel(out _progressTitle, out _progressSubtitle, out _progressBar);
        _progressPanel.Visibility = Visibility.Collapsed;
        _authPanel = BuildAuthPanel(info);

        var container = new Grid { MinWidth = 380 };
        container.Children.Add(_authPanel);
        container.Children.Add(_progressPanel);

        _dialog = new ContentDialog
        {
            Title           = "Sign in to GitHub",
            Content         = container,
            CloseButtonText = "Cancel",
            DefaultButton   = ContentDialogButton.Close,
            XamlRoot        = root,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_dialog, "Sirius_UpdateDialog");

        if (cancel.CanBeCanceled)
        {
            cancel.Register(() =>
            {
                _queue.TryEnqueue(() =>
                {
                    try { if (!_inProgressMode) _dialog?.Hide(); } catch { }
                });
            });
        }

        _ = _dialog.ShowAsync().AsTask().ContinueWith(_ =>
        {
            if (_closeRequested || _inProgressMode)
            {
                _userDismissed.TrySetResult(true);
            }
            else
            {
                _userDismissed.TrySetException(new OperationCanceledException(
                    "GitHub sign-in cancelled."));
            }
            _closed.TrySetResult(true);
        }, TaskScheduler.Default);
    }

    public void FailBuild(Exception ex)
    {
        _userDismissed.TrySetException(ex);
        _closed.TrySetResult(true);
    }

    public void TransitionToProgress(string title, string? subtitle)
    {
        _queue.TryEnqueue(() =>
        {
            try
            {
                if (_dialog is null) return;
                _inProgressMode = true;
                _dialog.Title           = "Updating";
                _dialog.CloseButtonText = string.Empty;
                _dialog.IsPrimaryButtonEnabled  = false;
                _dialog.IsSecondaryButtonEnabled = false;

                if (_authPanel     is not null) _authPanel.Visibility     = Visibility.Collapsed;
                if (_progressPanel is not null) _progressPanel.Visibility = Visibility.Visible;

                if (_progressTitle    is not null) _progressTitle.Text    = title ?? string.Empty;
                if (_progressSubtitle is not null) _progressSubtitle.Text = subtitle ?? string.Empty;
                if (_progressBar      is not null)
                {
                    _progressBar.IsIndeterminate = true;
                    _progressBar.Value           = 0;
                }
            }
            catch { /* UI updates must never throw */ }
        });
    }

    public void ReportProgress(string? title, string? subtitle, double? percent)
    {
        _queue.TryEnqueue(() =>
        {
            try
            {
                if (!_inProgressMode || _dialog is null) return;
                if (title    is not null && _progressTitle    is not null) _progressTitle.Text    = title;
                if (subtitle is not null && _progressSubtitle is not null) _progressSubtitle.Text = subtitle;
                if (_progressBar is not null)
                {
                    if (percent is { } p)
                    {
                        _progressBar.IsIndeterminate = false;
                        _progressBar.Minimum         = 0;
                        _progressBar.Maximum         = 100;
                        _progressBar.Value           = Math.Clamp(p, 0, 100);
                    }
                    else
                    {
                        _progressBar.IsIndeterminate = true;
                    }
                }
            }
            catch { /* UI updates must never throw */ }
        });
    }

    public Task CloseAsync()
    {
        _closeRequested = true;
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

    // ── XAML building ────────────────────────────────────────────────────

    static StackPanel BuildProgressPanel(out TextBlock title, out TextBlock subtitle, out ProgressBar bar)
    {
        var panel = new StackPanel { Spacing = 0, MinWidth = 380 };
        title = new TextBlock
        {
            Text         = "Working…",
            TextWrapping = TextWrapping.Wrap,
            Style        = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(title, "Sirius_ProgressTitle");
        panel.Children.Add(title);

        subtitle = new TextBlock
        {
            Text         = string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
            Style        = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground   = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(subtitle, "Sirius_ProgressSubtitle");
        panel.Children.Add(subtitle);

        bar = new ProgressBar
        {
            IsIndeterminate = true,
            Minimum         = 0,
            Maximum         = 100,
            Margin          = new Thickness(0, 16, 0, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(bar, "Sirius_ProgressBar");
        panel.Children.Add(bar);
        return panel;
    }

    static StackPanel BuildAuthPanel(DeviceCodeInfo info)
    {
        var content = new StackPanel { Spacing = 0, MinWidth = 380 };

        var description = new TextBlock
        {
            Text         = "This update lives in a private GitHub repository. Sign in with GitHub to continue.",
            TextWrapping = TextWrapping.Wrap,
            Style        = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        };
        content.Children.Add(description);

        if (!string.IsNullOrWhiteSpace(info.SuggestedLogin))
        {
            var hint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 16, 0, 0),
                Style        = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            };
            hint.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Sign in as " });
            hint.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text       = info.SuggestedLogin,
                FontFamily = new FontFamily("Cascadia Mono"),
            });
            hint.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = " — the GitHub account that has access to this repo.",
            });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(hint, "Sirius_LoginHint");
            content.Children.Add(hint);
        }

        var instructions = new TextBlock
        {
            Text         = $"Enter this one-time code at {info.VerificationUri}:",
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 16, 0, 0),
            Style        = (Style)Application.Current.Resources["BodyTextBlockStyle"],
        };
        content.Children.Add(instructions);

        var codeText = new TextBlock
        {
            Text                   = info.UserCode,
            FontFamily             = new FontFamily("Cascadia Mono"),
            FontSize               = 28,
            FontWeight             = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = true,
            HorizontalAlignment    = HorizontalAlignment.Center,
            Margin                 = new Thickness(0, 4, 0, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(codeText, "GitHub one-time code");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(codeText, "Sirius_UserCode");
        content.Children.Add(codeText);

        var copyButton = new Button
        {
            Content             = "Copy code",
            Margin              = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(copyButton, "Sirius_Copy");
        copyButton.Click += (_, _) => ContentDialogUpdateUi.TryCopyToClipboard(info.UserCode);
        content.Children.Add(copyButton);

        var openBrowserButton = new HyperlinkButton
        {
            Content             = "Open browser",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(openBrowserButton, "Sirius_OpenBrowser");
        openBrowserButton.Click += (_, _) =>
        {
            var u = !string.IsNullOrWhiteSpace(info.VerificationUriComplete)
                ? info.VerificationUriComplete!
                : info.VerificationUri;
            if (!ContentDialogUpdateUi.TryOpenBrowser(u) && u != info.VerificationUri)
                ContentDialogUpdateUi.TryOpenBrowser(info.VerificationUri);
        };
        content.Children.Add(openBrowserButton);

        var statusRing = new ProgressRing
        {
            IsActive = true,
            Width    = 16,
            Height   = 16,
            Margin   = new Thickness(0, 0, 8, 0),
        };
        var statusText = new TextBlock
        {
            Text              = "Waiting for authorization in your browser…",
            VerticalAlignment = VerticalAlignment.Center,
            Style             = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground        = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var status = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin              = new Thickness(0, 16, 0, 0),
            Children            = { statusRing, statusText },
        };
        content.Children.Add(status);

        return content;
    }
}
