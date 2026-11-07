using System.Net;
using System.Reflection;
using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.Comments.Tests;

public class SubmissionTests
{
    /// <summary>Only used for IContentService, whose lookup returns null so a submission ends at NoSuchPage.</summary>
    public class ContentProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    /// <summary>IContentService whose lookup returns a published page.</summary>
    public class PublishedContentProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "GetById" ? DispatchProxy.Create<IContent, PublishedPageProxy>() : null;
    }

    public class PublishedPageProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == "get_Published" ? true : null;
    }

    private sealed class RecordingRepository : ICommentRepository
    {
        public List<CommentRow> Added { get; } = [];

        public void Add(CommentRow row) => Added.Add(row);

        public IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int skip, int take) => [];

        public int CountApproved(Guid contentKey) => 0;

        public int DeleteForContent(Guid contentKey) => 0;

        public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take) => [];

        public CommentCounts Counts() => new(0, 0, 0);

        public CommentRow? Get(Guid id) => null;

        public bool SetStatus(Guid id, CommentStatus status) => false;

        public bool Delete(Guid id) => false;
    }

    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        public FormToken Tokens { get; }
        public CommentSubmission Submission { get; }
        public RecordingRepository Repository { get; } = new();

        public Rig(CommentsOptions options, bool pagePublished = false)
        {
            Tokens = new FormToken(new EphemeralDataProtectionProvider(), Time);
            Submission = new CommentSubmission(
                Repository,
                new RateLimiter(Time),
                Tokens,
                pagePublished ? DispatchProxy.Create<IContentService, PublishedContentProxy>() : DispatchProxy.Create<IContentService, ContentProxy>(),
                Options.Create(options),
                Time);
        }

        public SubmitResult Send(string? name = "Ana", string? email = null, string? body = "Hello there", string? honeypot = null, string client = "1.1.1.1", int waitSeconds = 10)
        {
            string token = Tokens.Issue();
            Time.Advance(TimeSpan.FromSeconds(waitSeconds));
            return Submission.Submit(new CommentInput(Guid.NewGuid(), name, email, body, honeypot, token, client));
        }
    }

    [Fact]
    public void Filled_honeypot_is_dropped()
    {
        var rig = new Rig(new CommentsOptions());
        Assert.Equal(SubmitResult.Dropped, rig.Send(honeypot: "http://spam.example"));
        Assert.Empty(rig.Repository.Added);
    }

    [Fact]
    public void Submit_faster_than_min_seconds_is_refused()
    {
        var rig = new Rig(new CommentsOptions { MinSecondsOnForm = 3 });
        Assert.Equal(SubmitResult.TooFast, rig.Send(waitSeconds: 1));
        Assert.Equal(SubmitResult.NoSuchPage, rig.Send(waitSeconds: 4));
    }

    [Fact]
    public void Zero_width_characters_are_stripped_before_the_empty_check()
    {
        const string zeroWidth = "\u200B\u200B\u2060\uFEFF";
        Assert.Equal(string.Empty, CommentSubmission.Clean(zeroWidth, singleLine: true));

        var rig = new Rig(new CommentsOptions());
        Assert.Equal(SubmitResult.NameRequired, rig.Send(name: zeroWidth));
        Assert.Equal(SubmitResult.BodyInvalid, rig.Send(body: zeroWidth));
    }

    [Fact]
    public void Posts_over_the_limit_are_rate_limited_per_client()
    {
        var rig = new Rig(new CommentsOptions { RateLimitMaxPosts = 2 });

        Assert.Equal(SubmitResult.NoSuchPage, rig.Send(client: "1.1.1.1"));
        Assert.Equal(SubmitResult.NoSuchPage, rig.Send(client: "1.1.1.1"));
        Assert.Equal(SubmitResult.RateLimited, rig.Send(client: "1.1.1.1"));
        Assert.Equal(SubmitResult.NoSuchPage, rig.Send(client: "2.2.2.2"));
    }

    [Fact]
    public void A_tampered_token_is_invalid()
    {
        var rig = new Rig(new CommentsOptions());
        var input = new CommentInput(Guid.NewGuid(), "Ana", null, "Hello there", null, "not-a-token", "1.1.1.1");

        Assert.Equal(SubmitResult.Invalid, rig.Submission.Submit(input));
        Assert.Equal(SubmitResult.Invalid, rig.Submission.Submit(input with { Token = null }));
    }

    [Fact]
    public void An_old_token_is_expired()
    {
        var rig = new Rig(new CommentsOptions { MaxFormAgeMinutes = 10 });

        Assert.Equal(SubmitResult.Expired, rig.Send(waitSeconds: 10 * 60 + 60));
        Assert.Equal(SubmitResult.NoSuchPage, rig.Send(waitSeconds: 9 * 60));
    }

    [Fact]
    public void Email_is_required_only_when_configured()
    {
        Assert.Equal(SubmitResult.EmailRequired, new Rig(new CommentsOptions { RequireEmail = true }).Send(email: null));
        Assert.Equal(SubmitResult.NoSuchPage, new Rig(new CommentsOptions { RequireEmail = true }).Send(email: "ana@example.com"));
        Assert.Equal(SubmitResult.NoSuchPage, new Rig(new CommentsOptions()).Send(email: null));
    }

    [Fact]
    public void Body_length_is_bounded()
    {
        var rig = new Rig(new CommentsOptions { RateLimitMaxPosts = 100 });

        Assert.Equal(SubmitResult.BodyInvalid, rig.Send(body: "a"));
        Assert.Equal(SubmitResult.NoSuchPage, rig.Send(body: "ab"));
        Assert.Equal(SubmitResult.NoSuchPage, rig.Send(body: new string('a', 2000)));
        Assert.Equal(SubmitResult.BodyInvalid, rig.Send(body: new string('a', 2001)));
    }

    [Fact]
    public void A_published_page_stores_the_comment_as_pending_or_approved()
    {
        var pending = new Rig(new CommentsOptions(), pagePublished: true);
        Assert.Equal(SubmitResult.Pending, pending.Send());
        Assert.Equal((int)CommentStatus.Pending, pending.Repository.Added.Single().Status);

        var approved = new Rig(new CommentsOptions { AutoApprove = true }, pagePublished: true);
        Assert.Equal(SubmitResult.Approved, approved.Send());
        Assert.Equal((int)CommentStatus.Approved, approved.Repository.Added.Single().Status);
    }

    [Fact]
    public void Name_limit_is_clamped_to_80_and_email_to_200()
    {
        var rig = new Rig(new CommentsOptions { MaxNameLength = 500, MaxEmailLength = 500, RateLimitMaxPosts = 100 });

        Assert.Equal(SubmitResult.NameRequired, rig.Send(name: new string('n', 81)));
        Assert.Equal(SubmitResult.NoSuchPage, rig.Send(name: new string('n', 80)));

        Assert.Equal(SubmitResult.EmailInvalid, rig.Send(email: Email(201)));
        Assert.Equal(SubmitResult.NoSuchPage, rig.Send(email: Email(200)));
    }

    [Fact]
    public void Ipv6_clients_share_a_bucket_per_64()
    {
        var limiter = new RateLimiter(new FakeTimeProvider());
        string a = CommentsController.ClientKey(IPAddress.Parse("2001:db8:1:2::1"));
        string sameNet = CommentsController.ClientKey(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff"));
        string otherNet = CommentsController.ClientKey(IPAddress.Parse("2001:db8:1:3::1"));

        Assert.Equal(a, sameNet);
        Assert.NotEqual(a, otherNet);

        var window = TimeSpan.FromMinutes(10);
        Assert.True(limiter.TryAcquire(a, 1, window));
        Assert.False(limiter.TryAcquire(sameNet, 1, window));
        Assert.True(limiter.TryAcquire(otherNet, 1, window));
    }

    /// <summary>A valid address of exactly the given length.</summary>
    private static string Email(int length)
    {
        const string local = "user";
        int domainLength = length - local.Length - 1 - 4;
        var domain = new char[domainLength];
        for (int i = 0; i < domainLength; i++)
        {
            domain[i] = (i + 1) % 41 == 0 ? '.' : 'b';
        }
        return local + "@" + new string(domain) + ".com";
    }
}
