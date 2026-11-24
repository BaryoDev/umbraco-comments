using BaryoDev.Umbraco.Comments.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.Comments.Services;

public enum ReplyResult { Saved, NotFound, NotApproved, IsReply, BodyInvalid, Unsupported }

public enum NotifyResult { Sent, NotFound, NotAReply, AlreadySent, NoEmail, CannotSend, Failed, Unsupported, NotApproved }

public sealed record ReplyAuthor(Guid UserKey, string Name);

public sealed record ReplyOutcome(ReplyResult Result, Guid? ReplyId, NotifyResult? Email);

/// <summary>The rules for replying to a comment and emailing the commenter about it.</summary>
public sealed class CommentReplies(
    ICommentRepository repository,
    ICommentReplyEmail email,
    IOptions<CommentsOptions> options,
    TimeProvider time,
    ILogger<CommentReplies> logger)
{
    public async Task<ReplyOutcome> ReplyAsync(Guid parentId, string? body, bool notify, ReplyAuthor author, CancellationToken ct = default)
    {
        if (repository is not ICommentRepositoryThreaded)
        {
            return new ReplyOutcome(ReplyResult.Unsupported, null, null);
        }

        CommentRow? parent = repository.Get(parentId);
        if (parent is null)
        {
            return new ReplyOutcome(ReplyResult.NotFound, null, null);
        }
        if (parent.Status != (int)CommentStatus.Approved)
        {
            return new ReplyOutcome(ReplyResult.NotApproved, null, null);
        }
        if (parent.ParentId is not null)
        {
            return new ReplyOutcome(ReplyResult.IsReply, null, null);
        }

        CommentsOptions o = options.Value;
        string text = CommentSubmission.Clean(body, singleLine: false);
        if (text.Length < o.MinBodyLength || text.Length > Math.Min(o.MaxBodyLength, CommentsOptions.HardBodyLimit))
        {
            return new ReplyOutcome(ReplyResult.BodyInvalid, null, null);
        }

        string name = CommentSubmission.Clean(author.Name, singleLine: true);
        if (name.Length > CommentsOptions.HardNameLimit)
        {
            name = name[..CommentsOptions.HardNameLimit];
        }
        if (name.Length == 0)
        {
            name = "Author";
        }

        var reply = new CommentRow
        {
            Id = Guid.NewGuid(),
            ContentKey = parent.ContentKey,
            ParentId = parent.Id,
            AuthorUserKey = author.UserKey,
            Name = name,
            Email = null,
            Body = text,
            Status = (int)CommentStatus.Approved,
            CreatedUtc = time.GetUtcNow().UtcDateTime,
        };
        repository.Add(reply);

        NotifyResult? sent = null;
        if (notify && !string.IsNullOrWhiteSpace(parent.Email))
        {
            try
            {
                sent = await NotifyAsync(reply.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The reply is already saved, so report the email as failed instead of failing the request.
                logger.LogError(ex, "Emailing the commenter about reply {ReplyId} failed", reply.Id);
                sent = NotifyResult.Failed;
            }
        }
        return new ReplyOutcome(ReplyResult.Saved, reply.Id, sent);
    }

    public async Task<NotifyResult> NotifyAsync(Guid replyId, CancellationToken ct = default)
    {
        if (repository is not ICommentRepositoryThreaded threaded)
        {
            return NotifyResult.Unsupported;
        }

        CommentRow? reply = repository.Get(replyId);
        if (reply is null)
        {
            return NotifyResult.NotFound;
        }
        if (reply.ParentId is not Guid parentId)
        {
            return NotifyResult.NotAReply;
        }
        if (reply.NotifiedUtc is not null)
        {
            return NotifyResult.AlreadySent;
        }
        if (reply.Status != (int)CommentStatus.Approved)
        {
            return NotifyResult.NotApproved;
        }

        CommentRow? parent = repository.Get(parentId);
        if (parent is null || string.IsNullOrWhiteSpace(parent.Email))
        {
            return NotifyResult.NoEmail;
        }
        if (parent.Status != (int)CommentStatus.Approved)
        {
            return NotifyResult.NotApproved;
        }
        // Umbraco's sender returns without sending when SMTP is not set, so claim nothing here.
        if (!email.CanSend)
        {
            return NotifyResult.CannotSend;
        }
        if (!threaded.TryClaimNotify(replyId, time.GetUtcNow().UtcDateTime))
        {
            return NotifyResult.AlreadySent;
        }

        try
        {
            await email.SendAsync(parent, reply, ct);
            return NotifyResult.Sent;
        }
        catch (Exception ex)
        {
            try
            {
                threaded.ReleaseNotify(replyId);
            }
            catch (Exception releaseEx)
            {
                logger.LogWarning(releaseEx, "The email claim for comment {ReplyId} could not be released.", replyId);
            }
            logger.LogWarning(ex, "Reply email for comment {ReplyId} failed and can be sent again.", replyId);
            return NotifyResult.Failed;
        }
    }
}
