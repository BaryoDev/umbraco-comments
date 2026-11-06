using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.Mvc;

namespace BaryoDev.Umbraco.Comments;

[Route("baryo-comments")]
public sealed class CommentsController(CommentSubmission submission) : Controller
{
    [HttpPost("submit")]
    [ValidateAntiForgeryToken]
    public IActionResult Submit(
        [FromForm] Guid contentKey,
        [FromForm] string? returnUrl,
        [FromForm] string? name,
        [FromForm] string? email,
        [FromForm] string? body,
        [FromForm(Name = "website")] string? honeypot,
        [FromForm(Name = "bdc_t")] string? token)
    {
        string client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        SubmitResult result = submission.Submit(new CommentInput(contentKey, name, email, body, honeypot, token, client));

        string code = result switch
        {
            SubmitResult.Pending or SubmitResult.Dropped => "pending",
            SubmitResult.Approved => "approved",
            _ => "error-" + result.ToString().ToLowerInvariant(),
        };

        string target = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        int cut = target.IndexOfAny(['?', '#']);
        string path = cut < 0 ? target : target[..cut];
        return LocalRedirect($"{path}?bdc={code}#comments");
    }
}
