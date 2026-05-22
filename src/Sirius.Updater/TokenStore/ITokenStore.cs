using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.TokenStore;

/// <summary>
/// Persistent (or in-memory) store for a GitHub access token. Sirius
/// asks the store before walking the user through the Device Flow, and
/// writes back any newly-acquired token so subsequent runs skip the
/// dance.
///
/// Implementations must be safe to invoke from any thread. They should
/// never throw on read — return null and log warnings instead, so a
/// corrupted cache never blocks the update path.
/// </summary>
public interface ITokenStore
{
    /// <summary>
    /// Returns the cached token if any is available and still valid
    /// (per implementation policy — e.g. MaxAge for DPAPI). Returns null
    /// when nothing usable is cached.
    /// </summary>
    Task<string?> LoadAsync(CancellationToken cancel = default);

    /// <summary>Persist a token. Best-effort — failures are logged
    /// but never thrown.</summary>
    Task SaveAsync(string token, CancellationToken cancel = default);

    /// <summary>Forget the cached token (called after a 401/403/404 with
    /// the cached token in hand).</summary>
    Task ClearAsync(CancellationToken cancel = default);
}
