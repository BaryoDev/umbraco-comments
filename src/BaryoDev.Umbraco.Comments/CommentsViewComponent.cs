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
        ["error-nosuchpage"] = "This page can not take comments.",
    };

    public IViewComponentResult Invoke(Guid contentKey)
    {
        CommentsOptions o = options.Value;
        var comments = repository.ListApproved(contentKey, o.PageSize)
            .Select(r => new CommentView(r.Name, r.CreatedUtc, r.Body))
            .ToList();

        string? code = Request.Query["bdc"].FirstOrDefault();
        string? notice = code is not null && Messages.TryGetValue(code, out var m) ? m : null;

        string returnUrl = Request.Path.Value ?? "/";
        return View(new CommentsModel(
            contentKey, returnUrl, comments, tokens.Issue(), notice,
            code?.StartsWith("error-", StringComparison.Ordinal) == true,
            o.RequireEmail, o.MaxNameLength, o.MaxEmailLength, Math.Min(o.MaxBodyLength, CommentsOptions.HardBodyLimit)));
    }
}
