using System;
using Microsoft.UI.Xaml;
using Sirius.Updater.Ui;

namespace Sirius.Updater.WinUI;

/// <summary>
/// Convenience extensions for wiring Sirius Updater into a WinUI 3 host.
/// </summary>
public static class SiriusUpdaterWinUIExtensions
{
    /// <summary>
    /// Attach a <see cref="ContentDialogUpdateUi"/> to the given anchor
    /// element and return a new <see cref="SiriusUpdater"/> bound to it.
    /// Use the returned instance instead of the input — the original
    /// stays UI-less.
    /// </summary>
    public static SiriusUpdater WithWinUI(this SiriusUpdater updater, FrameworkElement anchor)
    {
        if (updater is null) throw new ArgumentNullException(nameof(updater));
        if (anchor  is null) throw new ArgumentNullException(nameof(anchor));
        IUpdateUi ui = ContentDialogUpdateUi.CreateAndAttach(anchor);
        return updater.WithUi(ui);
    }
}
