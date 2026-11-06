namespace BaryoDev.Umbraco.Comments;

public sealed class CommentsOptions
{
    public const string Section = "BaryoDev:Comments";
    public const int HardBodyLimit = 4000;

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
    public int PageSize { get; set; } = 100;
}
