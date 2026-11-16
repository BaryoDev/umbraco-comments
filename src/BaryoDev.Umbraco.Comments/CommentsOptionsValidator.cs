using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.Comments;

/// <summary>Checks the pending cap and retention options at startup. Other options keep their current behaviour.</summary>
public sealed class CommentsOptionsValidator : IValidateOptions<CommentsOptions>
{
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
        if (options.SpamRetentionDays is < 1)
        {
            errors.Add("SpamRetentionDays must be 1 or more. Use null to keep spam.");
        }
        if (options.PendingRetentionDays is < 1)
        {
            errors.Add("PendingRetentionDays must be 1 or more. Use null to keep pending comments.");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
