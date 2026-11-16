using BaryoDev.Umbraco.Comments.Data;

namespace BaryoDev.Umbraco.Comments.Api;

internal static class ModerationQuery
{
    private static readonly CommentCounts None = new(0, 0, 0);

    /// <summary>
    /// A null <paramref name="allowed"/> means every page (administrators). Otherwise only the scoped queries are used, and a
    /// repository without them returns nothing.
    /// </summary>
    internal static (IReadOnlyList<CommentRow> Rows, CommentCounts Counts) Run(
        ICommentRepository repository, CommentStatus status, int skip, int take, IReadOnlyCollection<Guid>? allowed)
    {
        if (allowed is null)
        {
            return (repository.List(status, skip, take), repository.Counts());
        }
        if (repository is not ICommentRepositoryScoped scoped)
        {
            return ([], None);
        }
        return (scoped.List(status, skip, take, allowed), scoped.Counts(allowed));
    }
}
