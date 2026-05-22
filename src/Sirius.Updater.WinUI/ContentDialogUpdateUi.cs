using System;
using System.Diagnostics;
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
/// WinUI 3 <see cref="IUpdateUi"/> implementation. Renders a single
/// <see cref="ContentDialog"/> that hosts both:
/// <list type="bullet">
///   <item>A "sign in" panel showing the GitHub Device-Flow user code,
///         a Copy button, an "Open browser" hyperlink, an account hint
///         (e.g. "Sign in as <c>contoso</c> — your organization's
///         GitHub account"), and a spinner.</item>
///   <item>A "progress" panel (initially collapsed) showing a
///         <see cref="ProgressBar"/> and status text. The session
///         swaps panels in place via <see cref="IUpdateUiSession.TransitionToProgress"/>
///         so the dialog never closes-and-reopens — the user has
///         uninterrupted feedback from sign-in through install.</item>
/// </list>
///
/// Construct via <see cref="CreateAndAttach"/> from anywhere with an
/// element that's already in the visual tree; the helper will resolve
/// the <see cref="XamlRoot"/> and capture the UI-thread
/// <see cref="DispatcherQueue"/> for marshalling.
/// </summary>
public sealed class ContentDialogUpdateUi : IUpdateUi
{
    XamlRoot?         _xamlRoot;
    DispatcherQueue?  _uiQueue;

    /// <summary>
    /// Construct a UI bound to the given XAML root. Prefer
    /// <see cref="CreateAndAttach"/> for the common case where you
    /// already have a <see cref="FrameworkElement"/> in the tree.
    /// </summary>
    public ContentDialogUpdateUi(XamlRoot xamlRoot)
    {
        _xamlRoot = xamlRoot ?? throw new ArgumentNullException(nameof(xamlRoot));
        _uiQueue  = DispatcherQueue.GetForCurrentThread();
    }

    ContentDialogUpdateUi() { }

    /// <summary>
    /// Build a UI that latches onto <paramref name="anchor"/>'s
    /// <see cref="XamlRoot"/> once the element is loaded into the
    /// visual tree. Safe to call before <see cref="Window.Activate"/>.
    /// </summary>
    public static ContentDialogUpdateUi CreateAndAttach(FrameworkElement anchor)
    {
        if (anchor is null) throw new ArgumentNullException(nameof(anchor));
        var ui = new ContentDialogUpdateUi();
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

    public Task<IUpdateUiSession> PresentAsync(DeviceCodeInfo info, CancellationToken cancel)
    {
        // Auto-open the verification URL and pre-copy the code — these
        // also benefit users who never see the dialog (no XamlRoot yet).
        var openUri = !string.IsNullOrWhiteSpace(info.VerificationUriComplete)
            ? info.VerificationUriComplete!
            : info.VerificationUri;
        TryOpenBrowser(openUri);
        TryCopyToClipboard(info.UserCode);

        var root  = _xamlRoot;
        var queue = _uiQueue;
        if (root is null || queue is null)
        {
            return Task.FromResult<IUpdateUiSession>(new HeadlessSession(cancel));
        }

        var session = new DialogSession(queue);
        queue.TryEnqueue(() =>
        {
            try    { session.Build(root, info, cancel); }
            catch (Exception ex) { session.FailBuild(ex); }
        });
        return Task.FromResult<IUpdateUiSession>(session);
    }

    // ── shared helpers ───────────────────────────────────────────────────

    internal static bool TryOpenBrowser(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    internal static void TryCopyToClipboard(string value)
    {
        try
        {
            var dp = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(value);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
        catch { /* best effort */ }
    }

    // ── headless fallback session ────────────────────────────────────────

    sealed class HeadlessSession : IUpdateUiSession
    {
        readonly TaskCompletionSource<bool> _dismissed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration _reg;
        public HeadlessSession(CancellationToken cancel)
        {
            _reg = cancel.Register(() =>
                _dismissed.TrySetException(new OperationCanceledException(
                    "Update UI cancelled.", cancel)));
        }
        public Task UserDismissed => _dismissed.Task;
        public void TransitionToProgress(string title, string? subtitle) { }
        public void ReportProgress(string? title, string? subtitle, double? percent) { }
        public Task CloseAsync()        { _reg.Dispose(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _reg.Dispose(); return ValueTask.CompletedTask; }
    }
}
