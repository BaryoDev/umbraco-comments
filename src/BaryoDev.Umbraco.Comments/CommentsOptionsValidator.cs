using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.Comments;

/// <summary>Checks the pending cap and retention options at startup. Other options keep their current behaviour.</summary>
public sealed class CommentsOptionsValidator : IValidateOptions<CommentsOptions>
{
    public const int MaxRetentionDays = 36500;

    public ValidateOptionsResult Validate(string? name, CommentsOptions options)
    {
        var errors = new List<string>();
        if (options.MaxPendingPerPage is < 0)
        {
            errors.Add("MaxPendingPerPage can not be negative. Use null for no cap.");
        }
        if (options.MaxPendingTotal is < 0)
        {
            errors.Add("MaxPendingTotal can not be negative. Use null for no cap.");
        }
        if (options.SpamRetentionDays is < 1 or > MaxRetentionDays)
        {
            errors.Add("SpamRetentionDays must be between 1 and 36500. Use null to keep spam.");
        }
        if (options.PendingRetentionDays is < 1 or > MaxRetentionDays)
        {
            errors.Add("PendingRetentionDays must be between 1 and 36500. Use null to keep pending comments.");
        }
        if (options.ReplyTo is not null && !System.Net.Mail.MailAddress.TryCreate(options.ReplyTo, out _))
        {
            errors.Add("ReplyTo must be an email address. Use null to leave it out.");
        }
        if (options.PublicBaseUrl is not null
            && !(Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out Uri? baseUrl)
                && (baseUrl.Scheme == Uri.UriSchemeHttp || baseUrl.Scheme == Uri.UriSchemeHttps)))
        {
            errors.Add("PublicBaseUrl must be an absolute http or https URL. Use null to use the URL Umbraco builds.");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
