using Microsoft.AspNetCore.Http;

namespace BaryoDev.Umbraco.Comments.Services;

public enum SpamVerdict
{
    /// <summary>Carry on as normal.</summary>
    Allow,
    /// <summary>Save the comment with the Spam status. The visitor sees the normal pending notice.</summary>
    Spam,
    /// <summary>Do not save. The visitor sees an error.</summary>
    Reject,
}

/// <summary>A comment that passed validation, as the spam check sees it. Values are already cleaned. <see cref="Http"/> is null when the comment came in through <see cref="CommentSubmission.Submit"/>.</summary>
public sealed record CommentSpamContext(Guid ContentKey, string Name, string? Email, string Body, string Client, HttpContext? Http);

/// <summary>Runs after validation and before the comment is saved. Register your own with <c>services.AddSingleton&lt;ICommentSpamCheck, MyCheck&gt;()</c>. If it throws, nothing is saved.</summary>
public interface ICommentSpamCheck
{
    ValueTask<SpamVerdict> CheckAsync(CommentSpamContext context, CancellationToken cancellationToken);
}

/// <summary>The default. Allows everything.</summary>
public sealed class AllowAllSpamCheck : ICommentSpamCheck
{
    public ValueTask<SpamVerdict> CheckAsync(CommentSpamContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SpamVerdict.Allow);
}
