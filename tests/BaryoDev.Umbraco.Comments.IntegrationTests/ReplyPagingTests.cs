using BaryoDev.Umbraco.Comments.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Infrastructure.Install;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;

namespace BaryoDev.Umbraco.Comments.IntegrationTests;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest)]
public class ReplyPagingTests : UmbracoIntegrationTest
{
    private ICommentRepositoryThreaded Comments => GetRequiredService<ICommentRepositoryThreaded>();

    protected override void CustomTestSetup(IUmbracoBuilder builder)
    {
        builder.Services.TryAddSingleton<CommentRepository>();
        builder.Services.TryAddSingleton<ICommentRepository>(sp => sp.GetRequiredService<CommentRepository>());
        builder.Services.TryAddSingleton<ICommentRepositoryThreaded>(sp => sp.GetRequiredService<CommentRepository>());
    }

    [SetUp]
    public async Task RunPackageMigration()
    {
        var runner = Services.GetRequiredService<PackageMigrationRunner>();
        await runner.RunPackageMigrationsIfPendingAsync("BaryoDev.Umbraco.Comments");

        using var scope = ScopeProvider.CreateScope(autoComplete: true);
        Assert.That(scope.Database.SqlContext.SqlSyntax.DoesTableExist(scope.Database, CommentRow.TableName), Is.True);
    }

    private static CommentRow Row(Guid page, int minute, string name, Guid? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentKey = page,
        Name = name,
        Body = "body " + name,
        Status = (int)CommentStatus.Approved,
        CreatedUtc = new DateTime(2026, 11, 21, 12, minute, 0, DateTimeKind.Utc),
        ParentId = parent,
    };

    private IReadOnlyList<CommentView> Page(Guid page, int skip, int take)
    {
        var parents = Comments.ListApprovedTopLevel(page, skip, take);
        var replies = Comments.ListReplies(parents.Select(p => p.Id).ToList());
        return CommentsViewComponent.Thread(parents, replies);
    }

    [Test]
    public void Paging_counts_top_level_comments_only()
    {
        var page = Guid.NewGuid();
        var parents = new[] { Row(page, 1, "A"), Row(page, 2, "B"), Row(page, 3, "C") };
        foreach (var p in parents)
        {
            Comments.Add(p);
        }
        Comments.Add(Row(page, 10, "r1", parents[0].Id));
        Comments.Add(Row(page, 11, "r2", parents[0].Id));
        Comments.Add(Row(page, 12, "r3", parents[1].Id));
        Comments.Add(Row(page, 13, "r4", parents[2].Id));

        int total = Comments.CountApprovedTopLevel(page);
        var window = CommentsViewComponent.Window(total, 2, null);

        Assert.That(total, Is.EqualTo(3));
        Assert.That(window.PageCount, Is.EqualTo(2));
        Assert.That(Page(page, window.Skip, window.PageSize), Has.Count.EqualTo(2));
    }

    [Test]
    public void Replies_of_a_spam_comment_are_hidden()
    {
        var page = Guid.NewGuid();
        var parent = Row(page, 1, "A");
        var other = Row(page, 2, "B");
        Comments.Add(parent);
        Comments.Add(other);
        Comments.Add(Row(page, 10, "r1", parent.Id));
        Comments.Add(Row(page, 11, "r2", other.Id));

        Comments.SetStatus(parent.Id, CommentStatus.Spam);

        var thread = Page(page, 0, 10);
        Assert.That(thread.Select(c => c.Name), Is.EqualTo(new[] { "B" }));
        Assert.That(thread.SelectMany(c => c.Replies).Select(r => r.Name), Is.EqualTo(new[] { "r2" }));
    }
}
