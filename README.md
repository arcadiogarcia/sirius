# Sirius

[![CI](https://github.com/arcadiogarcia/sirius/actions/workflows/ci.yml/badge.svg)](https://github.com/arcadiogarcia/sirius/actions/workflows/ci.yml)
[![NuGet SiriusUpdater](https://img.shields.io/nuget/v/SiriusUpdater.svg?label=SiriusUpdater)](https://www.nuget.org/packages/SiriusUpdater/)
[![NuGet SiriusUpdater.WinUI](https://img.shields.io/nuget/v/SiriusUpdater.WinUI.svg?label=SiriusUpdater.WinUI)](https://www.nuget.org/packages/SiriusUpdater.WinUI/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> Drop-in self-update **and user-feedback** for sideloaded WinUI 3 / Windows
> App SDK applications, backed by **GitHub Releases** and **GitHub Issues**.
> Public and private repositories. No third-party update framework. No
> installer chrome. No UAC prompts on the happy path. One shared sign-in
> for both update and feedback flows.

---

## What it does

**Sirius Updater** turns a `Check for updates` click into:

1. **Look up** the latest release on GitHub for your `owner/repo`.
2. **Authenticate** if the repo is private — GitHub OAuth Device Flow, using
   the public GitHub-CLI OAuth app by default so users see the familiar
   "GitHub CLI" consent screen and you don't have to register your own.
3. **Cache the token** under DPAPI (CurrentUser) so repeat updates skip
   the device-code dance.
4. **Stream-download** the per-arch MSIX asset, with live progress.
5. **Install + relaunch** silently via `Add-AppxPackage
   -ForceApplicationShutdown` + `shell:AppsFolder\<AUMID>`.

All inside a single `ContentDialog` that morphs from "enter code" to
"downloading 12.3 MiB of 18.4 MiB (66.8%)" to "installing — restarting in a
moment" so users always know an update is in flight.

**Sirius Feedback** turns a `Send feedback` click into:

1. **Compose** dialog — title + multi-line markdown body + a list of
   attachments (each with a preview thumbnail for images and a checkbox
   to opt-out).
2. **Sign in** — reuses the same GitHub token the updater cached, so
   most users skip Device Flow entirely.
3. **Upload attachments** — one commit on a dedicated orphan branch in
   the configured repo. Images render inline in the issue; logs over
   the inline cap link out to the committed file; small text logs are
   inlined as `<details>` blocks.
4. **File the issue** via `POST /repos/{owner/repo}/issues`.
5. **Confirm** with a "View on GitHub" hyperlink and return the
   `IssueUrl` + `IssueNumber` to the calling app.

## Packages

| Package | Purpose |
|---|---|
| [`SiriusUpdater`](src/Sirius.Updater)             | Core library. Zero XAML deps. `SiriusUpdater` + `SiriusFeedback` facades, GitHub release source, Issues sink, attachment storage, Device Flow, DPAPI token cache, MSIX installer, abstractions for everything. |
| [`SiriusUpdater.WinUI`](src/Sirius.Updater.WinUI) | WinUI 3 surface. `ContentDialogUpdateUi` + `UpdateButton` for the updater; `ContentDialogFeedbackUi` + `FeedbackButton` for the feedback flow. |

## Quickstart

### 1. Add the packages

```xml
<ItemGroup>
  <PackageReference Include="SiriusUpdater"       Version="0.1.0" />
  <PackageReference Include="SiriusUpdater.WinUI" Version="0.1.0" />
</ItemGroup>
```

### 2. Stamp your build date (optional, recommended)

So `UpdateButton`'s tooltip can show "Built 2026-05-20 14:32 PDT" alongside
the version. Drop this into your app's csproj:

```xml
<PropertyGroup>
  <SiriusBuildDate Condition="'$(SiriusBuildDate)' == ''">$([System.DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"))</SiriusBuildDate>
</PropertyGroup>
<ItemGroup>
  <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
    <_Parameter1>SiriusBuildDate</_Parameter1>
    <_Parameter2>$(SiriusBuildDate)</_Parameter2>
  </AssemblyAttribute>
</ItemGroup>
```

CI can pin to the commit timestamp with `-p:SiriusBuildDate=...`.

### 3. Wire it up

```csharp
using Sirius.Updater;
using Sirius.Updater.WinUI;

// Once at startup. The MainWindow's root element acts as a XamlRoot anchor.
var updater = new SiriusUpdater(new SiriusUpdaterOptions
{
    Repository  = "contoso/my-app",   // owner/repo on GitHub
    ProductName = "MyApp",            // used in UX strings + MSIX asset name
});
updater = updater.WithWinUI(myRootElement);  // attach the WinUI dialog

// Anywhere you want a "Check for updates" affordance:
var button = new UpdateButton(updater);
myTitleBar.Children.Add(button);
```

That's it — public and private repos both work. On a private repo, the first
click pops up the Device-Flow dialog; the token gets cached, and subsequent
clicks go straight to download + install.

## Configuration reference

All knobs hang off `SiriusUpdaterOptions`:

| Option | Default | Notes |
|---|---|---|
| `Repository` *(required)*    | —                                      | `owner/repo` on GitHub. |
| `ProductName`                | `"Application"`                        | Used in dialog strings. |
| `AssetNamePattern`           | `"{name}_{version}_{arch}.msix"`       | Tokens: `{name}`, `{version}`, `{arch}`. |
| `AssetBaseName`              | `ProductName` (whitespace stripped)    | Override the `{name}` token. |
| `AssetSelector`              | (built-in)                             | Delegate for fully custom asset picking. |
| `OAuthClientId`              | GitHub-CLI public client id            | Use your own OAuth app for branded consent. |
| `OAuthScopes`                | `"repo"`                               | `repo` is the minimum for private downloads. |
| `PersistToken`               | `true`                                 | DPAPI cache. Disable for ephemeral environments. |
| `TokenMaxAge`                | 30 days                                | Defense-in-depth cap on cached tokens. |
| `TokenStorageDirectory`      | `<LocalState>\sirius-updater`          | Inside MSIX sandbox by default. |
| `StagingDirectory`           | `%TEMP%\sirius-updater\<ProductName>`  | Where the MSIX is downloaded to. |
| `EnvironmentTokenVariables`  | `["GH_TOKEN", "GITHUB_TOKEN"]`         | Read in order before falling through to Device Flow. |
| `IncludePreReleases`         | `false`                                | Set true to follow the pre-release channel. |
| `UserAgent`                  | `"SiriusUpdater/<version>"`            | GitHub requires a UA header. |
| `Services.Source`            | `GitHubReleaseSource`                  | Swap for custom backends. |
| `Services.TokenStore`        | layered (memory + env + DPAPI)         | Pass any `ITokenStore`. |
| `Services.Authenticator`     | `GitHubDeviceFlowAuthenticator`        | Pass any `IAuthenticator`. |
| `Services.Installer`         | `MsixAppxInstaller`                    | Pass any `IPackageInstaller`. |
| `Services.Host`              | `PackagedHostApplication`              | Pass for unpackaged / tests. |
| `Services.Ui`                | `NullUpdateUi`                         | The WinUI lib provides `ContentDialogUpdateUi`. |
| `Services.Log`               | `NullUpdaterLog`                       | Bridge to ILogger via `DelegateUpdaterLog`. |

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                         SiriusUpdater                            │
│  (facade — CheckAsync / UpdateAsync / orchestrates everything)   │
└──────────┬──────────────────┬──────────────────┬─────────────────┘
           │                  │                  │
   ┌───────▼───────┐  ┌───────▼───────┐  ┌───────▼─────────┐
   │ IUpdateSource │  │ IAuthenticator│  │ IPackageInstaller│
   │  GitHubRelease│  │ Device Flow   │  │  Add-AppxPackage │
   │     Source    │  │  (gh CLI app) │  │   + relaunch     │
   └───────┬───────┘  └───────┬───────┘  └─────────────────┘
           │                  │
   ┌───────▼───────┐  ┌───────▼───────┐  ┌────────────────┐
   │  ITokenStore  │  │   IUpdateUi   │◀─│ IUpdateUiSession│
   │ Layered:      │  │ ContentDialog │  │ (sign-in        │
   │  · InMemory   │  │ UpdateUi      │  │  → progress)    │
   │  · Env vars   │  └───────────────┘  └────────────────┘
   │  · DPAPI      │
   └───────────────┘
           ▲
   ┌───────┴───────┐
   │IHostApplication│  Package.Current → CurrentVersion / AUMID / Arch
   └───────────────┘
```

Every abstraction is independently swappable. The default wiring covers the
common case (sideloaded MSIX, public OR private GitHub repo) with no
configuration beyond `Repository` + `ProductName`. Tests use the same
interfaces — `FakeSource`, `FakeHost`, `InMemoryTokenStore`, `NullUpdateUi`.

## How private-repo auth works (and why no OAuth app registration is needed)

1. Sirius first tries the cached token (in-memory → env vars → DPAPI on disk).
2. If GitHub returns 401/403/**404** (404 = "token can't see this repo"), it
   wipes the cached token and starts the OAuth Device Flow.
3. The Device Flow uses GitHub CLI's **public** OAuth app id by default
   (`178c6fc778ccc68e1d6a`), so the user sees the familiar "GitHub CLI"
   consent screen. You can override `OAuthClientId` to your own app.
4. The dialog displays the one-time code (Cascadia Mono, 28pt) and opens
   the verification URL — pre-filled with the code AND a `?login=<owner>`
   hint so users with multiple GitHub identities sign in as the right one.
5. On success, the token goes into the DPAPI cache (CurrentUser scope +
   app-specific entropy), so the next update is one click.

The cached token is encrypted with two layers of containment:

- **MSIX sandbox** — file lives in `ApplicationData.Current.LocalFolder`,
  per (package, user). Other MSIX apps and other users can't read it.
- **DPAPI envelope** — even if the file leaks out (backup, sideways copy,
  unpackaged build writing to `%LocalAppData%`), only the same Windows user
  who knows the entropy string can decrypt it. The entropy mixes in your
  `ProductName` so two Sirius-using apps can't read each other's tokens.

Plus a defense-in-depth 30-day max age (configurable).

## How install + restart works

1. Download the per-arch MSIX into the staging dir.
2. Write a tiny PowerShell helper next to it.
3. Launch the helper detached (`powershell.exe -WindowStyle Hidden -File …`)
   and return.
4. The helper sleeps 3s (so the UI can finish painting "Updating"), runs
   `Add-AppxPackage -ForceApplicationShutdown -ForceUpdateFromAnyVersion`
   (which silently installs AND kills the running app), then re-launches via
   `explorer.exe shell:AppsFolder\<AUMID>`.
5. A 5-second fallback `Environment.Exit(0)` in the original process
   guarantees no zombie if AV scanning delays the `ForceApplicationShutdown`.

The whole flow is **silent** — no UAC, no installer chrome, no certificate
prompts — provided the publisher cert is in `LocalMachine\TrustedPeople`.
Your first-install documentation should walk users through trusting it.

## Bridging to ILogger / Serilog

Sirius doesn't take a dependency on `Microsoft.Extensions.Logging`. Bridge in
a one-liner instead:

```csharp
var options = new SiriusUpdaterOptions
{
    Repository = "owner/repo",
    Services = new SiriusUpdaterServices
    {
        Log = new DelegateUpdaterLog((level, category, message, error) =>
            logger.Log(level switch
            {
                "error" => LogLevel.Error,
                "warn"  => LogLevel.Warning,
                _       => LogLevel.Information,
            }, error, "[{Cat}] {Msg}", category, message)),
    },
};
```

## Asset naming convention

The default `AssetNamePattern` is `{name}_{version}_{arch}.msix`. With
`ProductName = "MyApp"` this resolves to e.g. `MyApp_1.2.3_x64.msix` and
`MyApp_1.2.3_ARM64.msix`. Upload one asset per arch you support; the updater
picks the right one for the current process. If none matches, the first
`*.msix` / `*.msixbundle` attached to the release is used as a fallback.

For full control, supply `Options.AssetSelector` — a `Func<UpdateAssetSelectionContext, UpdateAsset?>`.

## Sirius Feedback

A second facade — `SiriusFeedback` — turns user-reported bugs into GitHub
issues with attachments, **sharing the updater's GitHub sign-in** so users
don't have to authenticate twice.

### Quickstart

```csharp
// Reuse the same updater so the cached GitHub token is shared.
var feedback = new SiriusFeedback(new SiriusFeedbackOptions
{
    ProductName            = "MyApp",
    Repository             = "owner/myapp-feedback",   // where issues are filed
    AttachmentsRepository  = "owner/myapp-feedback",   // where blobs are committed
    DefaultLabels          = new[] { "user-feedback" },
})
.SharingAuthWith(updater)   // <-- same DPAPI token, same sign-in
.WithWinUI(myRootElement);  // <-- ContentDialog UI

// Drop-in titlebar button. RequestFactory runs on click so logs are fresh.
var button = new FeedbackButton(feedback)
{
    RequestFactory = () => new FeedbackRequest
    {
        Title       = "",
        Body        = "",
        Attachments =
        {
            FeedbackAttachment.FromFile(@"C:\logs\app.log"),
            FeedbackAttachment.FromFile(latestScreenshotPath),
        },
        Diagnostics =
        {
            ["AppVersion"] = appVersion,
            ["OS"]         = Environment.OSVersion.ToString(),
        },
    },
};
myTitleBar.Children.Add(button);
```

That's it. First click prompts for sign-in (Device Flow), subsequent clicks
go straight to the compose dialog. The user types a title + body, unchecks
any attachments they don't want shared, clicks **Send**, and gets back a
"View on GitHub" link.

### Configuration reference

| Option | Default | Notes |
|---|---|---|
| `Repository` *(required)*    | —                                  | `owner/repo` where issues are filed. Can differ from the app's source repo. |
| `AttachmentsRepository`      | same as `Repository`               | `owner/repo` where attachment blobs are committed. Decouple if you want a separate, mostly-empty repo for blobs. |
| `AttachmentsBranch`          | `"feedback-attachments"`           | Orphan branch holding all attachment commits. Auto-created on first use with a seed README. |
| `ProductName` *(required)*   | —                                  | Used in dialog strings and as part of the DPAPI entropy that shares the token with the updater. **Must match the updater's `ProductName`** for `SharingAuthWith` to pick up the cached token. |
| `BodyTemplate`               | (built-in)                         | Function that renders the issue body from `(request, uploaded)`. Override for fully custom layouts. |
| `DefaultLabels`              | empty                              | Applied to every issue in addition to any per-request labels. |
| `OAuthClientId` / `OAuthScopes` | inherits updater defaults       | Same Device-Flow plumbing as the updater. `repo` scope required to write issues + push blobs on a private repo. |
| `InlineTextMaxBytes`         | 64 KiB                             | Text under this is inlined in the issue body as a `<details>` block. Larger text is uploaded as a file. |
| `PerAttachmentMaxBytes`      | 25 MiB                             | Hard per-file cap. Larger attachments are **omitted** with a visible warning in the issue. |
| `TotalAttachmentMaxBytes`    | 100 MiB                            | Sum cap across all attachments. Excess is omitted with a warning. |
| `Services.Storage`           | `GitHubRepoBranchStorage`          | Pluggable `IAttachmentStorage`. Default commits blobs to `AttachmentsRepository@AttachmentsBranch`. Swap for Azure Blob, S3, internal artifact stores, etc. |
| `Services.Sink`              | `GitHubIssuesSink`                 | Pluggable `IFeedbackSink`. Default POSTs to GitHub Issues. |
| `Services.TokenStore` / `Services.Authenticator` | inherited from updater via `SharingAuthWith` | Or pass explicitly. |
| `Services.Ui`                | `NullFeedbackUi`                   | The WinUI lib provides `ContentDialogFeedbackUi` via `.WithWinUI(anchor)`. |

### How attachments are handled

Each `FeedbackAttachment` gets classified into one of three dispositions:

| Disposition | When | Issue body rendering |
|---|---|---|
| **Inlined**   | Text attachment under `InlineTextMaxBytes`. | Full content in a collapsible `<details>` block. No upload, no commit. |
| **Uploaded**  | Anything else under the per-attachment + total caps. | Committed to the attachments branch; images render inline as `![](raw-url)`, everything else as a link. |
| **Omitted**   | Exceeds caps, user un-checked it in the dialog, or upload failed. | Listed in a warning section with the reason ("too large", "user-omitted", "upload failed: …"). |

Raw URLs use the **immutable commit SHA**, not a branch ref, so they don't
break if the branch is later rewritten.

### Why a dedicated attachments branch?

GitHub doesn't expose `user-attachments` (the drag-and-drop endpoint behind
the issue compose page) as a stable public API, so Sirius can't use it.
Committing blobs via the Contents/Git Data APIs is the only first-party
mechanism that works headlessly and survives — but committing to `main`
would pollute history. The orphan `feedback-attachments` branch is invisible
to anyone browsing `main` yet still serves raw URLs that render in issues.

### Privacy & safety

- **Per-attachment opt-out**: every attachment shows up as a checkbox in
  the compose dialog. Users can untick a log file before sending.
- **Image previews**: image attachments show an inline thumbnail in the
  dialog so users see exactly what's about to be uploaded.
- **No silent uploads**: the attachments commit only happens after the user
  clicks Send.
- **Token scope**: the same `repo` scope as the updater. If you want
  feedback to file into a different repo than the updater pulls from, set
  `Repository` (and `AttachmentsRepository`) accordingly and ensure the
  signed-in user has issue-write access there.

### Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│                        SiriusFeedback                              │
│ (facade — SubmitAsync orchestrates compose → auth → upload → file) │
└──────┬───────────────────┬───────────────────┬────────────────────┘
       │                   │                   │
┌──────▼──────┐    ┌───────▼───────┐    ┌──────▼──────┐
│IFeedbackUi  │    │ITokenStore +  │    │IFeedbackSink│
│ContentDialog│    │IAuthenticator │    │GitHubIssues │
│FeedbackUi   │    │(shared with   │    │Sink         │
│(compose +   │    │ SiriusUpdater)│    │             │
│ result)     │    └───────────────┘    └──────┬──────┘
└─────────────┘                                │
                                       ┌───────▼────────────┐
                                       │IAttachmentStorage  │
                                       │GitHubRepoBranch    │
                                       │Storage             │
                                       │(orphan branch +    │
                                       │ blob+tree+commit)  │
                                       └────────────────────┘
```

## Status

Pre-1.0. The public surface is stable for the use cases above but may
evolve as additional update sources, channels, and rollback flows land.

## Releasing

Tag-driven publish via GitHub Actions — see [docs/RELEASING.md](docs/RELEASING.md).
TL;DR: `git tag v0.2.0 && git push origin v0.2.0` builds, packs, creates a
GitHub Release, and pushes both NuGet packages.

## License

MIT — see [LICENSE](LICENSE).
