using Asp.Versioning;
using BaryoDev.Umbraco.Comments.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

public sealed record CommentItem(Guid Id, Guid ContentKey, string? PageName, string Name, string? Email, string Body, string Status, DateTime CreatedUtc);

public sealed record CommentList(IReadOnlyList<CommentItem> Items, CommentCounts Counts, int Total);

/// <summary>Moderation queue. Content section access plus Browse on each comment's page, checked on the server.</summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("comments")]
[ApiExplorerSettings(GroupName = "Comments")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
public sealed class CommentsApiController(
    ICommentRepository repository,
    IContentService content,
    IContentPermissionService permissions,
    IBackOfficeSecurityAccessor security) : ManagementApiControllerBase
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

        // Rows for pages the user cannot browse are left out. Administrators also see rows whose page is gone.
        // The page is taken before the filter, so a restricted user can get a short page.
        var names = new Dictionary<Guid, string?>();
        var items = new List<CommentItem>();
        foreach (CommentRow r in repository.List(parsed, skip, take))
        {
            if (!await CanSeeAsync(r.ContentKey))
            {
                continue;
            }
            if (!names.TryGetValue(r.ContentKey, out var pageName))
            {
                pageName = content.GetById(r.ContentKey)?.Name;
                names[r.ContentKey] = pageName;
            }
            items.Add(new CommentItem(r.Id, r.ContentKey, pageName, r.Name, r.Email, r.Body,
                ((CommentStatus)r.Status).ToString().ToLowerInvariant(), DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc)));
        }
        var counts = repository.Counts();
        int total = parsed switch { CommentStatus.Pending => counts.Pending, CommentStatus.Approved => counts.Approved, _ => counts.Spam };
        return Ok(new CommentList(items, counts, total));
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Approve(Guid id) => Set(id, CommentStatus.Approved);

    [HttpPost("{id:guid}/spam")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Spam(Guid id) => Set(id, CommentStatus.Spam);

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

    // Fails closed: no user, no access. Rows of deleted pages have no node to check, so only administrators see them.
    private async Task<bool> CanSeeAsync(Guid contentKey)
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
