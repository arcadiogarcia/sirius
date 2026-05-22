using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Sirius.Updater.Diagnostics;

namespace Sirius.Updater.TokenStore;

/// <summary>
/// On-disk token cache encrypted with Windows DPAPI (CurrentUser scope)
/// plus app-specific entropy. Two layers of containment so a leak of
/// either alone isn't enough to disclose the token:
/// <list type="number">
///   <item><b>Storage location.</b> Inside an MSIX package this should
///         be <c>ApplicationData.Current.LocalFolder</c>, which is per
///         (package, user). The constructor takes the directory as a
///         parameter so non-packaged hosts can opt in too.</item>
///   <item><b>DPAPI envelope.</b> Encrypted bytes can only be decrypted
///         by the same Windows user account, and only after combining
///         with the entropy string. A different user — or any app that
///         doesn't know the entropy — cannot recover the token.</item>
/// </list>
/// Stamped with acquisition time and capped by <see cref="MaxAge"/> as
/// defense in depth: a forgotten token from a decommissioned machine
/// stops working after the cap regardless.
/// </summary>
public sealed class DpapiTokenStore : ITokenStore
{
    readonly string _filePath;
    readonly byte[] _entropy;
    readonly TimeSpan _maxAge;
    readonly IUpdaterLog _log;

    /// <summary>
    /// Construct a DPAPI token store.
    /// </summary>
    /// <param name="directory">Folder to hold the encrypted file. Created on demand.</param>
    /// <param name="fileName">File name (defaults to <c>github-token.bin</c>).</param>
    /// <param name="entropy">App-specific entropy mixed into the DPAPI key derivation. Required.</param>
    /// <param name="maxAge">Defense-in-depth expiry. Defaults to 30 days.</param>
    /// <param name="log">Optional log sink.</param>
    public DpapiTokenStore(
        string directory,
        string entropy,
        string fileName = "github-token.bin",
        TimeSpan? maxAge = null,
        IUpdaterLog? log = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Directory required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(entropy))   throw new ArgumentException("Entropy required.", nameof(entropy));

        _filePath = Path.Combine(directory, fileName);
        _entropy  = Encoding.UTF8.GetBytes(entropy);
        _maxAge   = maxAge ?? TimeSpan.FromDays(30);
        _log      = log    ?? NullUpdaterLog.Instance;
    }

    public TimeSpan MaxAge => _maxAge;

    public Task<string?> LoadAsync(CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult<string?>(null);
        try
        {
            if (!File.Exists(_filePath)) return Task.FromResult<string?>(null);

            var sealedBytes = File.ReadAllBytes(_filePath);
            byte[] plain;
            try
            {
                plain = ProtectedData.Unprotect(sealedBytes, _entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                _log.Warn("dpapi-token-store", $"Unprotect failed — discarding ({ex.Message})", ex);
                TryDelete();
                return Task.FromResult<string?>(null);
            }

            var entry = JsonSerializer.Deserialize<Entry>(plain);
            if (entry is null || string.IsNullOrEmpty(entry.Token))
            {
                TryDelete();
                return Task.FromResult<string?>(null);
            }

            var age = DateTimeOffset.UtcNow - entry.AcquiredAtUtc;
            if (age > _maxAge)
            {
                _log.Info("dpapi-token-store", $"Cached token exceeded MaxAge ({age.TotalDays:0.#}d) — discarding");
                TryDelete();
                return Task.FromResult<string?>(null);
            }

            _log.Info("dpapi-token-store", $"Loaded cached token (age {age.TotalHours:0.#}h)");
            return Task.FromResult<string?>(entry.Token);
        }
        catch (Exception ex)
        {
            _log.Warn("dpapi-token-store", $"Load failed: {ex.Message}", ex);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SaveAsync(string token, CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(token))
            return Task.CompletedTask;
        try
        {
            var entry = new Entry { Token = token, AcquiredAtUtc = DateTimeOffset.UtcNow };
            var plain    = JsonSerializer.SerializeToUtf8Bytes(entry);
            var sealedBs = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            File.WriteAllBytes(tmp, sealedBs);
            TryHardenAcl(tmp);
            if (File.Exists(_filePath))
                File.Replace(tmp, _filePath, destinationBackupFileName: null);
            else
                File.Move(tmp, _filePath);

            _log.Info("dpapi-token-store", "Saved token (DPAPI CurrentUser)");
        }
        catch (Exception ex)
        {
            _log.Warn("dpapi-token-store", $"Save failed: {ex.Message}", ex);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancel = default)
    {
        TryDelete();
        return Task.CompletedTask;
    }

    void TryDelete()
    {
        try { if (File.Exists(_filePath)) File.Delete(_filePath); }
        catch (Exception ex) { _log.Warn("dpapi-token-store", $"Delete failed: {ex.Message}", ex); }
    }

    static void TryHardenAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var fi  = new FileInfo(path);
            var sec = fi.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (sid is not null)
            {
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    sid,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            }
            fi.SetAccessControl(sec);
        }
        catch
        {
            // Best-effort. DPAPI still protects the bytes if some other
            // principal manages to open the file.
        }
    }

    sealed class Entry
    {
        [JsonPropertyName("token")]           public string         Token         { get; set; } = "";
        [JsonPropertyName("acquired_at_utc")] public DateTimeOffset AcquiredAtUtc { get; set; }
    }
}
