namespace BaryoDev.Umbraco.Comments.Data;

public sealed record CommentCounts(int Pending, int Approved, int Spam);

public interface ICommentRepository
{
    void Add(CommentRow row);
    IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int skip, int take);
    int CountApproved(Guid contentKey);
    int DeleteForContent(Guid contentKey);
    /// <summary>Rows of one status, newest first. A non-null <paramref name="contentKeys"/> limits the rows to those pages.</summary>
    IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take, IReadOnlyCollection<Guid>? contentKeys = null);
    /// <summary>Counts per status. A non-null <paramref name="contentKeys"/> limits the count to those pages.</summary>
    CommentCounts Counts(IReadOnlyCollection<Guid>? contentKeys = null);
    IReadOnlyList<Guid> ContentKeys();
    CommentRow? Get(Guid id);
    bool SetStatus(Guid id, CommentStatus status);
    bool Delete(Guid id);
}
