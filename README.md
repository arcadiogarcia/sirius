# Sirius Updater

[![CI](https://github.com/arcadiogarcia/sirius/actions/workflows/ci.yml/badge.svg)](https://github.com/arcadiogarcia/sirius/actions/workflows/ci.yml)
[![NuGet Sirius.Updater](https://img.shields.io/nuget/v/Sirius.Updater.svg?label=Sirius.Updater)](https://www.nuget.org/packages/Sirius.Updater/)
[![NuGet Sirius.Updater.WinUI](https://img.shields.io/nuget/v/Sirius.Updater.WinUI.svg?label=Sirius.Updater.WinUI)](https://www.nuget.org/packages/Sirius.Updater.WinUI/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> Drop-in self-update for sideloaded WinUI 3 / Windows App SDK applications,
> backed by **GitHub Releases**. Public and private repositories. No third-party
> update framework. No installer chrome. No UAC prompts on the happy path.

---

## What it does

Sirius Updater turns a `Check for updates` click into:

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

## Packages

| Package | Purpose |
|---|---|
| [`Sirius.Updater`](src/Sirius.Updater)             | Core library. Zero XAML deps. `SiriusUpdater` facade, GitHub source, Device Flow, DPAPI token cache, MSIX installer, abstractions for everything. |
| [`Sirius.Updater.WinUI`](src/Sirius.Updater.WinUI) | WinUI 3 surface. `ContentDialogUpdateUi` (sign-in + progress) and a drop-in `UpdateButton` titlebar control. |

## Quickstart

### 1. Add the packages

```xml
<ItemGroup>
  <PackageReference Include="Sirius.Updater"       Version="0.1.0" />
  <PackageReference Include="Sirius.Updater.WinUI" Version="0.1.0" />
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

## Status

Pre-1.0. The public surface is stable for the use cases above but may
evolve as additional update sources, channels, and rollback flows land.

## Releasing

Tag-driven publish via GitHub Actions — see [docs/RELEASING.md](docs/RELEASING.md).
TL;DR: `git tag v0.2.0 && git push origin v0.2.0` builds, packs, creates a
GitHub Release, and pushes both NuGet packages.

## License

MIT — see [LICENSE](LICENSE).
