using System.Net;
using System.Reflection;
using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed class RecordingRepository : ICommentRepositoryScoped
    {
        public List<CommentRow> Added { get; } = [];

        public void Add(CommentRow row) => Added.Add(row);

        public IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int skip, int take) => [];

        public int CountApproved(Guid contentKey) => 0;

        public int DeleteForContent(Guid contentKey) => 0;

        public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take) => [];

        public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take, IReadOnlyCollection<Guid> contentKeys) => [];

        public int PendingTotal { get; set; }

        public int PendingForPage { get; set; }

        public CommentCounts Counts() => new(PendingTotal, 0, 0);

        public CommentCounts Counts(IReadOnlyCollection<Guid> contentKeys) => new(PendingForPage, 0, 0);

        public IReadOnlyList<Guid> ContentKeys() => [];

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

        public Rig(CommentsOptions options, bool pagePublished = false, ICommentSpamCheck? spam = null)
        {
            Tokens = new FormToken(new EphemeralDataProtectionProvider(), Time);
            Submission = new CommentSubmission(
                Repository,
                new RateLimiter(Time),
                Tokens,
                pagePublished ? DispatchProxy.Create<IContentService, PublishedContentProxy>() : DispatchProxy.Create<IContentService, ContentProxy>(),
                Options.Create(options),
                Time,
                spam ?? new AllowAllSpamCheck());
        }

        public SubmitResult SendToken(string token, int waitSeconds = 10)
        {
            Time.Advance(TimeSpan.FromSeconds(waitSeconds));
            return Submission.Submit(new CommentInput(PageKey, "Ana", null, "Hello there", null, token, "1.1.1.1"));
        }

        public Guid PageKey { get; } = Guid.NewGuid();

        public SubmitResult Send(string? name = "Ana", string? email = null, string? body = "Hello there", string? honeypot = null, string client = "1.1.1.1", int waitSeconds = 10, Guid? tokenFor = null)
        {
            string token = Tokens.Issue(tokenFor ?? PageKey);
            Time.Advance(TimeSpan.FromSeconds(waitSeconds));
            return Submission.Submit(new CommentInput(PageKey, name, email, body, honeypot, token, client));
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
    public void Bidi_overrides_are_dropped_from_names_but_kept_in_bodies()
    {
        Assert.Equal("evil", CommentSubmission.Clean("\u202Eevil\u2066", singleLine: true));
        Assert.Equal("a\u2028b", CommentSubmission.Clean("a\u2028b", singleLine: false));
        Assert.Equal("\U0001F468\u200D\U0001F469", CommentSubmission.Clean("\U0001F468\u200D\U0001F469", singleLine: false));

        var rig = new Rig(new CommentsOptions());
        Assert.Equal(SubmitResult.NameRequired, rig.Send(name: "\u202E\u202D"));
    }

    [Fact]
    public void A_token_issued_for_one_page_is_refused_on_another()
    {
        var rig = new Rig(new CommentsOptions());

        Assert.Equal(SubmitResult.Invalid, rig.Send(tokenFor: Guid.NewGuid()));
        Assert.Equal(SubmitResult.NoSuchPage, rig.Send());
    }

    [Fact]
    public void A_full_limiter_evicts_old_clients_and_still_serves_new_ones()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time, maxTrackedClients: 100);
        var window = TimeSpan.FromMinutes(10);

        for (int i = 0; i < 99; i++)
        {
            Assert.True(limiter.TryAcquire("client-" + i, 1, window));
            time.Advance(TimeSpan.FromSeconds(1));
        }
        // The most recent client uses up its allowance.
        Assert.True(limiter.TryAcquire("heavy", 1, window));
        Assert.False(limiter.TryAcquire("heavy", 1, window));
        time.Advance(TimeSpan.FromSeconds(1));

        // The table is at the cap. New clients are let in, and the table stays bounded.
        for (int i = 0; i < 100_000; i++)
        {
            Assert.True(limiter.TryAcquire("new-" + i, 1, window), "new client " + i);
        }
        Assert.True(limiter.TrackedClients <= 100);
    }

    [Fact]
    public void An_over_limit_client_stays_refused_when_the_table_fills()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(time, maxTrackedClients: 100);
        var window = TimeSpan.FromMinutes(10);
        for (int i = 0; i < 90; i++)
        {
            limiter.TryAcquire("client-" + i, 1, window);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.True(limiter.TryAcquire("heavy", 1, window));
        time.Advance(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 20; i++)
        {
            Assert.True(limiter.TryAcquire("late-" + i, 1, window));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(limiter.TryAcquire("heavy", 1, window));
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

    private sealed class FixedSpamCheck(SpamVerdict verdict) : ICommentSpamCheck
    {
        public CommentSpamContext? Seen { get; private set; }

        public ValueTask<SpamVerdict> CheckAsync(CommentSpamContext context, CancellationToken cancellationToken)
        {
            Seen = context;
            return ValueTask.FromResult(verdict);
        }
    }

    [Fact]
    public void A_token_is_accepted_once()
    {
        var rig = new Rig(new CommentsOptions(), pagePublished: true);
        string token = rig.Tokens.Issue(rig.PageKey);

        Assert.Equal(SubmitResult.Pending, rig.SendToken(token));
        Assert.Equal(SubmitResult.Reused, rig.SendToken(token));
        Assert.Single(rig.Repository.Added);
    }

    [Fact]
    public void Every_token_carries_its_own_nonce()
    {
        var provider = new EphemeralDataProtectionProvider();
        var time = new FakeTimeProvider();
        var tokens = new FormToken(provider, time);
        var key = Guid.NewGuid();
        var protector = provider.CreateProtector("BaryoDev.Umbraco.Comments.FormToken");

        string[] a = protector.Unprotect(tokens.Issue(key)).Split('|');
        string[] b = protector.Unprotect(tokens.Issue(key)).Split('|');

        Assert.Equal(a[0], b[0]);
        Assert.Equal(a[1], b[1]);
        Assert.NotEmpty(a[2]);
        Assert.NotEqual(a[2], b[2]);
    }

    [Fact]
    public void Two_tokens_issued_in_the_same_second_are_both_accepted_once()
    {
        var time = new FakeTimeProvider();
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), time);
        var key = Guid.NewGuid();
        string first = tokens.Issue(key);
        string second = tokens.Issue(key);
        var maxAge = TimeSpan.FromMinutes(10);

        Assert.Equal(TokenUse.Accepted, tokens.Use(first, key, maxAge));
        Assert.Equal(TokenUse.Accepted, tokens.Use(second, key, maxAge));
        Assert.Equal(TokenUse.Reused, tokens.Use(first, key, maxAge));
    }

    [Fact]
    public void Concurrent_use_of_one_token_is_accepted_once()
    {
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), new FakeTimeProvider());
        var key = Guid.NewGuid();
        string token = tokens.Issue(key);
        var results = new TokenUse[64];

        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            i => results[i] = tokens.Use(token, key, TimeSpan.FromMinutes(10)));

        Assert.Equal(1, results.Count(r => r == TokenUse.Accepted));
        Assert.Equal(results.Length - 1, results.Count(r => r == TokenUse.Reused));
    }

    [Fact]
    public void Submit_result_keeps_the_numbers_it_had_in_0_1_0()
    {
        var v010 = new (string Name, int Value)[]
        {
            ("Pending", 0), ("Approved", 1), ("Dropped", 2), ("Invalid", 3), ("TooFast", 4), ("Expired", 5),
            ("RateLimited", 6), ("NoSuchPage", 7), ("NameRequired", 8), ("EmailRequired", 9), ("EmailInvalid", 10), ("BodyInvalid", 11),
        };
        foreach (var (name, value) in v010)
        {
            Assert.Equal(value, (int)Enum.Parse<SubmitResult>(name));
        }
        Assert.Equal(12, (int)SubmitResult.Reused);
        Assert.Equal(13, (int)SubmitResult.Paused);
        Assert.Equal(14, (int)SubmitResult.Rejected);
    }

    private sealed class ThrowingSpamCheck : ICommentSpamCheck
    {
        public ValueTask<SpamVerdict> CheckAsync(CommentSpamContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("hook failed");
    }

    private sealed class RejectOnceSpamCheck : ICommentSpamCheck
    {
        private int _calls;

        public ValueTask<SpamVerdict> CheckAsync(CommentSpamContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_calls++ == 0 ? SpamVerdict.Reject : SpamVerdict.Allow);
    }

    private sealed class ThrowingRepository : ICommentRepository
    {
        private readonly RecordingRepository _inner = new();
        public bool Throw { get; set; } = true;
        public List<CommentRow> Added => _inner.Added;

        public void Add(CommentRow row)
        {
            if (Throw)
            {
                throw new InvalidOperationException("db down");
            }
            _inner.Add(row);
        }

        public IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int skip, int take) => [];
        public int CountApproved(Guid contentKey) => 0;
        public int DeleteForContent(Guid contentKey) => 0;
        public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take) => [];
        public CommentCounts Counts() => new(0, 0, 0);
        public CommentRow? Get(Guid id) => null;
        public bool SetStatus(Guid id, CommentStatus status) => false;
        public bool Delete(Guid id) => false;
    }

    [Fact]
    public void A_throwing_hook_saves_nothing_and_releases_the_token()
    {
        var rig = new Rig(new CommentsOptions(), pagePublished: true, spam: new ThrowingSpamCheck());
        string token = rig.Tokens.Issue(rig.PageKey);

        Assert.Throws<InvalidOperationException>(() => rig.SendToken(token));
        Assert.Empty(rig.Repository.Added);
        Assert.Equal(0, rig.Tokens.UsedCount);
    }

    [Fact]
    public void A_rejected_comment_releases_the_token_so_a_resend_works()
    {
        var rig = new Rig(new CommentsOptions(), pagePublished: true, spam: new RejectOnceSpamCheck());
        string token = rig.Tokens.Issue(rig.PageKey);

        Assert.Equal(SubmitResult.Rejected, rig.SendToken(token));
        Assert.Equal(SubmitResult.Pending, rig.SendToken(token));
        Assert.Single(rig.Repository.Added);
    }

    [Fact]
    public void A_paused_comment_releases_the_token_so_a_resend_works_after_the_cap_clears()
    {
        var rig = new Rig(new CommentsOptions { MaxPendingPerPage = 100 }, pagePublished: true);
        string token = rig.Tokens.Issue(rig.PageKey);
        rig.Repository.PendingForPage = 100;

        Assert.Equal(SubmitResult.Paused, rig.SendToken(token));
        rig.Repository.PendingForPage = 0;
        Assert.Equal(SubmitResult.Pending, rig.SendToken(token));
        Assert.Single(rig.Repository.Added);
    }

    [Fact]
    public void A_failed_save_releases_the_token()
    {
        var time = new FakeTimeProvider();
        var repository = new ThrowingRepository();
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), time);
        var key = Guid.NewGuid();
        var submission = new CommentSubmission(repository, new RateLimiter(time), tokens,
            DispatchProxy.Create<IContentService, PublishedContentProxy>(), Options.Create(new CommentsOptions()), time);
        string token = tokens.Issue(key);
        time.Advance(TimeSpan.FromSeconds(10));
        var input = new CommentInput(key, "Ana", null, "Hello there", null, token, "1.1.1.1");

        Assert.Throws<InvalidOperationException>(() => submission.Submit(input));
        repository.Throw = false;
        Assert.Equal(SubmitResult.Pending, submission.Submit(input));
        Assert.Single(repository.Added);
    }

    [Fact]
    public void An_unknown_verdict_value_fails_closed()
    {
        var rig = new Rig(new CommentsOptions(), pagePublished: true, spam: new FixedSpamCheck((SpamVerdict)99));

        Assert.Equal(SubmitResult.Rejected, rig.Send());
        Assert.Empty(rig.Repository.Added);
    }

    [Fact]
    public void A_registered_spam_check_replaces_the_default()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICommentSpamCheck, ThrowingSpamCheck>();
        CommentsComposer.AddServices(services);

        Assert.IsType<ThrowingSpamCheck>(services.BuildServiceProvider().GetRequiredService<ICommentSpamCheck>());
    }

    [Fact]
    public void A_token_in_the_old_two_part_format_is_invalid()
    {
        var provider = new EphemeralDataProtectionProvider();
        var time = new FakeTimeProvider();
        var key = Guid.NewGuid();
        string old = provider.CreateProtector("BaryoDev.Umbraco.Comments.FormToken")
            .Protect(time.GetUtcNow().ToUnixTimeSeconds() + "|" + key.ToString("N"));
        var tokens = new FormToken(provider, time);

        Assert.Null(tokens.Age(old, key));
        Assert.Equal(TokenUse.Invalid, tokens.Use(old, key, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_full_used_token_table_evicts_expired_entries_then_fails_closed()
    {
        var time = new FakeTimeProvider();
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), time, maxUsedTokens: 3);
        var key = Guid.NewGuid();
        var maxAge = TimeSpan.FromMinutes(10);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(TokenUse.Accepted, tokens.Use(tokens.Issue(key), key, maxAge));
        }
        Assert.Equal(TokenUse.Full, tokens.Use(tokens.Issue(key), key, maxAge));
        Assert.Equal(3, tokens.UsedCount);

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(TokenUse.Accepted, tokens.Use(tokens.Issue(key), key, maxAge));
        Assert.Equal(1, tokens.UsedCount);
    }

    [Fact]
    public void A_full_used_token_table_gives_the_expired_result()
    {
        var rig = new Rig(new CommentsOptions { RateLimitMaxPosts = 100 }, pagePublished: true);
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), rig.Time, maxUsedTokens: 1);
        var submission = new CommentSubmission(rig.Repository, new RateLimiter(rig.Time), tokens,
            DispatchProxy.Create<IContentService, PublishedContentProxy>(), Options.Create(new CommentsOptions()), rig.Time);

        SubmitResult Send()
        {
            string token = tokens.Issue(rig.PageKey);
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            return submission.Submit(new CommentInput(rig.PageKey, "Ana", null, "Hello there", null, token, "1.1.1.1"));
        }

        Assert.Equal(SubmitResult.Pending, Send());
        Assert.Equal(SubmitResult.Expired, Send());
        Assert.Single(rig.Repository.Added);
    }

    [Fact]
    public void The_pending_cap_per_page_refuses_and_writes_nothing()
    {
        var rig = new Rig(new CommentsOptions { MaxPendingPerPage = 100 }, pagePublished: true);
        rig.Repository.PendingForPage = 99;
        Assert.Equal(SubmitResult.Pending, rig.Send());

        rig.Repository.PendingForPage = 100;
        Assert.Equal(SubmitResult.Paused, rig.Send());
        Assert.Single(rig.Repository.Added);
    }

    [Fact]
    public void The_pending_cap_in_total_refuses_and_writes_nothing()
    {
        var rig = new Rig(new CommentsOptions { MaxPendingTotal = 5000 }, pagePublished: true);
        rig.Repository.PendingTotal = 5000;

        Assert.Equal(SubmitResult.Paused, rig.Send());
        Assert.Empty(rig.Repository.Added);
    }

    [Fact]
    public void A_null_cap_means_no_cap_and_the_defaults_are_set()
    {
        var defaults = new CommentsOptions();
        Assert.Equal(100, defaults.MaxPendingPerPage);
        Assert.Equal(5000, defaults.MaxPendingTotal);

        var rig = new Rig(new CommentsOptions { MaxPendingPerPage = null, MaxPendingTotal = null }, pagePublished: true);
        rig.Repository.PendingForPage = 1_000_000;
        rig.Repository.PendingTotal = 1_000_000;
        Assert.Equal(SubmitResult.Pending, rig.Send());
    }

    [Fact]
    public void The_cap_does_not_apply_to_auto_approved_comments()
    {
        var rig = new Rig(new CommentsOptions { AutoApprove = true, MaxPendingTotal = 0 }, pagePublished: true);
        Assert.Equal(SubmitResult.Approved, rig.Send());
    }

    [Fact]
    public void Spam_hook_allow_saves_as_usual_and_sees_the_cleaned_comment()
    {
        var spam = new FixedSpamCheck(SpamVerdict.Allow);
        var rig = new Rig(new CommentsOptions(), pagePublished: true, spam: spam);

        Assert.Equal(SubmitResult.Pending, rig.Send(name: "  Ana ", email: "ana@example.com", body: " Hello there "));
        Assert.Equal((int)CommentStatus.Pending, rig.Repository.Added.Single().Status);
        Assert.Equal("Ana", spam.Seen!.Name);
        Assert.Equal("ana@example.com", spam.Seen.Email);
        Assert.Equal("Hello there", spam.Seen.Body);
        Assert.Equal("1.1.1.1", spam.Seen.Client);
        Assert.Equal(rig.PageKey, spam.Seen.ContentKey);
    }

    [Fact]
    public void Spam_hook_spam_saves_with_the_spam_status_and_shows_the_normal_notice()
    {
        var rig = new Rig(new CommentsOptions(), pagePublished: true, spam: new FixedSpamCheck(SpamVerdict.Spam));

        Assert.Equal(SubmitResult.Dropped, rig.Send());
        Assert.Equal((int)CommentStatus.Spam, rig.Repository.Added.Single().Status);
    }

    [Fact]
    public void Spam_hook_reject_saves_nothing()
    {
        var rig = new Rig(new CommentsOptions(), pagePublished: true, spam: new FixedSpamCheck(SpamVerdict.Reject));

        Assert.Equal(SubmitResult.Rejected, rig.Send());
        Assert.Empty(rig.Repository.Added);
    }

    [Fact]
    public async Task The_default_spam_check_allows()
    {
        var verdict = await new AllowAllSpamCheck().CheckAsync(new CommentSpamContext(Guid.NewGuid(), "Ana", null, "Hi", "1.1.1.1", null), CancellationToken.None);
        Assert.Equal(SpamVerdict.Allow, verdict);
    }

    [Fact]
    public void Spam_hook_does_not_run_when_validation_fails()
    {
        var spam = new FixedSpamCheck(SpamVerdict.Allow);
        var rig = new Rig(new CommentsOptions(), pagePublished: true, spam: spam);

        Assert.Equal(SubmitResult.NameRequired, rig.Send(name: ""));
        Assert.Null(spam.Seen);
    }
}
