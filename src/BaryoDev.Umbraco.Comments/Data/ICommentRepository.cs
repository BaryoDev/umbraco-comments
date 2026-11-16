namespace BaryoDev.Umbraco.Comments.Data;

public sealed record CommentCounts(int Pending, int Approved, int Spam);

public interface ICommentRepository
{
    void Add(CommentRow row);
    IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int skip, int take);
    int CountApproved(Guid contentKey);
    int DeleteForContent(Guid contentKey);
    IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take);
    CommentCounts Counts();
    CommentRow? Get(Guid id);
    bool SetStatus(Guid id, CommentStatus status);
    bool Delete(Guid id);
}

/// <summary>
/// Page-scoped queries, used by the dashboard for editors who can browse only some pages and by the per-page pending cap.
/// A repository that does not implement this gets no scoped queries: an editor sees nothing, and the per-page cap uses the total.
/// </summary>
public interface ICommentRepositoryScoped : ICommentRepository
{
    /// <summary>Rows of one status, newest first, limited to the given pages.</summary>
    IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take, IReadOnlyCollection<Guid> contentKeys);
    /// <summary>Counts per status, limited to the given pages.</summary>
    CommentCounts Counts(IReadOnlyCollection<Guid> contentKeys);
    /// <summary>Every page key that has at least one comment.</summary>
    IReadOnlyList<Guid> ContentKeys();
}
