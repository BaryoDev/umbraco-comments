using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.Comments;

public sealed record CommentView(string Name, DateTime CreatedUtc, string Body);

public sealed record CommentsModel(
    Guid ContentKey,
    string ReturnUrl,
    IReadOnlyList<CommentView> Comments,
    int Total,
    string? LaterUrl,
    string? EarlierUrl,
    string DraftName,
    string DraftEmail,
    string DraftBody,
    string FormToken,
    string? Notice,
    bool IsError,
    bool RequireEmail,
    int MaxNameLength,
    int MaxEmailLength,
    int MaxBodyLength);

/// <summary>Usage: @await Component.InvokeAsync("BaryoComments", new { contentKey = Model.Key })</summary>
[ViewComponent(Name = "BaryoComments")]
public sealed class CommentsViewComponent(ICommentRepository repository, FormToken tokens, IOptions<CommentsOptions> options) : ViewComponent
{
    private static readonly Dictionary<string, string> Messages = new()
    {
        ["pending"] = "Thanks. Your comment is waiting for approval.",
        ["approved"] = "Thanks. Your comment is posted.",
        ["error-toofast"] = "That was quick. Wait a few seconds and send it again.",
        ["error-expired"] = "The form was open too long. Send it again.",
        ["error-invalid"] = "The form could not be checked. Reload the page and try again.",
        ["error-ratelimited"] = "Too many comments from your network. Try again later.",
        ["error-namerequired"] = "Add a name.",
        ["error-emailrequired"] = "Add an email address.",
        ["error-emailinvalid"] = "That email address does not look right.",
        ["error-bodyinvalid"] = "The comment is too short or too long.",
        ["error-antiforgery"] = "The form expired. Your text is still here, send it again.",
        ["error-nosuchpage"] = "This page can not take comments.",
    };

    public IViewComponentResult Invoke(Guid contentKey)
    {
        CommentsOptions o = options.Value;
        int pageSize = Math.Clamp(o.PageSize, 1, 500);
        int total = repository.CountApproved(contentKey);
        int pageCount = Math.Max(1, (total + pageSize - 1) / pageSize);
        int page = int.TryParse(Request.Query["bdcp"].FirstOrDefault(), out int p) ? Math.Clamp(p, 1, pageCount) : 1;
        var comments = repository.ListApproved(contentKey, (page - 1) * pageSize, pageSize)
            .Select(r => new CommentView(r.Name, r.CreatedUtc, r.Body))
            .ToList();

        string? code = Request.Query["bdc"].FirstOrDefault();
        string? notice = code is not null && Messages.TryGetValue(code, out var m) ? m : null;

        string path = Request.PathBase + Request.Path;
        string? older = page < pageCount ? $"{path}?bdcp={page + 1}#comments" : null;
        string? newer = page > 1 ? $"{path}?bdcp={page - 1}#comments" : null;

        bool failed = code?.StartsWith("error-", StringComparison.Ordinal) == true;
        string Draft(string key) => failed && TempData[key] is string s ? s : string.Empty;

        string returnUrl = path;
        return View(new CommentsModel(
            contentKey, returnUrl, comments, total, older, newer,
            Draft(CommentsController.DraftName), Draft(CommentsController.DraftEmail), Draft(CommentsController.DraftBody),
            tokens.Issue(), notice, failed,
            o.RequireEmail, Math.Min(o.MaxNameLength, CommentsOptions.HardNameLimit), Math.Min(o.MaxEmailLength, CommentsOptions.HardEmailLimit),
            Math.Min(o.MaxBodyLength, CommentsOptions.HardBodyLimit)));
    }
}
