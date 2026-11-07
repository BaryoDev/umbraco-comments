using System.Net;
using System.Net.Sockets;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace BaryoDev.Umbraco.Comments;

[Route("baryo-comments")]
public sealed class CommentsController(CommentSubmission submission, IAntiforgery antiforgery) : Controller
{
    public const string DraftName = "bdc.name";
    public const string DraftEmail = "bdc.email";
    public const string DraftBody = "bdc.body";

    [HttpPost("submit")]
    public async Task<IActionResult> Submit(
        [FromForm] Guid contentKey,
        [FromForm] string? returnUrl,
        [FromForm] string? name,
        [FromForm] string? email,
        [FromForm] string? body,
        [FromForm(Name = "website")] string? honeypot,
        [FromForm(Name = "bdc_t")] string? token)
    {
        string code;
        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
            SubmitResult result = submission.Submit(new CommentInput(contentKey, name, email, body, honeypot, token, ClientKey(HttpContext.Connection.RemoteIpAddress)));
            code = result switch
            {
                SubmitResult.Pending or SubmitResult.Dropped => "pending",
                SubmitResult.Approved => "approved",
                _ => "error-" + result.ToString().ToLowerInvariant(),
            };
        }
        catch (AntiforgeryValidationException)
        {
            code = "error-antiforgery";
        }

        if (code.StartsWith("error-", StringComparison.Ordinal))
        {
            TempData[DraftName] = Cut(name, CommentsOptions.HardNameLimit);
            TempData[DraftEmail] = Cut(email, CommentsOptions.HardEmailLimit);
            TempData[DraftBody] = Cut(body, CommentsOptions.HardBodyLimit);
        }

        string target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        int cut = target.IndexOfAny(['?', '#']);
        string path = cut < 0 ? target : target[..cut];
        return LocalRedirect($"{path}?bdc={code}#comments");
    }

    /// <summary>Cuts to max chars without splitting a surrogate pair.</summary>
    internal static string Cut(string? value, int max)
    {
        if (value is null)
        {
            return string.Empty;
        }
        if (value.Length <= max)
        {
            return value;
        }
        return char.IsHighSurrogate(value[max - 1]) ? value[..(max - 1)] : value[..max];
    }

    /// <summary>IPv4 as is, IPv6 reduced to its /64 so a client rotating inside a prefix shares one bucket.</summary>
    internal static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }
        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}
