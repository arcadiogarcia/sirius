# Architecture

> A 5-minute tour of the codebase for new contributors.

## Layering

Sirius is split into two layers so the core can be tested headless and
re-hosted under any UI framework:

```
Sirius.Updater         ← core: no XAML, no WindowsAppSDK XAML deps
   ▲
   │  (project reference)
   │
Sirius.Updater.WinUI   ← WinUI 3 surface: dialogs, drop-in button
```

The core targets `net10.0-windows10.0.19041.0` (for `Package.Current` and
DPAPI). The WinUI layer targets `net10.0-windows10.0.22621.0` and pulls
`Microsoft.WindowsAppSDK`.

## Pipeline

```
   ┌─────────────────────┐
   │  user clicks ⟳      │
   └─────────┬───────────┘
             ▼
   ┌─────────────────────────────────────┐
   │   SiriusUpdater.CheckAsync()         │
   │   - GetReleaseWithAuthAsync()       │
   │     • tries token from store        │
   │     • on 401/403/404 → run auth     │
   │       (Device Flow), retry once     │
   │   - picks an asset                  │
   └─────────┬───────────────────────────┘
             │  if UpdateAvailable && consent
             ▼
   ┌─────────────────────────────────────┐
   │   SiriusUpdater.UpdateAsync()        │
   │   - DownloadAsync()                 │
   │     • streams MSIX to staging       │
   │     • reports progress every ≤150ms │
   │   - IPackageInstaller.LaunchInstall │
   │     • PowerShell helper (detached)  │
   │     • Add-AppxPackage + relaunch    │
   └─────────────────────────────────────┘
```

A single `IUpdateUiSession` instance lives through the whole pipeline so
the user sees uninterrupted feedback: sign-in code → "Preparing…" →
"Downloading 12.3 MiB of 18.4 MiB (66.8%)" → "Installing — restarting in
a moment".

## Files of note

| File | Why it matters |
|---|---|
| `SiriusUpdater.cs`                          | The whole orchestration in ~400 LOC. Start here. |
| `Sources/GitHubReleaseSource.cs`            | API endpoint selection — private-repo asset URI rules. |
| `Auth/GitHubDeviceFlowClient.cs`            | Phase-1 + phase-2 OAuth Device Flow. Pure HTTP. |
| `Auth/GitHubDeviceFlowAuthenticator.cs`     | Races the poll against UI dismissal. |
| `TokenStore/DpapiTokenStore.cs`             | DPAPI envelope, MaxAge cap, ACL hardening. |
| `Installers/MsixAppxInstaller.cs`           | The PowerShell helper that survives our death. |
| `Ui/IUpdateUi.cs`                           | Factory + session contract. |
| `Sirius.Updater.WinUI/DialogSession.cs`     | Dual-panel `ContentDialog` implementation. |
| `Sirius.Updater.WinUI/UpdateButton.cs`      | The drop-in ⟳ titlebar button. |

## Tests

- `SiriusUpdaterTests` — orchestration via fake source/host (no network).
- `DpapiTokenStoreTests` — DPAPI roundtrip, entropy isolation, MaxAge.
- `LayeredTokenStoreTests` — chain semantics.

Run with `dotnet test`.

## Extending

- **New release backend** — implement `IUpdateSource`, pass via
  `Options.Services.Source`.
- **New auth flow** (PAT prompt, SSO) — implement `IAuthenticator`.
- **Alternate token storage** (Credential Manager, Vault) — implement
  `ITokenStore`.
- **Custom UX** (WPF, MAUI, native) — implement `IUpdateUi` /
  `IUpdateUiSession` mirroring `DialogSession`.
