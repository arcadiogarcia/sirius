using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Diagnostics;

namespace Sirius.Updater.TokenStore;

/// <summary>
/// Chains several stores together. Reads walk the chain in order and
/// return the first non-null token; writes/clears fan out to every link.
///
/// The standard configuration is:
/// <list type="number">
///   <item><see cref="InMemoryTokenStore"/> — fast process-local cache.</item>
///   <item><see cref="EnvironmentTokenStore"/> — read-only env vars
///         (PATs from CI / devs).</item>
///   <item><see cref="DpapiTokenStore"/> — DPAPI-encrypted on disk.</item>
/// </list>
/// Writes propagate to the in-memory and DPAPI layers (env vars stay
/// untouched).
/// </summary>
public sealed class LayeredTokenStore : ITokenStore
{
    readonly IReadOnlyList<ITokenStore> _stores;
    readonly IUpdaterLog _log;

    public LayeredTokenStore(IReadOnlyList<ITokenStore> stores, IUpdaterLog? log = null)
    {
        _stores = stores ?? throw new ArgumentNullException(nameof(stores));
        _log    = log    ?? NullUpdaterLog.Instance;
    }

    public async Task<string?> LoadAsync(CancellationToken cancel = default)
    {
        foreach (var store in _stores)
        {
            try
            {
                var t = await store.LoadAsync(cancel).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(t)) return t;
            }
            catch (Exception ex)
            {
                _log.Warn("token-store", $"Load failed for {store.GetType().Name}: {ex.Message}", ex);
            }
        }
        return null;
    }

    public async Task SaveAsync(string token, CancellationToken cancel = default)
    {
        foreach (var store in _stores)
        {
            try { await store.SaveAsync(token, cancel).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.Warn("token-store", $"Save failed for {store.GetType().Name}: {ex.Message}", ex);
            }
        }
    }

    public async Task ClearAsync(CancellationToken cancel = default)
    {
        foreach (var store in _stores)
        {
            try { await store.ClearAsync(cancel).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.Warn("token-store", $"Clear failed for {store.GetType().Name}: {ex.Message}", ex);
            }
        }
    }
}
