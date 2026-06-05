using System;
using Microsoft.UI.Xaml;
using Sirius.Updater.Feedback;
using Sirius.Updater.Feedback.Ui;

namespace Sirius.Updater.WinUI.Feedback;

/// <summary>
/// Convenience extensions for wiring Sirius Feedback into a WinUI 3
/// host.
/// </summary>
public static class SiriusFeedbackWinUIExtensions
{
    /// <summary>
    /// Attach a <see cref="ContentDialogFeedbackUi"/> to the given
    /// anchor and return a new feedback instance bound to it.
    /// </summary>
    public static SiriusFeedback WithWinUI(this SiriusFeedback feedback, FrameworkElement anchor)
    {
        if (feedback is null) throw new ArgumentNullException(nameof(feedback));
        if (anchor   is null) throw new ArgumentNullException(nameof(anchor));
        IFeedbackUi ui = ContentDialogFeedbackUi.CreateAndAttach(anchor);
        return feedback.WithUi(ui);
    }
}
