using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sirius.Updater.Feedback;

namespace Sirius.Updater.WinUI.Feedback;

/// <summary>
/// Drop-in titlebar/toolbar button paralleling
/// <see cref="UpdateButton"/>. Single click runs
/// <see cref="SiriusFeedback.SubmitAsync(FeedbackRequest?, IProgress{FeedbackProgress}?, System.Threading.CancellationToken)"/>
/// with the request returned by the optional <see cref="RequestFactory"/>
/// (so consumers can gather logs / screenshots on demand instead of
/// every time the button is rendered).
///
/// Usage:
/// <code>
/// var btn = new FeedbackButton(feedback)
/// {
///     RequestFactory = () => new FeedbackRequest
///     {
///         Attachments = new[]
///         {
///             FeedbackAttachment.FromFile(myAppLogPath, "app.log"),
///         },
///     },
/// };
/// myTitleBar.Children.Add(btn);
/// </code>
/// </summary>
public sealed class FeedbackButton : Button
{
    readonly SiriusFeedback _feedback;
    readonly TeachingTip _tip;
    readonly FontIcon _idleIcon;
    readonly FontIcon _successIcon;
    readonly ProgressRing _spinner;
    DispatcherTimer? _resetTimer;

    public FeedbackButton(SiriusFeedback feedback)
    {
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));

        _idleIcon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph      = "\uED15", // Feedback
            FontSize   = 12,
        };
        _successIcon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph      = "\uE73E", // CheckMark
            FontSize   = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.SeaGreen),
            Visibility = Visibility.Collapsed,
        };
        _spinner = new ProgressRing
        {
            IsActive          = false,
            Width             = 14,
            Height            = 14,
            Visibility        = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var contentGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        contentGrid.Children.Add(_idleIcon);
        contentGrid.Children.Add(_successIcon);
        contentGrid.Children.Add(_spinner);
        Content = contentGrid;

        Padding           = new Thickness(8, 4, 8, 4);
        Background        = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderBrush       = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        VerticalAlignment = VerticalAlignment.Center;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, $"Send feedback about {_feedback.Repository}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, "Sirius_SendFeedback");

        ToolTipService.SetToolTip(this, $"Send feedback ({_feedback.Repository})");

        _tip = new TeachingTip
        {
            Target                = this,
            PreferredPlacement    = TeachingTipPlacementMode.Bottom,
            IsLightDismissEnabled = true,
        };
        Loaded += (_, _) =>
        {
            if (Parent is Panel parent && !parent.Children.Contains(_tip))
                parent.Children.Add(_tip);
        };
        Click += OnClickAsync;
    }

    /// <summary>
    /// Optional callback that builds the request each time the button
    /// is clicked. Use this to attach freshly-collected logs /
    /// screenshots without buffering them eagerly. May return a
    /// completed Task with null to fall back to an empty request
    /// (compose UI gathers everything). Async so callers can await
    /// captures like <c>RenderTargetBitmap.RenderAsync</c>.
    /// </summary>
    public Func<Task<FeedbackRequest?>>? RequestFactory { get; set; }

    /// <summary>The shared TeachingTip — exposed for callers that want
    /// to customise placement, theme, etc.</summary>
    public TeachingTip TeachingTip => _tip;

    async void OnClickAsync(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        SetVisualState(VisualState.Working, "Sending feedback…");
        // IProgress that updates the tooltip + (later) could drive
        // textual progress beside the spinner. Stages mirror the
        // SubmitAsync pipeline: Composing → SigningIn → Uploading → Posting → Done.
        var progress = new Progress<FeedbackProgress>(p =>
        {
            var detail = string.IsNullOrEmpty(p.Detail) ? p.Title : $"{p.Title} — {p.Detail}";
            ToolTipService.SetToolTip(this, detail);
        });
        try
        {
            FeedbackRequest? req = null;
            try
            {
                if (RequestFactory is not null)
                    req = await RequestFactory.Invoke().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _tip.Title    = "Couldn't gather feedback context";
                _tip.Subtitle = ex.Message;
                _tip.IsOpen   = true;
                SetVisualState(VisualState.Idle);
                return;
            }

            var result = await _feedback.SubmitAsync(req, progress).ConfigureAwait(true);
            if (result.Success)
            {
                _tip.Title    = $"Feedback submitted (#{result.IssueNumber})";
                _tip.Subtitle = result.IssueUrl ?? string.Empty;
                _tip.IsOpen   = true;
                SetVisualState(VisualState.Success, $"Feedback submitted (#{result.IssueNumber})");
                ScheduleResetAfter(TimeSpan.FromSeconds(4));
            }
            else if (string.Equals(result.ErrorCode, "USER_CANCELLED", StringComparison.Ordinal) ||
                     string.Equals(result.ErrorCode, "AUTH_CANCELLED", StringComparison.Ordinal))
            {
                // Quiet — don't pop a tip just because the user dismissed.
                SetVisualState(VisualState.Idle);
            }
            else
            {
                _tip.Title    = "Couldn't send feedback";
                _tip.Subtitle = result.Message ?? result.ErrorCode ?? "Unknown error.";
                _tip.IsOpen   = true;
                SetVisualState(VisualState.Idle);
            }
        }
        catch (Exception ex)
        {
            _tip.Title    = "Feedback failed";
            _tip.Subtitle = ex.Message;
            _tip.IsOpen   = true;
            SetVisualState(VisualState.Idle);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    enum VisualState { Idle, Working, Success }

    void SetVisualState(VisualState state, string? toolTip = null)
    {
        _resetTimer?.Stop();
        switch (state)
        {
            case VisualState.Working:
                _idleIcon.Visibility    = Visibility.Collapsed;
                _successIcon.Visibility = Visibility.Collapsed;
                _spinner.Visibility     = Visibility.Visible;
                _spinner.IsActive       = true;
                break;
            case VisualState.Success:
                _spinner.IsActive       = false;
                _spinner.Visibility     = Visibility.Collapsed;
                _idleIcon.Visibility    = Visibility.Collapsed;
                _successIcon.Visibility = Visibility.Visible;
                break;
            default:
                _spinner.IsActive       = false;
                _spinner.Visibility     = Visibility.Collapsed;
                _successIcon.Visibility = Visibility.Collapsed;
                _idleIcon.Visibility    = Visibility.Visible;
                break;
        }
        ToolTipService.SetToolTip(this, toolTip ?? $"Send feedback ({_feedback.Repository})");
    }

    void ScheduleResetAfter(TimeSpan delay)
    {
        _resetTimer?.Stop();
        _resetTimer = new DispatcherTimer { Interval = delay };
        _resetTimer.Tick += (_, _) =>
        {
            _resetTimer?.Stop();
            SetVisualState(VisualState.Idle);
        };
        _resetTimer.Start();
    }
}
