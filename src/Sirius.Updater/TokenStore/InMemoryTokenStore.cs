using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.TokenStore;

/// <summary>
/// Process-local token cache. Holds the freshest token in a volatile
/// field; never touches disk. Always the first link in the
/// <see cref="LayeredTokenStore"/> chain so subsequent calls in the
/// same process don't pay the cost of decrypting DPAPI / reading env.
/// </summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    string? _token;

    public Task<string?> LoadAsync(CancellationToken cancel = default)
        => Task.FromResult(_token);

    public Task SaveAsync(string token, CancellationToken cancel = default)
    {
        _token = token;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancel = default)
    {
        _token = null;
        return Task.CompletedTask;
    }
}
