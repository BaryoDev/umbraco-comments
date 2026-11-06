using Umbraco.Cms.Infrastructure.Migrations;

namespace BaryoDev.Umbraco.Comments.Data;

public sealed class CreateTables(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (!TableExists(CommentRow.TableName))
        {
            Create.Table<CommentRow>().Do();
        }
        return Task.CompletedTask;
    }
}
