using Umbraco.Cms.Infrastructure.Migrations;

namespace BaryoDev.Umbraco.Comments.Data;

public sealed class AddReplyColumns(IMigrationContext context) : AsyncMigrationBase(context)
{
    private const string ParentIndex = "IX_baryoComment_ParentId";

    protected override Task MigrateAsync()
    {
        // Not Alter.Table: on SQLite it throws. These helpers write ALTER TABLE ADD COLUMN, which SQLite accepts,
        // and skip a column that already exists.
        AddColumn<CommentRow>(CommentRow.TableName, "ParentId");
        AddColumn<CommentRow>(CommentRow.TableName, "AuthorUserKey");
        AddColumn<CommentRow>(CommentRow.TableName, "NotifiedUtc");
        if (!IndexExists(ParentIndex))
        {
            CreateIndex<CommentRow>(ParentIndex);
        }
        return Task.CompletedTask;
    }
}
