using System.Net.Mail;
using BaryoDev.Umbraco.Comments.Data;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.Comments.Services;

public sealed record CommentInput(Guid ContentKey, string? Name, string? Email, string? Body, string? Honeypot, string? Token, string Client);

public enum SubmitResult { Pending, Approved, Dropped, Invalid, TooFast, Expired, RateLimited, NoSuchPage, NameRequired, EmailRequired, EmailInvalid, BodyInvalid }

public sealed class CommentSubmission(
    ICommentRepository repository,
    RateLimiter limiter,
    FormToken tokens,
    IContentService content,
    IOptions<CommentsOptions> options,
    TimeProvider time)
{
    public SubmitResult Submit(CommentInput input)
    {
        CommentsOptions o = options.Value;

        if (!string.IsNullOrWhiteSpace(input.Honeypot))
        {
            return SubmitResult.Dropped;
        }

        TimeSpan? age = tokens.Age(input.Token);
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

        if (name.Length == 0 || name.Length > o.MaxNameLength)
        {
            return SubmitResult.NameRequired;
        }
        if (email.Length == 0 && o.RequireEmail)
        {
            return SubmitResult.EmailRequired;
        }
        if (email.Length > 0 && !IsEmail(email, o.MaxEmailLength))
        {
            return SubmitResult.EmailInvalid;
        }
        if (body.Length < o.MinBodyLength || body.Length > Math.Min(o.MaxBodyLength, CommentsOptions.HardBodyLimit))
        {
            return SubmitResult.BodyInvalid;
        }

        var page = content.GetById(input.ContentKey);
        if (page is null || !page.Published)
        {
            return SubmitResult.NoSuchPage;
        }

        if (!limiter.TryAcquire(input.Client, o.RateLimitMaxPosts, TimeSpan.FromMinutes(o.RateLimitWindowMinutes)))
        {
            return SubmitResult.RateLimited;
        }

        repository.Add(new CommentRow
        {
            Id = Guid.NewGuid(),
            ContentKey = input.ContentKey,
            Name = name,
            Email = email.Length == 0 ? null : email,
            Body = body,
            Status = (int)(o.AutoApprove ? CommentStatus.Approved : CommentStatus.Pending),
            CreatedUtc = time.GetUtcNow().UtcDateTime,
        });
        return o.AutoApprove ? SubmitResult.Approved : SubmitResult.Pending;
    }

    private static bool IsEmail(string value, int max)
    {
        if (value.Length > max)
        {
            return false;
        }
        return MailAddress.TryCreate(value, out var address) && address.Address == value;
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
