namespace BaryoDev.Umbraco.Comments.Data;

public sealed record CommentCounts(int Pending, int Approved, int Spam);

public interface ICommentRepository
{
    void Add(CommentRow row);
    IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int take);
    int CountApproved(Guid contentKey);
    int DeleteForContent(Guid contentKey);
    IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take);
    CommentCounts Counts();
    bool SetStatus(Guid id, CommentStatus status);
    bool Delete(Guid id);
}
