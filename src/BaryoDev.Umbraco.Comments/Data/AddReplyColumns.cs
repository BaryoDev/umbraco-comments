using Umbraco.Cms.Infrastructure.Migrations;

namespace BaryoDev.Umbraco.Comments.Data;

public sealed class AddReplyColumns(IMigrationContext context) : AsyncMigrationBase(context)
{
    private const string ParentIndex = "IX_baryoComment_ParentId";

    protected override Task MigrateAsync()
    {
        if (!ColumnExists(CommentRow.TableName, "ParentId"))
        {
            Alter.Table(CommentRow.TableName).AddColumn("ParentId").AsGuid().Nullable().Do();
        }
        if (!ColumnExists(CommentRow.TableName, "AuthorUserKey"))
        {
            Alter.Table(CommentRow.TableName).AddColumn("AuthorUserKey").AsGuid().Nullable().Do();
        }
        if (!ColumnExists(CommentRow.TableName, "NotifiedUtc"))
        {
            Alter.Table(CommentRow.TableName).AddColumn("NotifiedUtc").AsDateTime().Nullable().Do();
        }
        if (!IndexExists(ParentIndex))
        {
            Create.Index(ParentIndex).OnTable(CommentRow.TableName).OnColumn("ParentId").Ascending().WithOptions().NonClustered().Do();
        }
        return Task.CompletedTask;
    }
}
