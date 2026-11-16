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
        var errors = new List<Exception>();
        if (o.SpamRetentionDays is int spamDays)
        {
            deleted += Try(CommentStatus.Spam, now.AddDays(-spamDays), errors);
        }
        if (o.PendingRetentionDays is int pendingDays)
        {
            deleted += Try(CommentStatus.Pending, now.AddDays(-pendingDays), errors);
        }
        if (errors.Count == 1)
        {
            throw errors[0];
        }
        if (errors.Count > 1)
        {
            throw new AggregateException(errors);
        }
        return deleted;
    }

    // One failed delete must not skip the other.
    private int Try(CommentStatus status, DateTime cutoff, List<Exception> errors)
    {
        try
        {
            return store.DeleteOlderThan(status, cutoff);
        }
        catch (Exception e)
        {
            errors.Add(e);
            return 0;
        }
    }
}

/// <summary>Runs <see cref="CommentsRetention"/> hourly. Only registered work happens when a retention option is set.</summary>
public sealed class CommentsRetentionService(CommentsRetention retention, IOptions<CommentsOptions> options, ILogger<CommentsRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let host start-up carry on before the first run.
        await Task.Yield();
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
