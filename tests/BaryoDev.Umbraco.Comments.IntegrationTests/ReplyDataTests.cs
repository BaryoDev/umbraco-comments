using BaryoDev.Umbraco.Comments.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Install;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;

namespace BaryoDev.Umbraco.Comments.IntegrationTests;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest)]
public class ReplyDataTests : UmbracoIntegrationTest
{
    private const string CreateTablesStep = "b3d7c1a4-5e02-4f6a-8a31-7c9e0d2f4b18";
    private const string PlanName = "BaryoDev.Umbraco.Comments";

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
        AssertRan(await runner.RunPackageMigrationsIfPendingAsync(PlanName));

        Assert.That(TableExists(), Is.True);
    }

    // The runner reports a failed plan in its result instead of throwing.
    private static void AssertRan(IEnumerable<ExecutedMigrationPlan> executed)
    {
        foreach (ExecutedMigrationPlan plan in executed)
        {
            Assert.That(plan.Successful, Is.True, plan.Exception?.ToString());
        }
    }

    private bool TableExists()
    {
        using var scope = ScopeProvider.CreateScope(autoComplete: true);
        return scope.Database.SqlContext.SqlSyntax.DoesTableExist(scope.Database, CommentRow.TableName);
    }

    private HashSet<string> Columns()
    {
        using var scope = ScopeProvider.CreateScope(autoComplete: true);
        return scope.Database.SqlContext.SqlSyntax.GetColumnsInSchema(scope.Database)
            .Where(c => c.TableName == CommentRow.TableName)
            .Select(c => c.ColumnName)
            .ToHashSet();
    }

    private static CommentRow Row(Guid page, CommentStatus status, int minute, string name = "Ana", Guid? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentKey = page,
        Name = name,
        Body = "body " + name,
        Status = (int)status,
        CreatedUtc = new DateTime(2026, 11, 21, 12, minute, 0, DateTimeKind.Utc),
        ParentId = parent,
    };

    [Test]
    public async Task Migration_adds_reply_columns_to_a_0_2_0_table()
    {
        var oldId = Guid.NewGuid();
        var page = Guid.NewGuid();
        var created = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

        using (var scope = ScopeProvider.CreateScope())
        {
            var db = scope.Database;
            bool sqlite = db.SqlContext.SqlSyntax.ProviderName == Constants.ProviderNames.SQLLite;
            string guid = sqlite ? "TEXT" : "uniqueidentifier";
            string text = sqlite ? "TEXT" : "nvarchar(4000)";
            string time = sqlite ? "TEXT" : "datetime";

            db.Execute("DROP TABLE baryoComment");
            db.Execute($"CREATE TABLE baryoComment (Id {guid} NOT NULL PRIMARY KEY, ContentKey {guid} NOT NULL, Name {text} NOT NULL, " +
                       $"Email {text} NULL, Body {text} NOT NULL, Status int NOT NULL, CreatedUtc {time} NOT NULL)");
            db.Execute("CREATE INDEX IX_baryoComment_ContentKey ON baryoComment (ContentKey)");
            db.Execute("INSERT INTO baryoComment (Id, ContentKey, Name, Email, Body, Status, CreatedUtc) VALUES (@0, @1, @2, @3, @4, @5, @6)",
                oldId, page, "Old", "old@example.com", "an old comment", (int)CommentStatus.Approved, created);
            scope.Complete();
        }

        Assert.That(Columns(), Does.Not.Contain("ParentId"));

        // Put the plan back at the CreateTables step, as a site that has only run 0.2.0 would be.
        Services.GetRequiredService<IKeyValueService>().SetValue(Constants.Conventions.Migrations.KeyValuePrefix + PlanName, CreateTablesStep);

        var runner = Services.GetRequiredService<PackageMigrationRunner>();
        var executed = (await runner.RunPackageMigrationsIfPendingAsync(PlanName)).ToList();
        Assert.That(executed, Has.Count.EqualTo(1), "the plan was not pending");
        AssertRan(executed);

        Assert.That(Columns(), Is.SupersetOf(new[] { "ParentId", "AuthorUserKey", "NotifiedUtc" }));
        var old = Comments.Get(oldId);
        Assert.That(old, Is.Not.Null);
        Assert.That(old!.Name, Is.EqualTo("Old"));
        Assert.That(old.ParentId, Is.Null);
        Assert.That(old.AuthorUserKey, Is.Null);
        Assert.That(old.NotifiedUtc, Is.Null);
    }

    [Test]
    public void Migration_runs_twice_without_error_with_reply_columns()
    {
        Assert.That(Columns(), Is.SupersetOf(new[] { "ParentId", "AuthorUserKey", "NotifiedUtc" }));

        var executor = Services.GetRequiredService<IMigrationPlanExecutor>();
        Assert.DoesNotThrowAsync(async () => await executor.ExecutePlanAsync(new CommentsMigrationPlan(), string.Empty));
        Assert.That(Columns(), Is.SupersetOf(new[] { "ParentId", "AuthorUserKey", "NotifiedUtc" }));
    }

    [Test]
    public void Top_level_list_and_count_skip_replies()
    {
        var page = Guid.NewGuid();
        var first = Row(page, CommentStatus.Approved, 0, "first");
        var second = Row(page, CommentStatus.Approved, 1, "second");
        Comments.Add(first);
        Comments.Add(second);
        for (int i = 0; i < 3; i++)
        {
            Comments.Add(Row(page, CommentStatus.Approved, 2 + i, "reply" + i, first.Id));
        }

        Assert.That(Comments.CountApprovedTopLevel(page), Is.EqualTo(2));
        Assert.That(Comments.ListApprovedTopLevel(page, 0, 10).Select(r => r.Name), Is.EqualTo(new[] { "first", "second" }));
        Assert.That(Comments.ListApprovedTopLevel(page, 0, 10).Count, Is.EqualTo(2));
        Assert.That(Comments.CountApproved(page), Is.EqualTo(5));
    }

    [Test]
    public void ListReplies_returns_approved_replies_oldest_first()
    {
        var page = Guid.NewGuid();
        var parent = Row(page, CommentStatus.Approved, 0, "parent");
        var other = Row(page, CommentStatus.Approved, 1, "other");
        Comments.Add(parent);
        Comments.Add(other);
        Comments.Add(Row(page, CommentStatus.Approved, 5, "late", parent.Id));
        Comments.Add(Row(page, CommentStatus.Pending, 3, "pending", parent.Id));
        Comments.Add(Row(page, CommentStatus.Approved, 2, "early", parent.Id));
        Comments.Add(Row(page, CommentStatus.Approved, 4, "elsewhere", other.Id));

        Assert.That(Comments.ListReplies([parent.Id]).Select(r => r.Name), Is.EqualTo(new[] { "early", "late" }));
        Assert.That(Comments.ListReplies([parent.Id, other.Id]).Select(r => r.Name), Is.EqualTo(new[] { "early", "elsewhere", "late" }));
        Assert.That(Comments.ListReplies([]), Is.Empty);
    }

    [Test]
    public void Delete_removes_replies()
    {
        var page = Guid.NewGuid();
        var parent = Row(page, CommentStatus.Approved, 0, "parent");
        var keep = Row(page, CommentStatus.Approved, 1, "keep");
        Comments.Add(parent);
        Comments.Add(keep);
        var reply = Row(page, CommentStatus.Approved, 2, "reply", parent.Id);
        Comments.Add(reply);
        Comments.Add(Row(page, CommentStatus.Approved, 3, "keep reply", keep.Id));

        Assert.That(Comments.Delete(parent.Id), Is.True);

        Assert.That(Comments.Get(parent.Id), Is.Null);
        Assert.That(Comments.Get(reply.Id), Is.Null);
        Assert.That(Comments.ListReplies([parent.Id]), Is.Empty);
        Assert.That(Comments.ListReplies([keep.Id]), Has.Count.EqualTo(1));
    }

    [Test]
    public void Retention_removes_orphan_replies()
    {
        var page = Guid.NewGuid();
        var cutoff = new DateTime(2026, 11, 21, 12, 30, 0, DateTimeKind.Utc);
        var parent = Row(page, CommentStatus.Spam, 0, "old-spam");
        var reply = Row(page, CommentStatus.Approved, 45, "reply", parent.Id);
        var kept = Row(page, CommentStatus.Approved, 46, "kept");
        Comments.Add(parent);
        Comments.Add(reply);
        Comments.Add(kept);

        var store = new CommentRetentionStore(GetRequiredService<IScopeProvider>());
        Assert.That(store.DeleteOlderThan(CommentStatus.Spam, cutoff), Is.EqualTo(1));

        Assert.That(Comments.Get(parent.Id), Is.Null);
        Assert.That(Comments.Get(reply.Id), Is.Null);
        Assert.That(Comments.Get(kept.Id), Is.Not.Null);
    }

    [Test]
    public void TryClaimNotify_succeeds_once()
    {
        var page = Guid.NewGuid();
        var parent = Row(page, CommentStatus.Approved, 0, "parent");
        var reply = Row(page, CommentStatus.Approved, 1, "reply", parent.Id);
        Comments.Add(parent);
        Comments.Add(reply);
        var at = new DateTime(2026, 11, 21, 13, 0, 0, DateTimeKind.Utc);

        Assert.That(Comments.TryClaimNotify(reply.Id, at), Is.True);
        Assert.That(Comments.TryClaimNotify(reply.Id, at), Is.False);
        Assert.That(Comments.Get(reply.Id)!.NotifiedUtc, Is.Not.Null);

        Assert.That(Comments.TryClaimNotify(parent.Id, at), Is.False);

        Comments.ReleaseNotify(reply.Id);
        Assert.That(Comments.Get(reply.Id)!.NotifiedUtc, Is.Null);
        Assert.That(Comments.TryClaimNotify(reply.Id, at), Is.True);
    }
}
