using System.Net;
using System.Net.Sockets;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace BaryoDev.Umbraco.Comments;

[Route("baryo-comments")]
public sealed class CommentsController(CommentSubmission submission, IAntiforgery antiforgery) : Controller
{
    /// <summary>Largest accepted post. A valid comment is a few KB at most.</summary>
    public const int MaxRequestBytes = 32_768;
    public const int MaxFieldBytes = 16_384;

    [HttpPost("submit")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(ValueLengthLimit = MaxFieldBytes, MultipartBodyLengthLimit = MaxRequestBytes)]
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

        string target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        int cut = target.IndexOfAny(['?', '#']);
        string path = cut < 0 ? target : target[..cut];
        return LocalRedirect($"{path}?bdc={code}#comments");
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
