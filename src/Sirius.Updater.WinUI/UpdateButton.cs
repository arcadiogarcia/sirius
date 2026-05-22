using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Sirius.Updater.WinUI;

/// <summary>
/// Drop-in titlebar/toolbar button that wires up a one-click
/// "check + install" flow against a <see cref="SiriusUpdater"/>. Mirrors
/// the UX of the original Radiant ⟳ button:
/// <list type="bullet">
///   <item>Single click = check, and if a newer release exists, install
///         (no confirmation dialog — pressing the button is the
///         consent).</item>
///   <item>A <see cref="TeachingTip"/> reflects progress: "Checking…",
///         "You're up to date", "Updating to vX…", or an error.</item>
///   <item>Tooltip shows the current version and (if stamped) the build
///         date.</item>
///   <item>Disabled while a flow is in-flight to prevent double-clicks.</item>
/// </list>
///
/// Usage:
/// <code>
/// var button = new UpdateButton(updater);
/// myTitleBar.Children.Add(button);
/// </code>
/// </summary>
public sealed class UpdateButton : Button
{
    readonly SiriusUpdater _updater;
    readonly FontIcon _icon;
    readonly TeachingTip _tip;

    public UpdateButton(SiriusUpdater updater)
    {
        _updater = updater ?? throw new ArgumentNullException(nameof(updater));

        // Segoe Fluent Icon — "Refresh" / ⟳.
        _icon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph      = "\uE72C",
            FontSize   = 12,
        };

        Content     = _icon;
        Padding     = new Thickness(8, 4, 8, 4);
        Background  = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        VerticalAlignment = VerticalAlignment.Center;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "Check for updates");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, "Sirius_CheckForUpdates");

        ToolTipService.SetToolTip(this, FormatTooltip());

        _tip = new TeachingTip
        {
            Target                = this,
            PreferredPlacement    = TeachingTipPlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Title                 = string.Empty,
            Subtitle              = string.Empty,
        };

        Click += OnClickAsync;
        Loaded += (_, _) =>
        {
            // TeachingTip needs to live in the visual tree to render; we
            // anchor it as a sibling inside the parent panel.
            if (Parent is Panel parent && !parent.Children.Contains(_tip))
                parent.Children.Add(_tip);
        };
    }

    /// <summary>The shared TeachingTip — exposed for callers that want
    /// to customise placement, theme, etc.</summary>
    public TeachingTip TeachingTip => _tip;

    async void OnClickAsync(object sender, RoutedEventArgs e)
    {
        IsEnabled = false;
        try
        {
            _tip.Title    = "Checking for updates…";
            _tip.Subtitle = $"Current version: v{_updater.CurrentVersion}";
            _tip.IsOpen   = true;

            var check = await _updater.CheckAsync().ConfigureAwait(true);
            if (!check.Success)
            {
                _tip.Title    = "Couldn't check for updates";
                _tip.Subtitle = check.Message ?? check.Error ?? "Unknown error.";
                return;
            }
            if (!check.UpdateAvailable)
            {
                _tip.Title    = "You're up to date";
                _tip.Subtitle = $"v{_updater.CurrentVersion} is the latest version.";
                return;
            }

            _tip.Title    = $"Updating to v{check.Latest}…";
            _tip.Subtitle = "The app will close and relaunch automatically.";

            var install = await _updater.UpdateAsync().ConfigureAwait(true);
            if (!install.Success)
            {
                _tip.Title    = "Couldn't install the update";
                _tip.Subtitle = install.Message ?? install.Error ?? "Unknown error.";
            }
        }
        catch (Exception ex)
        {
            _tip.Title    = "Update failed";
            _tip.Subtitle = ex.Message;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    string FormatTooltip()
    {
        var line1 = $"Check for updates (v{_updater.CurrentVersion})";
        if (_updater.BuildDate is { } built)
        {
            var local    = built.ToLocalTime();
            var zoneName = TimeZoneInfo.Local.IsDaylightSavingTime(local)
                ? TimeZoneInfo.Local.DaylightName
                : TimeZoneInfo.Local.StandardName;
            return line1 + "\n" + $"Built {local:yyyy-MM-dd HH:mm} {zoneName}";
        }
        return line1;
    }
}
