using BaryoDev.Umbraco.Comments.Data;

namespace BaryoDev.Umbraco.Comments.Api;

internal static class ModerationQuery
{
    internal enum Audience { None, Administrator, Editor }

    private static readonly CommentCounts NoCounts = new(0, 0, 0);

    /// <summary>
    /// Administrators get the plain list and counts. An editor on a page-scoped repository gets the scoped queries over the pages
    /// they can browse. An editor on any other repository gets the plain list with each row they can not browse dropped (a short
    /// page, as in 0.1.0) and zero counts. No user gets nothing. A check that throws drops the row.
    /// </summary>
    internal static async Task<(IReadOnlyList<CommentRow> Rows, CommentCounts Counts)> RunAsync(
        ICommentRepository repository, CommentStatus status, int skip, int take, Audience audience, Func<Guid, Task<bool>> canSee)
    {
        switch (audience)
        {
            case Audience.Administrator:
                return (repository.List(status, skip, take), repository.Counts());
            case Audience.Editor when repository is ICommentRepositoryScoped scoped:
                var allowed = new List<Guid>();
                foreach (Guid key in scoped.ContentKeys())
                {
                    if (await Safe(canSee, key))
                    {
                        allowed.Add(key);
                    }
                }
                return (scoped.List(status, skip, take, allowed), scoped.Counts(allowed));
            case Audience.Editor:
                var visible = new List<CommentRow>();
                foreach (CommentRow row in repository.List(status, skip, take))
                {
                    if (await Safe(canSee, row.ContentKey))
                    {
                        visible.Add(row);
                    }
                }
                return (visible, NoCounts);
            default:
                return ([], NoCounts);
        }
    }

    private static async Task<bool> Safe(Func<Guid, Task<bool>> canSee, Guid key)
    {
        try
        {
            return await canSee(key);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
