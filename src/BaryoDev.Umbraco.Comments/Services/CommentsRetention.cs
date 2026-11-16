using BaryoDev.Umbraco.Comments.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.Comments.Services;

/// <summary>Deletes spam and pending comments older than the configured number of days. Does nothing for a setting that is null. Approved comments are never touched.</summary>
public sealed class CommentsRetention(ICommentRetentionStore store, IOptions<CommentsOptions> options, TimeProvider time)
{
    public int RunOnce()
    {
        CommentsOptions o = options.Value;
        DateTime now = time.GetUtcNow().UtcDateTime;
        int deleted = 0;
        if (o.SpamRetentionDays is int spamDays)
        {
            deleted += store.DeleteOlderThan(CommentStatus.Spam, now.AddDays(-spamDays));
        }
        if (o.PendingRetentionDays is int pendingDays)
        {
            deleted += store.DeleteOlderThan(CommentStatus.Pending, now.AddDays(-pendingDays));
        }
        return deleted;
    }
}

/// <summary>Runs <see cref="CommentsRetention"/> hourly. Only registered work happens when a retention option is set.</summary>
public sealed class CommentsRetentionService(CommentsRetention retention, IOptions<CommentsOptions> options, ILogger<CommentsRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CommentsOptions o = options.Value;
        if (o.SpamRetentionDays is null && o.PendingRetentionDays is null)
        {
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                try
                {
                    int n = retention.RunOnce();
                    if (n > 0)
                    {
                        logger.LogInformation("Comment retention deleted {Count} rows.", n);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Comment retention run failed. It will try again in an hour.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
