# Release process

Sirius Updater publishes to NuGet via tag-triggered GitHub Actions.

## TL;DR

```bash
git tag v0.2.0
git push origin v0.2.0
```

That single tag push fires `.github/workflows/release.yml`, which:

1. Parses the tag (`v0.2.0` → version `0.2.0`).
2. Stamps the UTC build date into the assembly metadata
   (`SiriusBuildDate`) so the `UpdateButton` tooltip can show it.
3. Restores, builds core (AnyCPU) + WinUI (x64 and ARM64), tests.
4. `dotnet pack`s both projects with the parsed version.
5. Uploads the `.nupkg` / `.snupkg` files as a workflow artifact.
6. Creates a GitHub Release named after the tag with auto-generated
   release notes from the commit log since the previous tag.
7. Publishes both packages to NuGet.org.

## Tag format

```
vMAJOR.MINOR.PATCH            # stable     e.g. v0.2.0
vMAJOR.MINOR.PATCH-suffix     # prerelease e.g. v0.3.0-rc.1
```

The leading `v` is stripped; the rest must match SemVer 2.0. Suffixes
flow into the NuGet package version unchanged; `AssemblyVersion` and
`FileVersion` always come from the strict numeric `MAJOR.MINOR.PATCH`
prefix so they don't trip CS7034/CS7035.

## One-time setup

| Secret              | Where                                | Required for           |
|---------------------|--------------------------------------|------------------------|
| `NUGET_API_KEY`     | repo → Settings → Secrets → Actions  | publishing to NuGet    |

Create the API key at <https://www.nuget.org/account/apikeys> with
scope `Push new packages and package versions` and glob
`Sirius.Updater*`.

## CI

`.github/workflows/ci.yml` runs on every push (except tags) and PR. It
builds, tests, and runs `dotnet pack` with a `ci.<run-number>`
prerelease suffix so packaging breakage is caught before tagging. CI
artifacts are uploaded under the workflow run but **not** pushed
anywhere.

## Republishing a botched release

`--skip-duplicate` is intentionally **not** used on `dotnet nuget push`
— a silent skip on a duplicate version masks "you already pushed a
broken artifact" scenarios. If a release goes out wrong:

1. Unlist the bad version on NuGet.org.
2. Bump the tag (`v0.2.0` → `v0.2.1`) and push again. Never re-cut the
   same version.

## Local dry-run

```pwsh
dotnet pack src/Sirius.Updater/Sirius.Updater.csproj `
  -c Release -p:VersionPrefix=0.2.0 -p:VersionSuffix=dev -o artifacts
dotnet pack src/Sirius.Updater.WinUI/Sirius.Updater.WinUI.csproj `
  -c Release -p:VersionPrefix=0.2.0 -p:VersionSuffix=dev -p:Platform=x64 -o artifacts
```
