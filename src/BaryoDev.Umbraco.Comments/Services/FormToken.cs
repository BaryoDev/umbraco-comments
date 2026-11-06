using Microsoft.AspNetCore.DataProtection;

namespace BaryoDev.Umbraco.Comments.Services;

/// <summary>Signed timestamp carried in the form, used for the minimum time-on-form check.</summary>
public sealed class FormToken(IDataProtectionProvider protection, TimeProvider time)
{
    private readonly IDataProtector _protector = protection.CreateProtector("BaryoDev.Umbraco.Comments.FormToken");

    public string Issue() => _protector.Protect(time.GetUtcNow().ToUnixTimeSeconds().ToString());

    /// <summary>Returns the age of the token, or null if it is missing or not ours.</summary>
    public TimeSpan? Age(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        try
        {
            long issued = long.Parse(_protector.Unprotect(token));
            return time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issued);
        }
        catch (Exception e) when (e is FormatException or OverflowException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
