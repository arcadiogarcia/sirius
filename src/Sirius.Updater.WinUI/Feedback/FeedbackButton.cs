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

    public FeedbackButton(SiriusFeedback feedback)
    {
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));

        Content = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph      = "\uED15", // Feedback
            FontSize   = 12,
        };
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
    /// screenshots without buffering them eagerly. Return null to
    /// fall back to an empty request (compose UI gathers everything).
    /// </summary>
    public Func<FeedbackRequest?>? RequestFactory { get; set; }

    /// <summary>The shared TeachingTip — exposed for callers that want
    /// to customise placement, theme, etc.</summary>
    public TeachingTip TeachingTip => _tip;

    async void OnClickAsync(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try
        {
            FeedbackRequest? req = null;
            try { req = RequestFactory?.Invoke(); }
            catch (Exception ex)
            {
                _tip.Title    = "Couldn't gather feedback context";
                _tip.Subtitle = ex.Message;
                _tip.IsOpen   = true;
                return;
            }

            var result = await _feedback.SubmitAsync(req).ConfigureAwait(true);
            if (result.Success)
            {
                _tip.Title    = $"Feedback submitted (#{result.IssueNumber})";
                _tip.Subtitle = result.IssueUrl ?? string.Empty;
            }
            else if (string.Equals(result.ErrorCode, "USER_CANCELLED", StringComparison.Ordinal) ||
                     string.Equals(result.ErrorCode, "AUTH_CANCELLED", StringComparison.Ordinal))
            {
                // Quiet — don't pop a tip just because the user dismissed.
            }
            else
            {
                _tip.Title    = "Couldn't send feedback";
                _tip.Subtitle = result.Message ?? result.ErrorCode ?? "Unknown error.";
                _tip.IsOpen   = true;
            }
        }
        catch (Exception ex)
        {
            _tip.Title    = "Feedback failed";
            _tip.Subtitle = ex.Message;
            _tip.IsOpen   = true;
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
