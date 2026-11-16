namespace BaryoDev.Umbraco.Comments;

public sealed class CommentsOptions
{
    public const string Section = "BaryoDev:Comments";
    public const int HardBodyLimit = 4000;
    public const int HardNameLimit = 80;
    public const int HardEmailLimit = 200;

    public bool AutoApprove { get; set; }
    public bool RequireEmail { get; set; }
    public int MaxNameLength { get; set; } = 80;
    public int MaxEmailLength { get; set; } = 200;
    public int MaxBodyLength { get; set; } = 2000;
    public int MinBodyLength { get; set; } = 2;
    public int MinSecondsOnForm { get; set; } = 3;
    public int MaxFormAgeMinutes { get; set; } = 120;
    public int RateLimitMaxPosts { get; set; } = 5;
    public int RateLimitWindowMinutes { get; set; } = 10;
    public int PageSize { get; set; } = 50;
    /// <summary>Pending comments one page can hold. At the cap new comments for that page are refused and nothing is saved. Null (the default) means no cap.</summary>
    public int? MaxPendingPerPage { get; set; }
    /// <summary>Pending comments across all pages. Null (the default) means no cap.</summary>
    public int? MaxPendingTotal { get; set; }
    /// <summary>Delete spam comments older than this many days. Null keeps them.</summary>
    public int? SpamRetentionDays { get; set; }
    /// <summary>Delete pending comments older than this many days. Null keeps them. Approved comments are never deleted by age.</summary>
    public int? PendingRetentionDays { get; set; }
}
