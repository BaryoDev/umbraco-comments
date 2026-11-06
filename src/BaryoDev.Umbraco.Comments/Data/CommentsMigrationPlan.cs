using Umbraco.Cms.Core.Packaging;

namespace BaryoDev.Umbraco.Comments.Data;

public sealed class CommentsMigrationPlan() : PackageMigrationPlan("BaryoDev.Umbraco.Comments")
{
    protected override void DefinePlan()
    {
        To<CreateTables>("b3d7c1a4-5e02-4f6a-8a31-7c9e0d2f4b18");
    }
}
