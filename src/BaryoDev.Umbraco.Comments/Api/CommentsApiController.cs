using Asp.Versioning;
using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.Comments.Api;

public sealed record CommentItem(Guid Id, Guid ContentKey, string? PageName, string Name, string? Email, string Body, string Status, DateTime CreatedUtc)
{
    public Guid? ParentId { get; init; }
    public string? ParentName { get; init; }
    /// <summary>True for a reply written by a backoffice user.</summary>
    public bool IsSiteReply { get; init; }
    public DateTime? NotifiedUtc { get; init; }
    public bool CanReply { get; init; }
    public bool CanNotify { get; init; }
}

public sealed record ReplyRequest(string? Body, bool Notify = true);

/// <summary>EmailStatus is the lower case NotifyResult name, or null when no email was asked for.</summary>
public sealed record ReplyResponse(Guid Id, bool Emailed, string? EmailStatus);

public sealed record CommentList(IReadOnlyList<CommentItem> Items, CommentCounts Counts, int Total)
{
    /// <summary>True when a next page may exist. Needed because counts are zero for editors on a custom repository.</summary>
    public bool HasMore { get; init; }
}

/// <summary>Moderation queue. Content section access plus Browse on each comment's page, checked on the server.</summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("comments")]
[ApiExplorerSettings(GroupName = "Comments")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
public sealed class CommentsApiController(
    ICommentRepository repository,
    IContentService content,
    IContentPermissionService permissions,
    IBackOfficeSecurityAccessor security,
    CommentReplies replies,
    IOptions<CommentsOptions> options) : ManagementApiControllerBase
{
    private const int MaxTake = 200;

    [HttpGet]
    [ProducesResponseType<CommentList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string status = "pending", [FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        if (!Enum.TryParse(status, ignoreCase: true, out CommentStatus parsed) || !Enum.IsDefined(parsed))
        {
            return BadRequest(new { error = "status must be pending, approved or spam" });
        }
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxTake);

        // Rows, counts and total cover only the pages the user can browse. Administrators also see rows whose page is gone.
        var names = new Dictionary<Guid, string?>();
        var items = new List<CommentItem>();
        var parents = new Dictionary<Guid, CommentRow?>();
        var (rows, counts, hasMore) = await ModerationQuery.RunAsync(repository, parsed, skip, take, AudienceOfUser(), CanSeeAsync);
        foreach (CommentRow r in rows)
        {
            parents[r.Id] = r;
        }
        bool threaded = repository is ICommentRepositoryThreaded;
        foreach (CommentRow r in rows)
        {
            if (!names.TryGetValue(r.ContentKey, out var pageName))
            {
                pageName = content.GetById(r.ContentKey)?.Name;
                names[r.ContentKey] = pageName;
            }
            CommentRow? parent = null;
            if (r.ParentId is Guid parentId && !parents.TryGetValue(parentId, out parent))
            {
                parent = repository.Get(parentId);
                parents[parentId] = parent;
            }
            items.Add(new CommentItem(r.Id, r.ContentKey, pageName, r.Name, r.Email, r.Body,
                ((CommentStatus)r.Status).ToString().ToLowerInvariant(), DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc))
            {
                ParentId = r.ParentId,
                ParentName = parent?.Name,
                IsSiteReply = r.AuthorUserKey is not null,
                NotifiedUtc = r.NotifiedUtc is DateTime notified ? DateTime.SpecifyKind(notified, DateTimeKind.Utc) : null,
                CanReply = threaded && r.Status == (int)CommentStatus.Approved && r.ParentId is null,
                CanNotify = r.ParentId is not null && r.NotifiedUtc is null && !string.IsNullOrWhiteSpace(parent?.Email),
            });
        }
        int total = parsed switch { CommentStatus.Pending => counts.Pending, CommentStatus.Approved => counts.Approved, _ => counts.Spam };
        return Ok(new CommentList(items, counts, total) { HasMore = hasMore });
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Approve(Guid id) => Set(id, CommentStatus.Approved);

    [HttpPost("{id:guid}/spam")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Spam(Guid id) => Set(id, CommentStatus.Spam);

    [HttpPost("{id:guid}/reply")]
    [ProducesResponseType<ReplyResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reply(Guid id, [FromBody] ReplyRequest request)
    {
        CommentRow? parent = repository.Get(id);
        if (parent is null)
        {
            return NotFound();
        }
        if (!await CanSeeAsync(parent.ContentKey))
        {
            return Forbid();
        }
        IUser? user = security.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Forbid();
        }

        var author = new ReplyAuthor(user.Key, string.IsNullOrWhiteSpace(user.Name) ? user.Username : user.Name);
        ReplyOutcome outcome = await replies.ReplyAsync(id, request.Body, request.Notify, author, HttpContext.RequestAborted);
        switch (outcome.Result)
        {
            case ReplyResult.Saved:
                var response = new ReplyResponse(outcome.ReplyId!.Value, outcome.Email == NotifyResult.Sent, outcome.Email?.ToString().ToLowerInvariant());
                return StatusCode(StatusCodes.Status201Created, response);
            case ReplyResult.NotFound:
                return NotFound();
            case ReplyResult.NotApproved:
                return BadRequest(new { error = "Only approved comments can be replied to." });
            case ReplyResult.IsReply:
                return BadRequest(new { error = "Replies can not be replied to." });
            case ReplyResult.BodyInvalid:
                int max = Math.Min(options.Value.MaxBodyLength, CommentsOptions.HardBodyLimit);
                return BadRequest(new { error = $"Write between {options.Value.MinBodyLength} and {max} characters." });
            default:
                return BadRequest(new { error = "This comment store does not support replies." });
        }
    }

    [HttpPost("{id:guid}/notify")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Notify(Guid id)
    {
        CommentRow? reply = repository.Get(id);
        if (reply is null)
        {
            return NotFound();
        }
        if (!await CanSeeAsync(reply.ContentKey))
        {
            return Forbid();
        }

        NotifyResult result = await replies.NotifyAsync(id, HttpContext.RequestAborted);
        return result switch
        {
            NotifyResult.Sent => NoContent(),
            NotifyResult.NotFound => NotFound(),
            NotifyResult.NotAReply => BadRequest(new { error = "This comment is not a reply." }),
            NotifyResult.NoEmail => BadRequest(new { error = "The commenter left no email address." }),
            NotifyResult.AlreadySent => Conflict(new { error = "This reply was already emailed." }),
            NotifyResult.CannotSend => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Email is not set up on this site." }),
            NotifyResult.Failed => StatusCode(StatusCodes.Status502BadGateway, new { error = "The email could not be sent. Try again later." }),
            _ => BadRequest(new { error = "This comment store does not support replies." }),
        };
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        CommentRow? row = repository.Get(id);
        if (row is null)
        {
            return NotFound();
        }
        if (!await CanSeeAsync(row.ContentKey))
        {
            return Forbid();
        }
        return repository.Delete(id) ? NoContent() : NotFound();
    }

    private async Task<IActionResult> Set(Guid id, CommentStatus status)
    {
        CommentRow? row = repository.Get(id);
        if (row is null)
        {
            return NotFound();
        }
        if (!await CanSeeAsync(row.ContentKey))
        {
            return Forbid();
        }
        return repository.SetStatus(id, status) ? NoContent() : NotFound();
    }

    // Null means every page (administrators). Anyone else gets the pages they can browse, or none without a user.
    private ModerationQuery.Audience AudienceOfUser() =>
        ModerationQuery.AudienceOf(security.BackOfficeSecurity?.CurrentUser?.Groups.Select(g => g.Alias));

    // Fails closed: no user, no access. Rows of deleted pages have no node to check, so only administrators see them.
    private readonly Dictionary<Guid, bool> _seen = [];

    private async Task<bool> CanSeeAsync(Guid contentKey)
    {
        if (_seen.TryGetValue(contentKey, out bool known))
        {
            return known;
        }
        bool allowed = await CheckAsync(contentKey);
        _seen[contentKey] = allowed;
        return allowed;
    }

    private async Task<bool> CheckAsync(Guid contentKey)
    {
        IUser? user = security.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return false;
        }
        if (user.Groups.Any(g => g.Alias == Constants.Security.AdminGroupAlias))
        {
            return true;
        }
        ContentAuthorizationStatus status = await permissions.AuthorizeAccessAsync(user, contentKey, ActionBrowse.ActionLetter.ToString());
        return status == ContentAuthorizationStatus.Success;
    }
}
