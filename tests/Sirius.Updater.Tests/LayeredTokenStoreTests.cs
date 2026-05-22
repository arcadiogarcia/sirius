using System;
using System.Threading.Tasks;
using Sirius.Updater.TokenStore;
using Xunit;

namespace Sirius.Updater.Tests;

public class LayeredTokenStoreTests
{
    [Fact]
    public async Task Load_returns_first_non_null()
    {
        var store = new LayeredTokenStore(new ITokenStore[]
        {
            new InMemoryTokenStore(),
            new FixedTokenStore("env-token"),
        });
        Assert.Equal("env-token", await store.LoadAsync());
    }

    [Fact]
    public async Task Save_propagates_to_writable_layers()
    {
        var mem = new InMemoryTokenStore();
        var store = new LayeredTokenStore(new ITokenStore[] { mem, new FixedTokenStore("env") });
        await store.SaveAsync("written");
        Assert.Equal("written", await mem.LoadAsync());
    }

    [Fact]
    public async Task Load_walks_chain_in_order()
    {
        var store = new LayeredTokenStore(new ITokenStore[]
        {
            new FixedTokenStore(null), // empty mem
            new FixedTokenStore("env-token"),
        });
        Assert.Equal("env-token", await store.LoadAsync());
    }

    sealed class FixedTokenStore : ITokenStore
    {
        readonly string? _token;
        public FixedTokenStore(string? token) { _token = token; }
        public Task<string?> LoadAsync(System.Threading.CancellationToken cancel = default) => Task.FromResult(_token);
        public Task SaveAsync(string token, System.Threading.CancellationToken cancel = default) => Task.CompletedTask;
        public Task ClearAsync(System.Threading.CancellationToken cancel = default) => Task.CompletedTask;
    }
}
