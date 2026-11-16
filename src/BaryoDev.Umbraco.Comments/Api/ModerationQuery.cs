using BaryoDev.Umbraco.Comments.Data;
using Umbraco.Cms.Core;

namespace BaryoDev.Umbraco.Comments.Api;

internal static class ModerationQuery
{
    internal enum Audience { None, Administrator, Editor }

    private static readonly CommentCounts NoCounts = new(0, 0, 0);

    /// <summary>No groups at all counts as an editor, as in 0.1.0. Only the admin group makes an administrator.</summary>
    internal static Audience AudienceOf(IEnumerable<string>? groupAliases)
    {
        if (groupAliases is null)
        {
            return Audience.None;
        }
        return groupAliases.Contains(Constants.Security.AdminGroupAlias) ? Audience.Administrator : Audience.Editor;
    }

    /// <summary>
    /// Administrators get the plain list and counts. An editor on a page-scoped repository gets the scoped queries over the pages
    /// they can browse. An editor on any other repository gets the plain list with each row they can not browse dropped (a short
    /// page, as in 0.1.0) and zero counts. No user gets nothing. A check that throws drops the row.
    /// </summary>
    internal static async Task<(IReadOnlyList<CommentRow> Rows, CommentCounts Counts, bool HasMore)> RunAsync(
        ICommentRepository repository, CommentStatus status, int skip, int take, Audience audience, Func<Guid, Task<bool>> canSee)
    {
        switch (audience)
        {
            case Audience.Administrator:
                CommentCounts all = repository.Counts();
                return (repository.List(status, skip, take), all, (long)skip + take < Total(all, status));
            case Audience.Editor when repository is ICommentRepositoryScoped scoped:
                var allowed = new List<Guid>();
                foreach (Guid key in scoped.ContentKeys())
                {
                    if (await Safe(canSee, key))
                    {
                        allowed.Add(key);
                    }
                }
                CommentCounts mine = scoped.Counts(allowed);
                return (scoped.List(status, skip, take, allowed), mine, (long)skip + take < Total(mine, status));
            case Audience.Editor:
                var visible = new List<CommentRow>();
                IReadOnlyList<CommentRow> page = repository.List(status, skip, take);
                foreach (CommentRow row in page)
                {
                    if (await Safe(canSee, row.ContentKey))
                    {
                        visible.Add(row);
                    }
                }
                // Counts stay zero, so the page size is the only sign of a next page. It is taken before the filter.
                return (visible, NoCounts, page.Count == take);
            default:
                return ([], NoCounts, false);
        }
    }

    private static int Total(CommentCounts c, CommentStatus status) =>
        status switch { CommentStatus.Pending => c.Pending, CommentStatus.Approved => c.Approved, _ => c.Spam };

    private static async Task<bool> Safe(Func<Guid, Task<bool>> canSee, Guid key)
    {
        try
        {
            return await canSee(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
