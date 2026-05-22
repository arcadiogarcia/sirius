using System;
using System.IO;
using Sirius.Updater.TokenStore;
using Xunit;

namespace Sirius.Updater.Tests;

public class DpapiTokenStoreTests : IDisposable
{
    readonly string _dir;

    public DpapiTokenStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sirius-updater-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async System.Threading.Tasks.Task Roundtrip_persists_and_loads_token()
    {
        var store = new DpapiTokenStore(_dir, entropy: "test/entropy/v1");
        await store.SaveAsync("gho_abc123");
        var loaded = await store.LoadAsync();
        Assert.Equal("gho_abc123", loaded);
    }

    [Fact]
    public async System.Threading.Tasks.Task Different_entropy_cannot_decrypt()
    {
        var a = new DpapiTokenStore(_dir, entropy: "app-a/v1");
        await a.SaveAsync("secret-token");
        var b = new DpapiTokenStore(_dir, entropy: "app-b/v1");
        var loaded = await b.LoadAsync();
        Assert.Null(loaded);
    }

    [Fact]
    public async System.Threading.Tasks.Task Clear_removes_cache()
    {
        var store = new DpapiTokenStore(_dir, entropy: "test/entropy/v1");
        await store.SaveAsync("xyz");
        await store.ClearAsync();
        var loaded = await store.LoadAsync();
        Assert.Null(loaded);
    }

    [Fact]
    public async System.Threading.Tasks.Task Expired_token_is_discarded()
    {
        var store = new DpapiTokenStore(_dir, entropy: "test/entropy/v1",
            maxAge: TimeSpan.FromMilliseconds(50));
        await store.SaveAsync("xyz");
        await System.Threading.Tasks.Task.Delay(200);
        var loaded = await store.LoadAsync();
        Assert.Null(loaded);
    }
}
