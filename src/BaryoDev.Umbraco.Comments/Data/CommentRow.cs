using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace BaryoDev.Umbraco.Comments.Data;

[TableName(TableName)]
[PrimaryKey(nameof(Id), AutoIncrement = false)]
[ExplicitColumns]
public sealed class CommentRow
{
    public const string TableName = "baryoComment";

    [Column("Id")]
    [PrimaryKeyColumn(AutoIncrement = false)]
    public Guid Id { get; set; }

    [Column("ContentKey")]
    [Index(IndexTypes.NonClustered, Name = "IX_baryoComment_ContentKey")]
    public Guid ContentKey { get; set; }

    [Column("Name")]
    [Length(80)]
    public string Name { get; set; } = string.Empty;

    [Column("Email")]
    [Length(200)]
    [NullSetting(NullSetting = NullSettings.Null)]
    public string? Email { get; set; }

    [Column("Body")]
    [Length(CommentsOptions.HardBodyLimit)]
    public string Body { get; set; } = string.Empty;

    [Column("Status")]
    public int Status { get; set; }

    [Column("CreatedUtc")]
    public DateTime CreatedUtc { get; set; }
}
