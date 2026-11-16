using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BaryoDev.Umbraco.Comments.Tests;

public class RetentionTests
{
    private sealed class FakeStore : ICommentRetentionStore
    {
        public List<(CommentStatus Status, DateTime CreatedUtc)> Rows { get; } = [];

        public int DeleteOlderThan(CommentStatus status, DateTime cutoffUtc)
        {
            if (status == CommentStatus.Approved)
            {
                throw new ArgumentException("approved");
            }
            return Rows.RemoveAll(r => r.Status == status && r.CreatedUtc < cutoffUtc);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static FakeStore Seed()
    {
        var store = new FakeStore();
        foreach (var status in new[] { CommentStatus.Pending, CommentStatus.Spam, CommentStatus.Approved })
        {
            store.Rows.Add((status, Now.UtcDateTime.AddDays(-100)));
            store.Rows.Add((status, Now.UtcDateTime.AddDays(-10)));
            store.Rows.Add((status, Now.UtcDateTime.AddDays(-1)));
        }
        return store;
    }

    private static int Run(FakeStore store, CommentsOptions options) =>
        new CommentsRetention(store, Options.Create(options), new FakeTimeProvider(Now)).RunOnce();

    [Fact]
    public void Nothing_is_deleted_when_both_settings_are_null()
    {
        var store = Seed();
        Assert.Equal(0, Run(store, new CommentsOptions()));
        Assert.Equal(9, store.Rows.Count);
    }

    [Fact]
    public void Spam_retention_deletes_only_old_spam()
    {
        var store = Seed();
        Assert.Equal(2, Run(store, new CommentsOptions { SpamRetentionDays = 5 }));
        Assert.Equal(1, store.Rows.Count(r => r.Status == CommentStatus.Spam));
        Assert.Equal(3, store.Rows.Count(r => r.Status == CommentStatus.Pending));
        Assert.Equal(3, store.Rows.Count(r => r.Status == CommentStatus.Approved));
    }

    [Fact]
    public void Pending_retention_deletes_only_old_pending()
    {
        var store = Seed();
        Assert.Equal(1, Run(store, new CommentsOptions { PendingRetentionDays = 30 }));
        Assert.Equal(2, store.Rows.Count(r => r.Status == CommentStatus.Pending));
        Assert.Equal(3, store.Rows.Count(r => r.Status == CommentStatus.Spam));
        Assert.Equal(3, store.Rows.Count(r => r.Status == CommentStatus.Approved));
    }

    [Fact]
    public void Approved_comments_survive_any_setting()
    {
        var store = Seed();
        Run(store, new CommentsOptions { SpamRetentionDays = 1, PendingRetentionDays = 1 });
        Assert.Equal(3, store.Rows.Count(r => r.Status == CommentStatus.Approved));
        Assert.DoesNotContain(store.Rows, r => r.Status != CommentStatus.Approved && r.CreatedUtc < Now.UtcDateTime.AddDays(-1));
    }

    [Fact]
    public void The_store_refuses_to_delete_approved_rows()
    {
        var store = new CommentRetentionStore(null!);
        Assert.Throws<ArgumentException>(() => store.DeleteOlderThan(CommentStatus.Approved, DateTime.UtcNow));
    }

    [Fact]
    public void Retention_is_off_by_default()
    {
        var o = new CommentsOptions();
        Assert.Null(o.SpamRetentionDays);
        Assert.Null(o.PendingRetentionDays);
    }

    [Theory]
    [InlineData(-1, null, null, null, false)]
    [InlineData(null, -1, null, null, false)]
    [InlineData(null, null, 0, null, false)]
    [InlineData(null, null, null, -5, false)]
    [InlineData(0, 0, 1, 1, true)]
    [InlineData(null, null, null, null, true)]
    public void Options_are_validated(int? perPage, int? total, int? spamDays, int? pendingDays, bool valid)
    {
        var result = new CommentsOptionsValidator().Validate(null, new CommentsOptions
        {
            MaxPendingPerPage = perPage,
            MaxPendingTotal = total,
            SpamRetentionDays = spamDays,
            PendingRetentionDays = pendingDays,
        });
        Assert.Equal(valid, result.Succeeded);
    }
}
