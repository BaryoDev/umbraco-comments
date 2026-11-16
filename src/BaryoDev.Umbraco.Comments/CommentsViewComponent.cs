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
    string FormToken,
    string? Notice,
    bool IsError,
    bool RequireEmail,
    int MaxNameLength,
    int MaxEmailLength,
    int MaxBodyLength)
{
    /// <summary>The 0.1.0 constructor. The three draft values are ignored.</summary>
    [Obsolete("The draft lives in sessionStorage now. Use the constructor without the draft values.")]
    public CommentsModel(
        Guid ContentKey, string ReturnUrl, IReadOnlyList<CommentView> Comments, int Total, string? LaterUrl, string? EarlierUrl,
        string DraftName, string DraftEmail, string DraftBody,
        string FormToken, string? Notice, bool IsError, bool RequireEmail, int MaxNameLength, int MaxEmailLength, int MaxBodyLength)
        : this(ContentKey, ReturnUrl, Comments, Total, LaterUrl, EarlierUrl, FormToken, Notice, IsError, RequireEmail,
            MaxNameLength, MaxEmailLength, MaxBodyLength)
    {
    }

    /// <summary>The 0.1.0 deconstruction. The three draft values are empty.</summary>
    [Obsolete("The draft lives in sessionStorage now. Use the deconstruction without the draft values.")]
    public void Deconstruct(
        out Guid ContentKey, out string ReturnUrl, out IReadOnlyList<CommentView> Comments, out int Total, out string? LaterUrl, out string? EarlierUrl,
        out string DraftName, out string DraftEmail, out string DraftBody,
        out string FormToken, out string? Notice, out bool IsError, out bool RequireEmail, out int MaxNameLength, out int MaxEmailLength, out int MaxBodyLength)
    {
        ContentKey = this.ContentKey;
        ReturnUrl = this.ReturnUrl;
        Comments = this.Comments;
        Total = this.Total;
        LaterUrl = this.LaterUrl;
        EarlierUrl = this.EarlierUrl;
        DraftName = string.Empty;
        DraftEmail = string.Empty;
        DraftBody = string.Empty;
        FormToken = this.FormToken;
        Notice = this.Notice;
        IsError = this.IsError;
        RequireEmail = this.RequireEmail;
        MaxNameLength = this.MaxNameLength;
        MaxEmailLength = this.MaxEmailLength;
        MaxBodyLength = this.MaxBodyLength;
    }

    /// <summary>Always empty. The draft lives in the visitor's sessionStorage.</summary>
    [Obsolete("Always empty. The draft lives in sessionStorage now.")]
    public string DraftName { get; init; } = string.Empty;

    /// <summary>Always empty. The draft lives in the visitor's sessionStorage.</summary>
    [Obsolete("Always empty. The draft lives in sessionStorage now.")]
    public string DraftEmail { get; init; } = string.Empty;

    /// <summary>Always empty. The draft lives in the visitor's sessionStorage.</summary>
    [Obsolete("Always empty. The draft lives in sessionStorage now.")]
    public string DraftBody { get; init; } = string.Empty;
}

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
        ["error-antiforgery"] = "The form expired. Send it again.",
        ["error-reused"] = "That comment was already sent.",
        ["error-paused"] = "Comments are paused for review. Try again later.",
        ["error-rejected"] = "The comment could not be sent.",
        ["error-nosuchpage"] = "This page can not take comments.",
    };

    /// <summary>Clamps the configured page size and the requested page, and returns the rows to skip.</summary>
    internal static (int Page, int PageSize, int PageCount, int Skip) Window(int total, int configuredPageSize, string? pageQuery)
    {
        int pageSize = Math.Clamp(configuredPageSize, 1, 500);
        int pageCount = Math.Max(1, (total + pageSize - 1) / pageSize);
        int page = int.TryParse(pageQuery, out int p) ? Math.Clamp(p, 1, pageCount) : 1;
        return (page, pageSize, pageCount, (page - 1) * pageSize);
    }

    public IViewComponentResult Invoke(Guid contentKey)
    {
        CommentsOptions o = options.Value;
        int total = repository.CountApproved(contentKey);
        var (page, pageSize, pageCount, skip) = Window(total, o.PageSize, Request.Query["bdcp"].FirstOrDefault());
        var comments = repository.ListApproved(contentKey, skip, pageSize)
            .Select(r => new CommentView(r.Name, r.CreatedUtc, r.Body))
            .ToList();

        string? code = Request.Query["bdc"].FirstOrDefault();
        string? notice = code is not null && Messages.TryGetValue(code, out var m) ? m : null;

        string path = Request.PathBase + Request.Path;
        string? older = page < pageCount ? $"{path}?bdcp={page + 1}#comments" : null;
        string? newer = page > 1 ? $"{path}?bdcp={page - 1}#comments" : null;

        bool failed = code?.StartsWith("error-", StringComparison.Ordinal) == true;

        string returnUrl = path;
        return View(new CommentsModel(
            contentKey, returnUrl, comments, total, older, newer,
            tokens.Issue(contentKey), notice, failed,
            o.RequireEmail, Math.Min(o.MaxNameLength, CommentsOptions.HardNameLimit), Math.Min(o.MaxEmailLength, CommentsOptions.HardEmailLimit),
            Math.Min(o.MaxBodyLength, CommentsOptions.HardBodyLimit)));
    }
}
