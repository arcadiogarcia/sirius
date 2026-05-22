using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sirius.Updater.TokenStore;

/// <summary>
/// Read-only store backed by environment variables. Honoured for CI
/// builds and devs who deliberately export a PAT. Reads are served from
/// the configured variable list in order; Save and Clear are no-ops —
/// environment management is the caller's job, not ours.
/// </summary>
public sealed class EnvironmentTokenStore : ITokenStore
{
    readonly IReadOnlyList<string> _names;

    public EnvironmentTokenStore(IReadOnlyList<string> variableNames)
    {
        _names = variableNames ?? throw new ArgumentNullException(nameof(variableNames));
    }

    public Task<string?> LoadAsync(CancellationToken cancel = default)
    {
        foreach (var name in _names)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v)) return Task.FromResult<string?>(v.Trim());
        }
        return Task.FromResult<string?>(null);
    }

    public Task SaveAsync(string token, CancellationToken cancel = default) => Task.CompletedTask;
    public Task ClearAsync(CancellationToken cancel = default)              => Task.CompletedTask;
}
