using Asp.Versioning;
using BaryoDev.Umbraco.Comments.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.Comments.Api;

public sealed record CommentItem(Guid Id, Guid ContentKey, string? PageName, string Name, string? Email, string Body, string Status, DateTime CreatedUtc);

public sealed record CommentList(IReadOnlyList<CommentItem> Items, CommentCounts Counts);

/// <summary>Moderation queue. Content section access, checked on the server.</summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("comments")]
[ApiExplorerSettings(GroupName = "Comments")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
public sealed class CommentsApiController(ICommentRepository repository, IContentService content) : ManagementApiControllerBase
{
    private const int MaxTake = 200;

    [HttpGet]
    [ProducesResponseType<CommentList>(StatusCodes.Status200OK)]
    public IActionResult List([FromQuery] string status = "pending", [FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        if (!Enum.TryParse(status, ignoreCase: true, out CommentStatus parsed) || !Enum.IsDefined(parsed))
        {
            return BadRequest(new { error = "status must be pending, approved or spam" });
        }
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxTake);

        var names = new Dictionary<Guid, string?>();
        string? PageName(Guid key)
        {
            if (!names.TryGetValue(key, out var n))
            {
                n = content.GetById(key)?.Name;
                names[key] = n;
            }
            return n;
        }

        var items = repository.List(parsed, skip, take)
            .Select(r => new CommentItem(r.Id, r.ContentKey, PageName(r.ContentKey), r.Name, r.Email, r.Body,
                ((CommentStatus)r.Status).ToString().ToLowerInvariant(), DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc)))
            .ToList();
        return Ok(new CommentList(items, repository.Counts()));
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Approve(Guid id) => Set(id, CommentStatus.Approved);

    [HttpPost("{id:guid}/spam")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Spam(Guid id) => Set(id, CommentStatus.Spam);

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Delete(Guid id) => repository.Delete(id) ? NoContent() : NotFound();

    private IActionResult Set(Guid id, CommentStatus status) => repository.SetStatus(id, status) ? NoContent() : NotFound();
}
