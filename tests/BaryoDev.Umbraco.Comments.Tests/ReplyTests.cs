using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BaryoDev.Umbraco.Comments.Tests;

public class ReplyTests
{
    private class FakeRepo : ICommentRepository
    {
        public List<CommentRow> Rows { get; } = [];

        public void Add(CommentRow row) => Rows.Add(row);
        public IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int skip, int take) => [];
        public int CountApproved(Guid contentKey) => 0;
        public int DeleteForContent(Guid contentKey) => 0;
        public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take) => [];
        public CommentCounts Counts() => new(0, 0, 0);
        public CommentRow? Get(Guid id) => Rows.FirstOrDefault(r => r.Id == id);
        public bool SetStatus(Guid id, CommentStatus status) => false;
        public bool Delete(Guid id) => false;
    }

    private sealed class ThreadedRepo : FakeRepo, ICommentRepositoryThreaded
    {
        public IReadOnlyList<CommentRow> ListApprovedTopLevel(Guid contentKey, int skip, int take) => [];
        public int CountApprovedTopLevel(Guid contentKey) => 0;
        public IReadOnlyList<CommentRow> ListReplies(IReadOnlyCollection<Guid> parentIds) => [];

        public bool TryClaimNotify(Guid replyId, DateTime utc)
        {
            lock (Rows)
            {
                CommentRow? row = Get(replyId);
                if (row is null || row.ParentId is null || row.NotifiedUtc is not null)
                {
                    return false;
                }
                row.NotifiedUtc = utc;
                return true;
            }
        }

        public void ReleaseNotify(Guid replyId)
        {
            lock (Rows)
            {
                Get(replyId)!.NotifiedUtc = null;
            }
        }
    }

    private sealed class RecordingEmail : ICommentReplyEmail
    {
        public bool CanSend { get; set; } = true;
        public bool Throw { get; set; }
        public List<(CommentRow Parent, CommentRow Reply)> Sent { get; } = [];

        public Task SendAsync(CommentRow parent, CommentRow reply, CancellationToken ct)
        {
            if (Throw)
            {
                throw new InvalidOperationException("smtp down");
            }
            Sent.Add((parent, reply));
            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReplyAuthor Author = new(Guid.NewGuid(), "Arnel");

    private static CommentRow Parent(CommentStatus status = CommentStatus.Approved, string? email = "reader@example.com", Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentKey = Guid.NewGuid(),
        Name = "Reader",
        Email = email,
        Body = "I would like to try these.",
        Status = (int)status,
        CreatedUtc = Now.UtcDateTime.AddDays(-1),
        ParentId = parentId,
    };

    private static CommentReplies Build(ICommentRepository repo, ICommentReplyEmail email, CommentsOptions? options = null) =>
        new(repo, email, Options.Create(options ?? new CommentsOptions()), new FakeTimeProvider(Now), NullLogger<CommentReplies>.Instance);

    [Fact]
    public async Task Reply_to_an_approved_comment_is_saved_approved_with_author()
    {
        var repo = new ThreadedRepo();
        var parent = Parent();
        repo.Add(parent);

        var outcome = await Build(repo, new RecordingEmail()).ReplyAsync(parent.Id, "  Thanks, tell me what breaks.  ", false, Author, TestContext.Current.CancellationToken);

        Assert.Equal(ReplyResult.Saved, outcome.Result);
        CommentRow saved = Assert.Single(repo.Rows, r => r.ParentId == parent.Id);
        Assert.Equal(outcome.ReplyId, saved.Id);
        Assert.Equal(parent.ContentKey, saved.ContentKey);
        Assert.Equal(Author.UserKey, saved.AuthorUserKey);
        Assert.Equal((int)CommentStatus.Approved, saved.Status);
        Assert.Null(saved.Email);
        Assert.Equal("Arnel", saved.Name);
        Assert.Equal("Thanks, tell me what breaks.", saved.Body);
        Assert.Equal(Now.UtcDateTime, saved.CreatedUtc);
    }

    [Theory]
    [InlineData("missing", ReplyResult.NotFound)]
    [InlineData("pending", ReplyResult.NotApproved)]
    [InlineData("spam", ReplyResult.NotApproved)]
    [InlineData("reply", ReplyResult.IsReply)]
    public async Task Reply_to_pending_spam_missing_or_a_reply_is_refused(string kind, ReplyResult expected)
    {
        var repo = new ThreadedRepo();
        Guid target = Guid.NewGuid();
        switch (kind)
        {
            case "reply":
                var top = Parent();
                var reply = Parent(parentId: top.Id);
                repo.Add(top);
                repo.Add(reply);
                target = reply.Id;
                break;
            case "pending" or "spam":
                var row = Parent(kind == "pending" ? CommentStatus.Pending : CommentStatus.Spam);
                repo.Add(row);
                target = row.Id;
                break;
        }
        int before = repo.Rows.Count;

        var outcome = await Build(repo, new RecordingEmail()).ReplyAsync(target, "A fine reply", true, Author, TestContext.Current.CancellationToken);

        Assert.Equal(expected, outcome.Result);
        Assert.Null(outcome.ReplyId);
        Assert.Equal(before, repo.Rows.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    [InlineData("x")]
    public async Task Reply_body_outside_limits_is_BodyInvalid(string? body)
    {
        var repo = new ThreadedRepo();
        var parent = Parent();
        repo.Add(parent);

        var outcome = await Build(repo, new RecordingEmail()).ReplyAsync(parent.Id, body, false, Author, TestContext.Current.CancellationToken);

        Assert.Equal(ReplyResult.BodyInvalid, outcome.Result);
        Assert.Single(repo.Rows);
    }

    [Fact]
    public async Task Reply_body_over_the_limit_is_BodyInvalid()
    {
        var repo = new ThreadedRepo();
        var parent = Parent();
        repo.Add(parent);
        var options = new CommentsOptions { MaxBodyLength = 10 };

        var outcome = await Build(repo, new RecordingEmail(), options).ReplyAsync(parent.Id, new string('a', 11), false, Author, TestContext.Current.CancellationToken);

        Assert.Equal(ReplyResult.BodyInvalid, outcome.Result);
    }

    [Fact]
    public async Task Reply_saves_even_when_the_email_fails()
    {
        var repo = new ThreadedRepo();
        var parent = Parent();
        repo.Add(parent);

        var outcome = await Build(repo, new RecordingEmail { Throw = true }).ReplyAsync(parent.Id, "Thanks for trying it", true, Author, TestContext.Current.CancellationToken);

        Assert.Equal(ReplyResult.Saved, outcome.Result);
        Assert.Equal(NotifyResult.Failed, outcome.Email);
        Assert.Equal(2, repo.Rows.Count);
        Assert.Null(repo.Get(outcome.ReplyId!.Value)!.NotifiedUtc);
    }

    [Fact]
    public async Task Reply_without_notify_or_without_parent_email_sends_nothing()
    {
        var repo = new ThreadedRepo();
        var withEmail = Parent();
        var noEmail = Parent(email: null);
        repo.Add(withEmail);
        repo.Add(noEmail);
        var email = new RecordingEmail();
        var service = Build(repo, email);

        var quiet = await service.ReplyAsync(withEmail.Id, "Thanks for trying it", false, Author, TestContext.Current.CancellationToken);
        var none = await service.ReplyAsync(noEmail.Id, "Thanks for trying it", true, Author, TestContext.Current.CancellationToken);

        Assert.Equal(ReplyResult.Saved, quiet.Result);
        Assert.Null(quiet.Email);
        Assert.Equal(ReplyResult.Saved, none.Result);
        Assert.Null(none.Email);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task Notify_twice_sends_once()
    {
        var repo = new ThreadedRepo();
        var parent = Parent();
        repo.Add(parent);
        var email = new RecordingEmail();
        var service = Build(repo, email);
        var outcome = await service.ReplyAsync(parent.Id, "Thanks for trying it", false, Author, TestContext.Current.CancellationToken);

        var first = await service.NotifyAsync(outcome.ReplyId!.Value, TestContext.Current.CancellationToken);
        var second = await service.NotifyAsync(outcome.ReplyId!.Value, TestContext.Current.CancellationToken);

        Assert.Equal(NotifyResult.Sent, first);
        Assert.Equal(NotifyResult.AlreadySent, second);
        Assert.Single(email.Sent);
        Assert.NotNull(repo.Get(outcome.ReplyId!.Value)!.NotifiedUtc);
    }

    [Fact]
    public async Task Notify_without_smtp_is_CannotSend_and_leaves_it_unsent()
    {
        var repo = new ThreadedRepo();
        var parent = Parent();
        repo.Add(parent);
        var email = new RecordingEmail { CanSend = false };
        var service = Build(repo, email);

        var outcome = await service.ReplyAsync(parent.Id, "Thanks for trying it", true, Author, TestContext.Current.CancellationToken);

        Assert.Equal(ReplyResult.Saved, outcome.Result);
        Assert.Equal(NotifyResult.CannotSend, outcome.Email);
        Assert.Null(repo.Get(outcome.ReplyId!.Value)!.NotifiedUtc);
        Assert.Empty(email.Sent);

        email.CanSend = true;
        Assert.Equal(NotifyResult.Sent, await service.NotifyAsync(outcome.ReplyId!.Value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Notify_failure_releases_the_claim()
    {
        var repo = new ThreadedRepo();
        var parent = Parent();
        repo.Add(parent);
        var email = new RecordingEmail { Throw = true };
        var service = Build(repo, email);
        var outcome = await service.ReplyAsync(parent.Id, "Thanks for trying it", false, Author, TestContext.Current.CancellationToken);

        var failed = await service.NotifyAsync(outcome.ReplyId!.Value, TestContext.Current.CancellationToken);

        Assert.Equal(NotifyResult.Failed, failed);
        Assert.Null(repo.Get(outcome.ReplyId!.Value)!.NotifiedUtc);

        email.Throw = false;
        Assert.Equal(NotifyResult.Sent, await service.NotifyAsync(outcome.ReplyId!.Value, TestContext.Current.CancellationToken));
        Assert.Single(email.Sent);
    }

    [Fact]
    public void ReplyLink_uses_PublicBaseUrl()
    {
        Guid id = Guid.NewGuid();

        Assert.Equal($"https://baryo.dev/blog/post/#comment-{id}", CommentReplyEmail.ReplyLink("https://baryo.dev/", "/blog/post/", id));
    }

    [Fact]
    public void ReplyLink_keeps_only_the_path_of_an_absolute_page_url()
    {
        // Umbraco returns an absolute URL in Relative mode when the page's domain is not the request's host.
        Guid id = Guid.NewGuid();

        Assert.Equal($"https://baryo.dev/blog/post/?a=1#comment-{id}",
            CommentReplyEmail.ReplyLink("https://baryo.dev", "https://edit.baryo.dev/blog/post/?a=1", id));
        Assert.Equal($"https://edit.baryo.dev/blog/post/#comment-{id}",
            CommentReplyEmail.ReplyLink(null, "https://edit.baryo.dev/blog/post/", id));
    }

    [Fact]
    public void Email_encodes_html_and_flattens_the_subject()
    {
        var parent = Parent();
        parent.Name = "<b>x</b>";
        var reply = Parent(parentId: parent.Id);
        reply.Name = "Arnel";
        reply.Body = "line one\nline <i>two</i>";

        var (subject, html) = CommentReplyEmail.Compose(parent, reply, "A\r\nBcc: y", "https://baryo.dev/p/#comment-1");

        Assert.DoesNotContain('\r', subject);
        Assert.DoesNotContain('\n', subject);
        Assert.Contains("&lt;b&gt;", html);
        Assert.DoesNotContain("<b>x</b>", html);
        Assert.Contains("&lt;i&gt;two&lt;/i&gt;", html);
        Assert.Contains("<br>", html);
        Assert.Contains("Read it on the page", html);
    }

    [Fact]
    public void Options_reject_a_bad_ReplyTo_or_PublicBaseUrl()
    {
        var validator = new CommentsOptionsValidator();

        Assert.True(validator.Validate(null, new CommentsOptions()).Succeeded);
        Assert.True(validator.Validate(null, new CommentsOptions { ReplyTo = "me@baryo.dev", PublicBaseUrl = "https://baryo.dev" }).Succeeded);
        Assert.True(validator.Validate(null, new CommentsOptions { ReplyTo = "not an address" }).Failed);
        Assert.True(validator.Validate(null, new CommentsOptions { PublicBaseUrl = "/relative" }).Failed);
        Assert.True(validator.Validate(null, new CommentsOptions { PublicBaseUrl = "ftp://baryo.dev" }).Failed);
        Assert.True(validator.Validate(null, new CommentsOptions { PublicBaseUrl = "baryo.dev" }).Failed);
    }

    [Fact]
    public async Task Unsupported_repository_gets_Unsupported()
    {
        var repo = new FakeRepo();
        var parent = Parent();
        repo.Add(parent);
        var email = new RecordingEmail();
        var service = Build(repo, email);

        var reply = await service.ReplyAsync(parent.Id, "Thanks for trying it", true, Author, TestContext.Current.CancellationToken);
        var notify = await service.NotifyAsync(parent.Id, TestContext.Current.CancellationToken);

        Assert.Equal(ReplyResult.Unsupported, reply.Result);
        Assert.Equal(NotifyResult.Unsupported, notify);
        Assert.Single(repo.Rows);
        Assert.Empty(email.Sent);
    }
}
