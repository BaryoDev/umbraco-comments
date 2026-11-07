using System.Net;
using System.Reflection;
using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.Comments.Tests;

public class SubmissionTests
{
    private sealed class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }

    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        public FormToken Tokens { get; }
        public CommentSubmission Submission { get; }

        public Rig(CommentsOptions options)
        {
            Tokens = new FormToken(new EphemeralDataProtectionProvider(), Time);
            Submission = new CommentSubmission(
                DispatchProxy.Create<ICommentRepository, NullProxy>(),
                new RateLimiter(Time),
                Tokens,
                DispatchProxy.Create<IContentService, NullProxy>(),
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
