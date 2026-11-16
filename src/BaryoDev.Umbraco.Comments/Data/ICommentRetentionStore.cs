using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.Comments.Data;

/// <summary>Age-based deletion for the retention job. Approved comments can not be deleted through it.</summary>
public interface ICommentRetentionStore
{
    /// <summary>Deletes rows of the given status created before <paramref name="cutoffUtc"/>. Returns how many went.</summary>
    int DeleteOlderThan(CommentStatus status, DateTime cutoffUtc);
}

public sealed class CommentRetentionStore(IScopeProvider scopeProvider) : ICommentRetentionStore
{
    public int DeleteOlderThan(CommentStatus status, DateTime cutoffUtc)
    {
        if (status == CommentStatus.Approved)
        {
            throw new ArgumentException("Approved comments are never deleted by age.", nameof(status));
        }
        using var scope = scopeProvider.CreateScope();
        int n = scope.Database.Execute("DELETE FROM baryoComment WHERE Status = @0 AND CreatedUtc < @1", (int)status, cutoffUtc);
        scope.Complete();
        return n;
    }
}
