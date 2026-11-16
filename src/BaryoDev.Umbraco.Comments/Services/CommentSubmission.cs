using System.Globalization;
using System.Net.Mail;
using BaryoDev.Umbraco.Comments.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.Comments.Services;

public sealed record CommentInput(Guid ContentKey, string? Name, string? Email, string? Body, string? Honeypot, string? Token, string Client);

public enum SubmitResult { Pending, Approved, Dropped, Invalid, TooFast, Expired, RateLimited, NoSuchPage, NameRequired, EmailRequired, EmailInvalid, BodyInvalid, Reused, Paused, Rejected }

public sealed class CommentSubmission(
    ICommentRepository repository,
    RateLimiter limiter,
    FormToken tokens,
    IContentService content,
    IOptions<CommentsOptions> options,
    TimeProvider time,
    ICommentSpamCheck spamCheck)
{
    public CommentSubmission(
        ICommentRepository repository,
        RateLimiter limiter,
        FormToken tokens,
        IContentService content,
        IOptions<CommentsOptions> options,
        TimeProvider time)
        : this(repository, limiter, tokens, content, options, time, new AllowAllSpamCheck())
    {
    }

    /// <summary>Same as <see cref="SubmitAsync"/> without a request context. Prefer <see cref="SubmitAsync"/>.</summary>
    public SubmitResult Submit(CommentInput input) =>
        SubmitAsync(input, null, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public async ValueTask<SubmitResult> SubmitAsync(CommentInput input, HttpContext? http = null, CancellationToken cancellationToken = default)
    {
        CommentsOptions o = options.Value;

        if (!string.IsNullOrWhiteSpace(input.Honeypot))
        {
            return SubmitResult.Dropped;
        }

        TimeSpan? age = tokens.Age(input.Token, input.ContentKey);
        if (age is null)
        {
            return SubmitResult.Invalid;
        }
        if (age.Value.TotalSeconds < o.MinSecondsOnForm)
        {
            return SubmitResult.TooFast;
        }
        if (age.Value.TotalMinutes > o.MaxFormAgeMinutes)
        {
            return SubmitResult.Expired;
        }

        string name = Clean(input.Name, singleLine: true);
        string email = Clean(input.Email, singleLine: true);
        string body = Clean(input.Body, singleLine: false);

        if (name.Length == 0 || name.Length > Math.Min(o.MaxNameLength, CommentsOptions.HardNameLimit))
        {
            return SubmitResult.NameRequired;
        }
        if (email.Length == 0 && o.RequireEmail)
        {
            return SubmitResult.EmailRequired;
        }
        if (email.Length > 0 && !IsEmail(email, Math.Min(o.MaxEmailLength, CommentsOptions.HardEmailLimit)))
        {
            return SubmitResult.EmailInvalid;
        }
        if (body.Length < o.MinBodyLength || body.Length > Math.Min(o.MaxBodyLength, CommentsOptions.HardBodyLimit))
        {
            return SubmitResult.BodyInvalid;
        }

        if (!limiter.TryAcquire(input.Client, o.RateLimitMaxPosts, TimeSpan.FromMinutes(o.RateLimitWindowMinutes)))
        {
            return SubmitResult.RateLimited;
        }

        var page = content.GetById(input.ContentKey);
        if (page is null || !page.Published)
        {
            return SubmitResult.NoSuchPage;
        }

        TokenUse use = tokens.Use(input.Token, input.ContentKey, TimeSpan.FromMinutes(o.MaxFormAgeMinutes));
        if (use != TokenUse.Accepted)
        {
            // A full table fails closed with the expired-form notice.
            return use switch { TokenUse.Reused => SubmitResult.Reused, TokenUse.Full => SubmitResult.Expired, _ => SubmitResult.Invalid };
        }

        bool saved = false;
        try
        {
            SpamVerdict verdict = await spamCheck.CheckAsync(
                new CommentSpamContext(input.ContentKey, name, email.Length == 0 ? null : email, body, input.Client, http), cancellationToken);
            CommentStatus status;
            switch (verdict)
            {
                case SpamVerdict.Allow:
                    status = o.AutoApprove ? CommentStatus.Approved : CommentStatus.Pending;
                    break;
                case SpamVerdict.Spam:
                    status = CommentStatus.Spam;
                    break;
                default:
                    // Reject, and any value this version does not know, fails closed.
                    return SubmitResult.Rejected;
            }
            if (status == CommentStatus.Pending && PendingCapReached(o, input.ContentKey))
            {
                return SubmitResult.Paused;
            }

            repository.Add(new CommentRow
            {
                Id = Guid.NewGuid(),
                ContentKey = input.ContentKey,
                Name = name,
                Email = email.Length == 0 ? null : email,
                Body = body,
                Status = (int)status,
                CreatedUtc = time.GetUtcNow().UtcDateTime,
            });
            saved = true;
            return status switch
            {
                CommentStatus.Spam => SubmitResult.Dropped,
                CommentStatus.Approved => SubmitResult.Approved,
                _ => SubmitResult.Pending,
            };
        }
        finally
        {
            // Nothing was saved, so the visitor can send the same form again.
            if (!saved)
            {
                tokens.Release(input.Token, input.ContentKey);
            }
        }
    }

    private bool PendingCapReached(CommentsOptions o, Guid contentKey)
    {
        if (o.MaxPendingTotal is int total && repository.Counts().Pending >= total)
        {
            return true;
        }
        return o.MaxPendingPerPage is int perPage && repository.Counts([contentKey]).Pending >= perPage;
    }

    private static bool IsEmail(string value, int max)
    {
        if (value.Length > max)
        {
            return false;
        }
        return MailAddress.TryCreate(value, out var address) && address.Address == value && address.Host.Contains('.');
    }

    /// <summary>Trims, normalises line breaks and drops control characters.</summary>
    internal static string Clean(string? value, bool singleLine)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (char c in value.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c is '\u200B' or '\u2060' or '\uFEFF')
            {
                continue;
            }
            // Bidi overrides and other format characters in a name or email would reorder the text around it.
            // Bodies keep them, since emoji sequences need the joiner.
            if (singleLine && char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }
            if (c == '\n' && !singleLine)
            {
                sb.Append(c);
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }
}
