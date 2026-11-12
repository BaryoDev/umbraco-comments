using Microsoft.AspNetCore.DataProtection;

namespace BaryoDev.Umbraco.Comments.Services;

/// <summary>Signed timestamp and page key carried in the form. The timestamp drives the time-on-form checks, the key ties the token to the page it was issued for.</summary>
public sealed class FormToken(IDataProtectionProvider protection, TimeProvider time)
{
    private readonly IDataProtector _protector = protection.CreateProtector("BaryoDev.Umbraco.Comments.FormToken");

    public string Issue(Guid contentKey) =>
        _protector.Protect(time.GetUtcNow().ToUnixTimeSeconds() + "|" + contentKey.ToString("N"));

    /// <summary>Returns the age of the token, or null if it is missing, not ours, or issued for another page.</summary>
    public TimeSpan? Age(string? token, Guid contentKey)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        try
        {
            string[] parts = _protector.Unprotect(token).Split('|');
            if (parts.Length != 2 || parts[1] != contentKey.ToString("N"))
            {
                return null;
            }
            long issued = long.Parse(parts[0]);
            return time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issued);
        }
        catch (Exception e) when (e is FormatException or OverflowException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
