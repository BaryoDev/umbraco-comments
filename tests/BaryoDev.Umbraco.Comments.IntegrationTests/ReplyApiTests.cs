using BaryoDev.Umbraco.Comments.Api;
using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.AuthorizationStatus;
using Umbraco.Cms.Infrastructure.Install;
using Umbraco.Cms.Tests.Common.Testing;
using Umbraco.Cms.Tests.Integration.Testing;
using Umbraco.Extensions;

namespace BaryoDev.Umbraco.Comments.IntegrationTests;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest)]
public class ReplyApiTests : UmbracoIntegrationTest
{
    private readonly FakeReplyEmail _email = new();

    private ICommentRepositoryThreaded Comments => GetRequiredService<ICommentRepositoryThreaded>();

    protected override void CustomTestSetup(IUmbracoBuilder builder)
    {
        builder.Services.TryAddSingleton<CommentRepository>();
        builder.Services.TryAddSingleton<ICommentRepository>(sp => sp.GetRequiredService<CommentRepository>());
        builder.Services.TryAddSingleton<ICommentRepositoryThreaded>(sp => sp.GetRequiredService<CommentRepository>());
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddUnique<ICommentReplyEmail>(_email);
        builder.Services.AddTransient<CommentReplies>();
    }

    [SetUp]
    public async Task RunPackageMigration()
    {
        _email.CanSend = true;
        _email.Sent.Clear();
        var runner = Services.GetRequiredService<PackageMigrationRunner>();
        await runner.RunPackageMigrationsIfPendingAsync("BaryoDev.Umbraco.Comments");
    }

    private static CommentRow Row(Guid key, CommentStatus status, int minute, string name, string? email = null, Guid? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentKey = key,
        Name = name,
        Email = email,
        Body = "body " + name,
        Status = (int)status,
        ParentId = parent,
        CreatedUtc = new DateTime(2026, 11, 7, 12, minute, 0, DateTimeKind.Utc),
    };

    private CommentsApiController Moderation(IUser? user, Func<object?[], object?>? permission = null)
    {
        var security = Stub.Make<IBackOfficeSecurity>(("get_CurrentUser", _ => user));
        var permissions = Stub.Make<IContentPermissionService>(("AuthorizeAccessAsync",
            permission ?? (_ => Task.FromResult(ContentAuthorizationStatus.UnauthorizedMissingPermissionAccess))));
        var controller = ActivatorUtilities.CreateInstance<CommentsApiController>(Services, new FixedAccessor(security), permissions);
        // The endpoints read HttpContext.RequestAborted, as they do in a real request.
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private CommentsApiController Admin() =>
        Moderation(GetRequiredService<IUserService>().GetAsync(Constants.Security.SuperUserKey).GetAwaiter().GetResult());

    // A signed-in user in no group, so only the Browse check can let them in.
    private CommentsApiController Restricted(params Guid[] browsable) =>
        Moderation(Stub.Make<IUser>(), a => Task.FromResult(
            browsable.Contains((Guid)a[1]!) ? ContentAuthorizationStatus.Success : ContentAuthorizationStatus.UnauthorizedMissingPermissionAccess));

    private static List<CommentItem> Items(IActionResult result) => ((CommentList)((OkObjectResult)result).Value!).Items.ToList();

    [Test]
    public async Task Reply_endpoint_saves_and_reports_email()
    {
        var page = Guid.NewGuid();
        var parent = Row(page, CommentStatus.Approved, 1, "Ana", "ana@example.com");
        Comments.Add(parent);

        var result = await Admin().Reply(parent.Id, new ReplyRequest("Thanks for reading."));

        var created = (ObjectResult)result;
        Assert.That(created.StatusCode, Is.EqualTo(201));
        var response = (ReplyResponse)created.Value!;
        Assert.That(response.Emailed, Is.True);
        Assert.That(response.EmailStatus, Is.EqualTo("sent"));
        CommentRow saved = Comments.Get(response.Id)!;
        Assert.That(saved.ParentId, Is.EqualTo(parent.Id));
        Assert.That(saved.AuthorUserKey, Is.EqualTo(Constants.Security.SuperUserKey));
        Assert.That(saved.NotifiedUtc, Is.Not.Null);
        Assert.That(_email.Sent, Is.EqualTo(new[] { response.Id }));

        var quiet = (ReplyResponse)((ObjectResult)await Admin().Reply(parent.Id, new ReplyRequest("No email please.", Notify: false))).Value!;
        Assert.That(quiet.Emailed, Is.False);
        Assert.That(quiet.EmailStatus, Is.Null);
        Assert.That(_email.Sent, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Reply_endpoint_refuses_a_user_without_Browse()
    {
        var page = Guid.NewGuid();
        var parent = Row(page, CommentStatus.Approved, 1, "Ana", "ana@example.com");
        Comments.Add(parent);

        Assert.That(await Restricted().Reply(parent.Id, new ReplyRequest("Hello there.")), Is.InstanceOf<ForbidResult>());
        Assert.That(await Restricted().Notify(parent.Id), Is.InstanceOf<ForbidResult>());
        Assert.That(Comments.ListReplies([parent.Id]), Is.Empty);
        Assert.That(_email.Sent, Is.Empty);
    }

    [Test]
    public async Task Reply_endpoint_is_400_for_a_pending_parent()
    {
        var parent = Row(Guid.NewGuid(), CommentStatus.Pending, 1, "Ana", "ana@example.com");
        Comments.Add(parent);

        var result = (BadRequestObjectResult)await Admin().Reply(parent.Id, new ReplyRequest("Hello there."));

        Assert.That(result.Value!.ToString(), Does.Contain("Only approved comments can be replied to."));
        Assert.That(Comments.ListReplies([parent.Id]), Is.Empty);
    }

    [Test]
    public async Task Notify_endpoint_is_409_the_second_time()
    {
        var parent = Row(Guid.NewGuid(), CommentStatus.Approved, 1, "Ana", "ana@example.com");
        Comments.Add(parent);
        var quiet = (ReplyResponse)((ObjectResult)await Admin().Reply(parent.Id, new ReplyRequest("Hello there.", Notify: false))).Value!;

        Assert.That(await Admin().Notify(quiet.Id), Is.InstanceOf<NoContentResult>());
        var second = await Admin().Notify(quiet.Id);

        Assert.That(second, Is.InstanceOf<ConflictObjectResult>());
        Assert.That(_email.Sent, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Notify_endpoint_is_503_without_smtp()
    {
        _email.CanSend = false;
        var parent = Row(Guid.NewGuid(), CommentStatus.Approved, 1, "Ana", "ana@example.com");
        Comments.Add(parent);
        var posted = (ReplyResponse)((ObjectResult)await Admin().Reply(parent.Id, new ReplyRequest("Hello there."))).Value!;
        Assert.That(posted.Emailed, Is.False);
        Assert.That(posted.EmailStatus, Is.EqualTo("cannotsend"));

        var result = (ObjectResult)await Admin().Notify(posted.Id);

        Assert.That(result.StatusCode, Is.EqualTo(503));
        Assert.That(Comments.Get(posted.Id)!.NotifiedUtc, Is.Null);
        Assert.That(_email.Sent, Is.Empty);
    }

    [Test]
    public async Task List_marks_replies_and_reply_permissions()
    {
        var page = Guid.NewGuid();
        var withEmail = Row(page, CommentStatus.Approved, 1, "Ana", "ana@example.com");
        var withoutEmail = Row(page, CommentStatus.Approved, 2, "Ben");
        var replyToAna = Row(page, CommentStatus.Approved, 3, "Site A", parent: withEmail.Id);
        replyToAna.AuthorUserKey = Constants.Security.SuperUserKey;
        var replyToBen = Row(page, CommentStatus.Approved, 4, "Site B", parent: withoutEmail.Id);
        replyToBen.AuthorUserKey = Constants.Security.SuperUserKey;
        var sentReply = Row(page, CommentStatus.Approved, 5, "Site C", parent: withEmail.Id);
        sentReply.AuthorUserKey = Constants.Security.SuperUserKey;
        sentReply.NotifiedUtc = new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);
        foreach (var r in new[] { withEmail, withoutEmail, replyToAna, replyToBen, sentReply })
        {
            Comments.Add(r);
        }
        Comments.Add(Row(page, CommentStatus.Pending, 6, "Waiting"));

        var approved = Items(await Admin().List(status: "approved")).ToDictionary(i => i.Name);

        Assert.That(approved["Ana"].CanReply, Is.True);
        Assert.That(approved["Ana"].ParentId, Is.Null);
        Assert.That(approved["Ana"].IsSiteReply, Is.False);
        Assert.That(approved["Ana"].CanNotify, Is.False);
        Assert.That(approved["Ben"].CanReply, Is.True);

        Assert.That(approved["Site A"].ParentId, Is.EqualTo(withEmail.Id));
        Assert.That(approved["Site A"].ParentName, Is.EqualTo("Ana"));
        Assert.That(approved["Site A"].IsSiteReply, Is.True);
        Assert.That(approved["Site A"].CanReply, Is.False);
        Assert.That(approved["Site A"].CanNotify, Is.True);
        Assert.That(approved["Site B"].ParentName, Is.EqualTo("Ben"));
        Assert.That(approved["Site B"].CanNotify, Is.False);
        Assert.That(approved["Site C"].CanNotify, Is.False);
        Assert.That(approved["Site C"].NotifiedUtc, Is.EqualTo(sentReply.NotifiedUtc));
        Assert.That(approved["Site C"].NotifiedUtc!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));

        Assert.That(Items(await Admin().List(status: "pending")).Single().CanReply, Is.False);
    }

    private sealed class FakeReplyEmail : ICommentReplyEmail
    {
        public bool CanSend { get; set; } = true;

        public List<Guid> Sent { get; } = [];

        public Task SendAsync(CommentRow parent, CommentRow reply, CancellationToken ct)
        {
            Sent.Add(reply.Id);
            return Task.CompletedTask;
        }
    }
}
