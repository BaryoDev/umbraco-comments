using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace BaryoDev.Umbraco.Comments.Services;

/// <summary>
/// Signed timestamp, page key and random nonce carried in the form. The timestamp drives the time-on-form checks, the key ties the token to
/// the page it was issued for, and the nonce lets <see cref="FormToken.Use"/> accept each token once. Used nonces are kept in memory on this
/// instance until the token would have expired, at most 50,000 at a time.
/// </summary>
public enum TokenUse { Accepted, Invalid, Reused, Full }

public sealed class FormToken
{
    public const int DefaultMaxUsedTokens = 50_000;

    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly int _maxUsed;
    private readonly Dictionary<string, DateTimeOffset> _used = new();
    private readonly Lock _gate = new();

    public FormToken(IDataProtectionProvider protection, TimeProvider time) : this(protection, time, DefaultMaxUsedTokens)
    {
    }

    internal FormToken(IDataProtectionProvider protection, TimeProvider time, int maxUsedTokens)
    {
        _protector = protection.CreateProtector("BaryoDev.Umbraco.Comments.FormToken");
        _time = time;
        _maxUsed = maxUsedTokens;
    }

    public string Issue(Guid contentKey) =>
        _protector.Protect(
            _time.GetUtcNow().ToUnixTimeSeconds() + "|" + contentKey.ToString("N") + "|" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

    /// <summary>Returns the age of the token, or null if it is missing, not ours, or issued for another page.</summary>
    public TimeSpan? Age(string? token, Guid contentKey) =>
        Read(token, contentKey) is var (issued, _) ? _time.GetUtcNow() - issued : null;

    /// <summary>
    /// Accepts a token once. Returns <see cref="TokenUse.Reused"/> if it was used before, <see cref="TokenUse.Invalid"/> if it is not valid
    /// for the page, and <see cref="TokenUse.Full"/> if the table of used tokens is full of live entries (it fails closed).
    /// An entry lives until <paramref name="maxAge"/> after the token was issued.
    /// </summary>
    public TokenUse Use(string? token, Guid contentKey, TimeSpan maxAge)
    {
        if (Read(token, contentKey) is not var (issued, nonce))
        {
            return TokenUse.Invalid;
        }
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_used.ContainsKey(nonce))
            {
                return TokenUse.Reused;
            }
            if (_used.Count >= _maxUsed)
            {
                foreach (string key in _used.Where(p => p.Value <= now).Select(p => p.Key).ToList())
                {
                    _used.Remove(key);
                }
                if (_used.Count >= _maxUsed)
                {
                    return TokenUse.Full;
                }
            }
            _used[nonce] = issued + maxAge;
            return TokenUse.Accepted;
        }
    }

    /// <summary>Forgets a used token so it can be sent again. Call it when nothing was saved.</summary>
    public void Release(string? token, Guid contentKey)
    {
        if (Read(token, contentKey) is not var (_, nonce))
        {
            return;
        }
        lock (_gate)
        {
            _used.Remove(nonce);
        }
    }

    internal int UsedCount
    {
        get { lock (_gate) { return _used.Count; } }
    }

    private (DateTimeOffset Issued, string Nonce)? Read(string? token, Guid contentKey)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        try
        {
            string[] parts = _protector.Unprotect(token).Split('|');
            if (parts.Length != 3 || parts[1] != contentKey.ToString("N") || parts[2].Length == 0)
            {
                return null;
            }
            return (DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[0])), parts[2]);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException or CryptographicException)
        {
            return null;
        }
    }
}
