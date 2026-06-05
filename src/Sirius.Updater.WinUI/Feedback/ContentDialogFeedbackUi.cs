using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sirius.Updater.Feedback;
using Sirius.Updater.Feedback.Ui;

namespace Sirius.Updater.WinUI.Feedback;

/// <summary>
/// WinUI 3 <see cref="IFeedbackUi"/>. Renders a single
/// <see cref="ContentDialog"/> that morphs through four states:
/// <list type="number">
///   <item><b>Compose</b> — title + multi-line body + attachment list
///         (each with an include checkbox + image thumbnail) + an
///         explicit destination note ("Will be filed at &lt;repo&gt;
///         and attachments uploaded to &lt;repo/branch&gt;").</item>
///   <item><b>Submitting</b> — progress bar + status text.</item>
///   <item><b>Submitted</b> — confirmation with a "View on GitHub"
///         hyperlink to the new issue.</item>
///   <item><b>Error</b> — title + message + Close.</item>
/// </list>
///
/// Construct via <see cref="CreateAndAttach"/> from any element already
/// (or about to be) in the visual tree.
/// </summary>
public sealed class ContentDialogFeedbackUi : IFeedbackUi
{
    XamlRoot?        _xamlRoot;
    DispatcherQueue? _uiQueue;

    public ContentDialogFeedbackUi(XamlRoot xamlRoot)
    {
        _xamlRoot = xamlRoot ?? throw new ArgumentNullException(nameof(xamlRoot));
        _uiQueue  = DispatcherQueue.GetForCurrentThread();
    }

    ContentDialogFeedbackUi() { }

    public static ContentDialogFeedbackUi CreateAndAttach(FrameworkElement anchor)
    {
        if (anchor is null) throw new ArgumentNullException(nameof(anchor));
        var ui = new ContentDialogFeedbackUi();
        void AttachIfReady()
        {
            var root = anchor.XamlRoot;
            if (root is null) return;
            ui._xamlRoot = root;
            ui._uiQueue  = DispatcherQueue.GetForCurrentThread();
        }
        anchor.Loaded += (_, _) => AttachIfReady();
        AttachIfReady();
        return ui;
    }

    public Task<IFeedbackUiSession> PresentAsync(FeedbackComposeContext context, CancellationToken cancel)
    {
        var root  = _xamlRoot;
        var queue = _uiQueue;
        if (root is null || queue is null)
        {
            // Without a XamlRoot we can't show a dialog. Surface immediately.
            throw new InvalidOperationException(
                "ContentDialogFeedbackUi has no XamlRoot yet. Wait for the anchor element's " +
                "Loaded event before invoking SubmitAsync.");
        }
        var session = new FeedbackDialogSession(queue);
        queue.TryEnqueue(() =>
        {
            try    { session.Build(root, context, cancel); }
            catch (Exception ex) { session.FailBuild(ex); }
        });
        return Task.FromResult<IFeedbackUiSession>(session);
    }
}
