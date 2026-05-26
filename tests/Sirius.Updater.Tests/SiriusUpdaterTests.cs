using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Auth;
using Sirius.Updater.Hosting;
using Sirius.Updater.Installers;
using Sirius.Updater.Sources;
using Sirius.Updater.TokenStore;
using Sirius.Updater.Ui;
using Xunit;

namespace Sirius.Updater.Tests;

public class SiriusUpdaterTests
{
    [Fact]
    public void Options_validate_throws_on_missing_repository()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SiriusUpdaterOptions { Repository = "no-slash" }.Validate());
        Assert.Contains("owner/repo", ex.Message);
    }

    [Fact]
    public async Task CheckAsync_returns_up_to_date_when_versions_match()
    {
        var release = new UpdateRelease("1.0.0", false, null, new[]
        {
            new UpdateAsset("MyApp_1.0.0_x64.msix", 1234, null, "https://example.com/x.msix"),
        });
        var u = BuildUpdater(release, version: new Version(1, 0, 0, 0));
        var r = await u.CheckAsync();
        Assert.True(r.Success);
        Assert.False(r.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_detects_newer_release_and_picks_matching_asset()
    {
        var release = new UpdateRelease("1.2.3", false, null, new[]
        {
            new UpdateAsset("MyApp_1.2.3_x64.msix", 1234, "https://api/asset/1", null),
            new UpdateAsset("MyApp_1.2.3_ARM64.msix", 1234, "https://api/asset/2", null),
        });
        var u = BuildUpdater(release, version: new Version(1, 0, 0, 0), arch: "x64");
        var r = await u.CheckAsync();
        Assert.True(r.UpdateAvailable);
        Assert.Equal("MyApp_1.2.3_x64.msix", r.AssetName);
        Assert.Equal(new Version(1, 2, 3), r.Latest);
    }

    [Fact]
    public async Task CheckAsync_falls_back_to_first_msix_when_no_pattern_match()
    {
        var release = new UpdateRelease("1.2.3", false, null, new[]
        {
            new UpdateAsset("Differently-Named-1.2.3.msix", 1234, "https://api/asset/1", null),
        });
        var u = BuildUpdater(release, version: new Version(1, 0, 0, 0), arch: "x64");
        var r = await u.CheckAsync();
        Assert.True(r.UpdateAvailable);
        Assert.Equal("Differently-Named-1.2.3.msix", r.AssetName);
    }

    [Fact]
    public async Task CheckAsync_reports_ASSET_NOT_FOUND_when_release_has_no_msix()
    {
        var release = new UpdateRelease("1.2.3", false, null, new[]
        {
            new UpdateAsset("README.txt", 1, "https://api/asset/1", null),
        });
        var u = BuildUpdater(release, version: new Version(1, 0, 0, 0));
        var r = await u.CheckAsync();
        Assert.True(r.UpdateAvailable);     // version IS newer
        Assert.Equal("ASSET_NOT_FOUND", r.Error);
    }

    [Fact]
    public async Task NullUpdateUi_PresentAsync_throws_with_actionable_message()
    {
        var info = new DeviceCodeInfo(
            UserCode:                "ABCD-1234",
            VerificationUri:         "https://github.com/login/device",
            ExpiresIn:               TimeSpan.FromMinutes(15),
            VerificationUriComplete: null,
            SuggestedLogin:          null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NullUpdateUi.Instance.PresentAsync(info, default));

        // Message should point the consumer at the standard remedies so
        // a silent 15-minute hang turns into a discoverable failure.
        Assert.Contains("WithWinUI",                ex.Message);
        Assert.Contains("Services.Ui",              ex.Message);
        Assert.Contains("GH_TOKEN",                 ex.Message);
    }

    [Fact]
    public async Task CheckAsync_surfaces_NullUpdateUi_guard_via_CHECK_FAILED()
    {
        // A source that demands auth + default token store (no tokens) +
        // default NullUpdateUi UI = the exact footgun shape we're guarding
        // against. The updater should bubble a CHECK_FAILED result whose
        // message names the WithWinUI remedy, not a 15-minute hang.
        var u = new SiriusUpdater(new SiriusUpdaterOptions
        {
            Repository  = "owner/private-repo",
            ProductName = "MyApp",
            Services = new SiriusUpdaterServices
            {
                Source     = new AuthRequiredSource(),
                Host       = new FakeHost(new Version(1, 0, 0, 0), "x64"),
                TokenStore = new InMemoryTokenStore(),
                Ui         = NullUpdateUi.Instance,
            },
        });

        var r = await u.CheckAsync();
        Assert.False(r.Success);
        Assert.Equal("CHECK_FAILED", r.Error);
        Assert.Contains("WithWinUI", r.Message ?? string.Empty);
    }

    static SiriusUpdater BuildUpdater(UpdateRelease release, Version version, string arch = "x64")
    {
        return new SiriusUpdater(new SiriusUpdaterOptions
        {
            Repository  = "owner/repo",
            ProductName = "MyApp",
            Services = new SiriusUpdaterServices
            {
                Source     = new FakeSource(release),
                Host       = new FakeHost(version, arch),
                TokenStore = new InMemoryTokenStore(),
                Ui         = NullUpdateUi.Instance,
            },
        });
    }

    sealed class FakeSource : IUpdateSource
    {
        readonly UpdateRelease _release;
        public FakeSource(UpdateRelease release) { _release = release; }
        public string ChannelId       => "fake";
        public string? SuggestedLogin => "owner";
        public Task<UpdateRelease> GetLatestAsync(
            System.Net.Http.HttpClient http, string? token, bool includePreReleases, CancellationToken cancel)
            => Task.FromResult(_release);
        public Uri ResolveAssetDownloadUri(UpdateAsset asset) =>
            new(asset.ApiDownloadUri ?? asset.BrowserDownloadUri ?? "https://example.com/asset");
    }

    sealed class AuthRequiredSource : IUpdateSource
    {
        public string ChannelId       => "auth-required";
        public string? SuggestedLogin => "owner";
        public Task<UpdateRelease> GetLatestAsync(
            System.Net.Http.HttpClient http, string? token, bool includePreReleases, CancellationToken cancel)
            => throw new SourceAuthRequiredException("simulated 404 on private repo");
        public Uri ResolveAssetDownloadUri(UpdateAsset asset) =>
            new("https://example.com/asset");
    }

    sealed class FakeHost : IHostApplication
    {
        public FakeHost(Version version, string arch) { CurrentVersion = version; Architecture = arch; }
        public Version CurrentVersion { get; }
        public string? Aumid          => "FakeApp_xyz!App";
        public bool   IsPackaged      => true;
        public string Architecture    { get; }
    }
}
