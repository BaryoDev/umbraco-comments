using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.Comments.Data;

public sealed class CommentRepository(IScopeProvider scopeProvider) : ICommentRepository
{
    public void Add(CommentRow row)
    {
        using var scope = scopeProvider.CreateScope();
        scope.Database.Insert(row);
        scope.Complete();
    }

    public IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int take)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.Fetch<CommentRow>(
            "SELECT * FROM baryoComment WHERE ContentKey = @0 AND Status = @1 ORDER BY CreatedUtc ASC",
            contentKey, (int)CommentStatus.Approved).Take(take).ToList();
    }

    public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.Fetch<CommentRow>(
            "SELECT * FROM baryoComment WHERE Status = @0 ORDER BY CreatedUtc DESC",
            (int)status).Skip(skip).Take(take).ToList();
    }

    public CommentCounts Counts()
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        int Count(CommentStatus s) =>
            scope.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM baryoComment WHERE Status = @0", (int)s);
        return new CommentCounts(Count(CommentStatus.Pending), Count(CommentStatus.Approved), Count(CommentStatus.Spam));
    }

    public bool SetStatus(Guid id, CommentStatus status)
    {
        using var scope = scopeProvider.CreateScope();
        int n = scope.Database.Execute("UPDATE baryoComment SET Status = @0 WHERE Id = @1", (int)status, id);
        scope.Complete();
        return n > 0;
    }

    public bool Delete(Guid id)
    {
        using var scope = scopeProvider.CreateScope();
        int n = scope.Database.Execute("DELETE FROM baryoComment WHERE Id = @0", id);
        scope.Complete();
        return n > 0;
    }
}
