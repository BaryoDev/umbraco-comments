using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.Comments.Data;

public sealed class CommentRepository(IScopeProvider scopeProvider) : ICommentRepositoryScoped, ICommentRepositoryThreaded
{
    public void Add(CommentRow row)
    {
        using var scope = scopeProvider.CreateScope();
        scope.Database.Insert(row);
        scope.Complete();
    }

    public IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int skip, int take)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.SkipTake<CommentRow>(skip, take,
            "SELECT * FROM baryoComment WHERE ContentKey = @0 AND Status = @1 ORDER BY CreatedUtc ASC, Id ASC",
            contentKey, (int)CommentStatus.Approved);
    }

    public int CountApproved(Guid contentKey)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM baryoComment WHERE ContentKey = @0 AND Status = @1",
            contentKey, (int)CommentStatus.Approved);
    }

    public int DeleteForContent(Guid contentKey)
    {
        using var scope = scopeProvider.CreateScope();
        int n = scope.Database.Execute("DELETE FROM baryoComment WHERE ContentKey = @0", contentKey);
        scope.Complete();
        return n;
    }

    // SQL Server allows about 2,100 parameters, so a key list goes in batches.
    internal const int KeyBatch = 1000;

    public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.SkipTake<CommentRow>(skip, take,
            "SELECT * FROM baryoComment WHERE Status = @0 ORDER BY CreatedUtc DESC, Id DESC", (int)status);
    }

    public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take, IReadOnlyCollection<Guid> contentKeys)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);

        // Each batch returns its own first skip+take rows in the database's order. The batches are joined in key order and
        // sorted by CreatedUtc alone (a stable sort), so ties keep one fixed order: batch, then the database's order inside it.
        // Each batch's cut follows the same order, so pages never repeat or skip a row, whatever the Id collation.
        var merged = new List<CommentRow>();
        foreach (Guid[] batch in contentKeys.Order().Chunk(KeyBatch))
        {
            merged.AddRange(scope.Database.SkipTake<CommentRow>(0, skip + take,
                "SELECT * FROM baryoComment WHERE Status = @0 AND ContentKey IN (@1) ORDER BY CreatedUtc DESC, Id DESC", (int)status, batch));
        }
        return merged.OrderByDescending(r => r.CreatedUtc).Skip(skip).Take(take).ToList();
    }

    public CommentCounts Counts()
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        int Count(CommentStatus s) => scope.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM baryoComment WHERE Status = @0", (int)s);
        return new CommentCounts(Count(CommentStatus.Pending), Count(CommentStatus.Approved), Count(CommentStatus.Spam));
    }

    public CommentCounts Counts(IReadOnlyCollection<Guid> contentKeys)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        int Count(CommentStatus s) => contentKeys.Chunk(KeyBatch).Sum(batch => scope.Database.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM baryoComment WHERE Status = @0 AND ContentKey IN (@1)", (int)s, batch));
        return new CommentCounts(Count(CommentStatus.Pending), Count(CommentStatus.Approved), Count(CommentStatus.Spam));
    }

    public IReadOnlyList<Guid> ContentKeys()
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.Fetch<Guid>("SELECT DISTINCT ContentKey FROM baryoComment");
    }

    public CommentRow? Get(Guid id)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.FirstOrDefault<CommentRow>("SELECT * FROM baryoComment WHERE Id = @0", id);
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
        scope.Database.Execute("DELETE FROM baryoComment WHERE ParentId = @0", id);
        int n = scope.Database.Execute("DELETE FROM baryoComment WHERE Id = @0", id);
        scope.Complete();
        return n > 0;
    }

    public IReadOnlyList<CommentRow> ListApprovedTopLevel(Guid contentKey, int skip, int take)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.SkipTake<CommentRow>(skip, take,
            "SELECT * FROM baryoComment WHERE ContentKey = @0 AND Status = @1 AND ParentId IS NULL ORDER BY CreatedUtc ASC, Id ASC",
            contentKey, (int)CommentStatus.Approved);
    }

    public int CountApprovedTopLevel(Guid contentKey)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM baryoComment WHERE ContentKey = @0 AND Status = @1 AND ParentId IS NULL",
            contentKey, (int)CommentStatus.Approved);
    }

    public IReadOnlyList<CommentRow> ListReplies(IReadOnlyCollection<Guid> parentIds)
    {
        if (parentIds.Count == 0)
        {
            return [];
        }
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        var merged = new List<CommentRow>();
        foreach (Guid[] batch in parentIds.Distinct().Chunk(KeyBatch))
        {
            merged.AddRange(scope.Database.Fetch<CommentRow>(
                "SELECT * FROM baryoComment WHERE Status = @0 AND ParentId IN (@1)", (int)CommentStatus.Approved, batch));
        }
        return merged.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).ToList();
    }

    public bool TryClaimNotify(Guid replyId, DateTime utc)
    {
        using var scope = scopeProvider.CreateScope();
        int n = scope.Database.Execute(
            "UPDATE baryoComment SET NotifiedUtc = @0 WHERE Id = @1 AND ParentId IS NOT NULL AND NotifiedUtc IS NULL", utc, replyId);
        scope.Complete();
        return n == 1;
    }

    public void ReleaseNotify(Guid replyId)
    {
        using var scope = scopeProvider.CreateScope();
        scope.Database.Execute("UPDATE baryoComment SET NotifiedUtc = NULL WHERE Id = @0", replyId);
        scope.Complete();
    }
}
