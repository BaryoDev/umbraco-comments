namespace BaryoDev.Umbraco.Comments.Data;

/// <summary>
/// Reply support. A repository that does not implement this gets the 0.2.0 behavior: a flat list and no reply button.
/// <see cref="ICommentRepository.ListApproved"/> and <see cref="ICommentRepository.CountApproved"/> still include replies.
/// </summary>
public interface ICommentRepositoryThreaded : ICommentRepository
{
    /// <summary>Approved comments of a page that are not replies, oldest first.</summary>
    IReadOnlyList<CommentRow> ListApprovedTopLevel(Guid contentKey, int skip, int take);
    /// <summary>How many approved comments of a page are not replies.</summary>
    int CountApprovedTopLevel(Guid contentKey);
    /// <summary>Approved replies to the given comments, oldest first.</summary>
    IReadOnlyList<CommentRow> ListReplies(IReadOnlyCollection<Guid> parentIds);
    /// <summary>Marks a reply as emailed. True only for the one caller that changed the row.</summary>
    bool TryClaimNotify(Guid replyId, DateTime utc);
    /// <summary>Clears the emailed mark so the email can be sent again.</summary>
    void ReleaseNotify(Guid replyId);
}
