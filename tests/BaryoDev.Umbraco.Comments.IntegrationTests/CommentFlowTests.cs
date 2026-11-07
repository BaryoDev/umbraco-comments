using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Install;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;

namespace BaryoDev.Umbraco.Comments.IntegrationTests;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest)]
public class CommentFlowTests : UmbracoIntegrationTest
{
    private ICommentRepository Comments => GetRequiredService<ICommentRepository>();

    protected override void CustomTestSetup(IUmbracoBuilder builder)
    {
        builder.Services.TryAddSingleton<ICommentRepository, CommentRepository>();
        builder.AddNotificationHandler<ContentDeletedNotification, CommentsContentDeletedHandler>();
    }

    [SetUp]
    public async Task RunPackageMigration()
    {
        var runner = Services.GetRequiredService<PackageMigrationRunner>();
        await runner.RunPackageMigrationsIfPendingAsync("BaryoDev.Umbraco.Comments");

        // The runner skips when key-value state says the plan ran; run it directly if the table is absent.
        if (!TableExists())
        {
            var executor = Services.GetRequiredService<IMigrationPlanExecutor>();
            await executor.ExecutePlanAsync(new CommentsMigrationPlan(), string.Empty);
        }
    }

    private bool TableExists()
    {
        using var scope = ScopeProvider.CreateScope(autoComplete: true);
        return scope.Database.SqlContext.SqlSyntax.DoesTableExist(scope.Database, CommentRow.TableName);
    }

    private static CommentRow Row(Guid key, CommentStatus status, int minute, string name = "Ana") => new()
    {
        Id = Guid.NewGuid(),
        ContentKey = key,
        Name = name,
        Body = "body " + name,
        Status = (int)status,
        CreatedUtc = new DateTime(2026, 11, 7, 12, minute, 0, DateTimeKind.Utc),
    };

    private Guid PublishPage()
    {
        var shortString = Services.GetRequiredService<IShortStringHelper>();
        var types = Services.GetRequiredService<IContentTypeService>();
        var type = new ContentType(shortString, Constants.System.Root) { Alias = "page", Name = "page", AllowedAsRoot = true };
        var created = types.CreateAsync(type, Constants.Security.SuperUserKey).GetAwaiter().GetResult();
        Assert.That(created.Success, Is.True, "create type: " + created.Result);

        var content = Services.GetRequiredService<IContentService>();
        IContent page = content.Create("Page", Constants.System.Root, "page");
        content.Save(page);
        var published = content.Publish(page, ["*"]);
        Assert.That(published.Success, Is.True, "publish: " + published.Result);
        return page.Key;
    }

    [Test]
    public void Migration_runs_twice_without_error()
    {
        Assert.That(TableExists(), Is.True);

        var executor = Services.GetRequiredService<IMigrationPlanExecutor>();
        Assert.DoesNotThrowAsync(async () => await executor.ExecutePlanAsync(new CommentsMigrationPlan(), string.Empty));
        Assert.That(TableExists(), Is.True);
    }

    private SubmitResult SubmitComment(Guid pageKey, bool autoApprove)
    {
        var options = new CommentsOptions { AutoApprove = autoApprove, MinSecondsOnForm = 0 };
        var tokens = new FormToken(GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
        var submission = new CommentSubmission(
            Comments,
            new RateLimiter(TimeProvider.System),
            tokens,
            GetRequiredService<IContentService>(),
            Options.Create(options),
            TimeProvider.System);
        return submission.Submit(new CommentInput(pageKey, "Ana", null, "A real comment", null, tokens.Issue(), "1.1.1.1"));
    }

    [Test]
    public void New_comment_is_pending_and_not_listed()
    {
        var key = PublishPage();

        Assert.That(SubmitComment(key, autoApprove: false), Is.EqualTo(SubmitResult.Pending));

        Assert.That(Comments.ListApproved(key, 0, 10), Is.Empty);
        Assert.That(Comments.CountApproved(key), Is.EqualTo(0));
        var stored = Comments.List(CommentStatus.Pending, 0, 10);
        Assert.That(stored, Has.Count.EqualTo(1));
        Assert.That(stored[0].ContentKey, Is.EqualTo(key));
        Assert.That(stored[0].Status, Is.EqualTo((int)CommentStatus.Pending));
    }

    [Test]
    public void Auto_approve_lists_the_comment_at_once()
    {
        var key = PublishPage();

        Assert.That(SubmitComment(key, autoApprove: true), Is.EqualTo(SubmitResult.Approved));

        var listed = Comments.ListApproved(key, 0, 10);
        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0].Body, Is.EqualTo("A real comment"));
    }

    [Test]
    public void Approved_comment_is_listed()
    {
        var key = Guid.NewGuid();
        var row = Row(key, CommentStatus.Pending, 0);
        Comments.Add(row);

        Assert.That(Comments.SetStatus(row.Id, CommentStatus.Approved), Is.True);

        var listed = Comments.ListApproved(key, 0, 10);
        Assert.That(listed.Select(r => r.Id), Is.EqualTo(new[] { row.Id }));
        Assert.That(Comments.CountApproved(key), Is.EqualTo(1));
    }

    [Test]
    public void Second_page_skips_the_first_page()
    {
        var key = Guid.NewGuid();
        for (int i = 1; i <= 5; i++)
        {
            Comments.Add(Row(key, CommentStatus.Approved, i, "c" + i));
        }

        var (page, pageSize, _, skip) = CommentsViewComponent.Window(Comments.CountApproved(key), 2, "2");
        Assert.That(page, Is.EqualTo(2));
        var second = Comments.ListApproved(key, skip, pageSize);

        Assert.That(second.Select(r => r.Name), Is.EqualTo(new[] { "c3", "c4" }));
    }

    [TestCase(5, 2, "99", 3, 4)]
    [TestCase(5, 2, "0", 1, 0)]
    [TestCase(5, 2, "abc", 1, 0)]
    [TestCase(5, 0, "2", 2, 1)]
    [TestCase(5, 9999, "1", 1, 0)]
    public void Window_clamps_page_and_page_size(int total, int configured, string query, int page, int skip)
    {
        var w = CommentsViewComponent.Window(total, configured, query);
        Assert.That(w.Page, Is.EqualTo(page));
        Assert.That(w.Skip, Is.EqualTo(skip));
    }

    [Test]
    public void Deleting_the_page_deletes_its_comments()
    {
        var key = PublishPage();
        Comments.Add(Row(key, CommentStatus.Approved, 1, "a"));
        Comments.Add(Row(key, CommentStatus.Approved, 2, "b"));
        Assert.That(Comments.CountApproved(key), Is.EqualTo(2));

        var content = Services.GetRequiredService<IContentService>();
        content.Delete(content.GetById(key)!);

        Assert.That(Comments.CountApproved(key), Is.EqualTo(0));
    }

    [Test]
    public async Task Missing_antiforgery_redirects_with_error_and_keeps_the_text()
    {
        var http = new DefaultHttpContext();
        var controller = new CommentsController(null!, new FailingAntiforgery())
        {
            ControllerContext = new ControllerContext(new ActionContext(
                http, new RouteData(), new ControllerActionDescriptor { ControllerName = "Comments", ActionName = "Submit" })),
            TempData = new TempDataDictionary(http, new NoTempDataProvider()),
            Url = new LocalOnlyUrlHelper(),
        };

        var result = await controller.Submit(Guid.NewGuid(), "/blog/post", "Ana", "ana@example.com", "my long comment", null, "token");

        var redirect = result as LocalRedirectResult;
        Assert.That(redirect, Is.Not.Null);
        Assert.That(redirect!.Url, Does.Contain("bdc=error-antiforgery"));
        Assert.That(redirect.Url, Does.StartWith("/blog/post?"));
        Assert.That(controller.TempData[CommentsController.DraftBody], Is.EqualTo("my long comment"));
        Assert.That(controller.TempData[CommentsController.DraftName], Is.EqualTo("Ana"));
    }

    private sealed class FailingAntiforgery : IAntiforgery
    {
        public Task ValidateRequestAsync(HttpContext httpContext) => throw new AntiforgeryValidationException("missing token");

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => throw new NotSupportedException();

        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
    }

    private sealed class NoTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class LocalOnlyUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext => throw new NotSupportedException();

        public string? Action(UrlActionContext actionContext) => throw new NotSupportedException();

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

        public string? RouteUrl(UrlRouteContext routeContext) => throw new NotSupportedException();

        public string? Link(string? routeName, object? values) => throw new NotSupportedException();
    }
}
