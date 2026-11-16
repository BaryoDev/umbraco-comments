#pragma warning disable CS0618
using System.Reflection;
using BaryoDev.Umbraco.Comments.Api;
using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.Comments.Tests;

/// <summary>A 0.1.0 consumer must still compile and run against this version.</summary>
public class ApiCompatTests
{
    private static readonly Guid Page = Guid.NewGuid();

    private static string Sig(MethodInfo m) =>
        m.ReturnType.Name + " " + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name + (p.HasDefaultValue ? "=" : ""))) + ")";

    private static bool IsObsolete(MemberInfo m) => m.GetCustomAttribute<ObsoleteAttribute>() is not null;

    // FormToken

    [Fact]
    public void FormToken_keeps_the_0_1_0_Issue_and_Age()
    {
        MethodInfo issue = typeof(FormToken).GetMethod("Issue", Type.EmptyTypes)!;
        MethodInfo age = typeof(FormToken).GetMethod("Age", [typeof(string)])!;
        Assert.Equal(typeof(string), issue.ReturnType);
        Assert.Equal(typeof(TimeSpan?), age.ReturnType);
        Assert.True(IsObsolete(issue));
        Assert.True(IsObsolete(age));
        Assert.NotNull(typeof(FormToken).GetConstructor([typeof(IDataProtectionProvider), typeof(TimeProvider)]));
    }

    [Fact]
    public void Legacy_Age_reads_a_legacy_token_and_refuses_a_page_bound_one()
    {
        var time = new FakeTimeProvider();
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), time);
        string legacy = tokens.Issue();
        time.Advance(TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), tokens.Age(legacy));
        Assert.Null(tokens.Age(tokens.Issue(Page)));
        Assert.Null(tokens.Age(null));
        Assert.Null(tokens.Age("garbage"));
    }

    [Fact]
    public void An_Issue_token_is_refused_by_every_page_check()
    {
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), new FakeTimeProvider());
        string legacy = tokens.Issue();

        Assert.Null(tokens.Age(legacy, Page));
        Assert.Equal(TokenUse.Invalid, tokens.Use(legacy, Page, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void The_submit_path_refuses_an_Issue_token()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), time);
        var repository = new ScopedFake();
        var submission = new CommentSubmission(repository, new RateLimiter(time), tokens,
            DispatchProxy.Create<IContentService, SubmissionTests.PublishedContentProxy>(), Options.Create(new CommentsOptions()), time, new AllowAllSpamCheck());
        string legacy = tokens.Issue();
        time.Advance(TimeSpan.FromSeconds(10));

        SubmitResult result = submission.Submit(new CommentInput(Page, "Ana", null, "Hello there", null, legacy, "1.1.1.1"));

        Assert.Equal(SubmitResult.Invalid, result);
        Assert.Empty(repository.Added);
    }

    // ICommentRepository

    [Fact]
    public void ICommentRepository_has_exactly_the_0_1_0_members()
    {
        string[] expected =
        [
            "Void Add(CommentRow)",
            "IReadOnlyList`1 ListApproved(Guid,Int32,Int32)",
            "Int32 CountApproved(Guid)",
            "Int32 DeleteForContent(Guid)",
            "IReadOnlyList`1 List(CommentStatus,Int32,Int32)",
            "CommentCounts Counts()",
            "CommentRow Get(Guid)",
            "Boolean SetStatus(Guid,CommentStatus)",
            "Boolean Delete(Guid)",
        ];
        string[] actual = typeof(ICommentRepository).GetMethods().Select(Sig).ToArray();
        Assert.Equal(expected.Order(), actual.Order());
    }

    [Fact]
    public void ICommentRepositoryScoped_holds_the_new_members_and_the_built_in_repository_implements_it()
    {
        string[] actual = typeof(ICommentRepositoryScoped).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(Sig).ToArray();
        Assert.Equal(
            new[]
            {
                "IReadOnlyList`1 List(CommentStatus,Int32,Int32,IReadOnlyCollection`1)",
                "CommentCounts Counts(IReadOnlyCollection`1)",
                "IReadOnlyList`1 ContentKeys()",
            }.Order(),
            actual.Order());
        Assert.True(typeof(ICommentRepository).IsAssignableFrom(typeof(ICommentRepositoryScoped)));
        Assert.True(typeof(ICommentRepositoryScoped).IsAssignableFrom(typeof(CommentRepository)));
    }

    private sealed class ScopeProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    [Fact]
    public void The_built_in_repository_resolves_for_both_interfaces()
    {
        var services = new ServiceCollection();
        services.AddSingleton(DispatchProxy.Create<IScopeProvider, ScopeProxy>());
        CommentsComposer.AddServices(services);
        using ServiceProvider provider = services.BuildServiceProvider();

        var plain = provider.GetRequiredService<ICommentRepository>();
        var scoped = provider.GetRequiredService<ICommentRepositoryScoped>();

        Assert.IsType<CommentRepository>(plain);
        Assert.Same(plain, scoped);
    }

    [Fact]
    public void A_repository_without_the_scoped_interface_gets_no_scoped_query_and_returns_nothing()
    {
        var repository = new PlainFake();
        Guid[] allowed = [Page];

        var (rows, counts) = ModerationQuery.Run(repository, CommentStatus.Pending, 0, 50, allowed);

        Assert.Empty(rows);
        Assert.Equal(new CommentCounts(0, 0, 0), counts);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public void A_repository_without_the_scoped_interface_still_serves_administrators()
    {
        var repository = new PlainFake();

        ModerationQuery.Run(repository, CommentStatus.Pending, 0, 50, null);

        Assert.Equal(2, repository.Calls);
    }

    [Fact]
    public void A_scoped_repository_gets_the_scoped_query()
    {
        var repository = new ScopedFake();
        Guid[] allowed = [Page];

        ModerationQuery.Run(repository, CommentStatus.Pending, 0, 50, allowed);

        Assert.Equal(allowed, repository.ScopedListKeys);
    }

    [Fact]
    public void Without_scoped_counts_the_per_page_cap_uses_the_total()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), time);
        var repository = new PlainFake { Pending = 100 };
        var submission = new CommentSubmission(repository, new RateLimiter(time), tokens,
            DispatchProxy.Create<IContentService, SubmissionTests.PublishedContentProxy>(),
            Options.Create(new CommentsOptions { MaxPendingPerPage = 100, MaxPendingTotal = null }), time, new AllowAllSpamCheck());
        string token = tokens.Issue(Page);
        time.Advance(TimeSpan.FromSeconds(10));

        SubmitResult result = submission.Submit(new CommentInput(Page, "Ana", null, "Hello there", null, token, "1.1.1.1"));

        Assert.Equal(SubmitResult.Paused, result);
        Assert.Empty(repository.Added);
    }

    private class PlainFake : ICommentRepository
    {
        public int Calls { get; protected set; }
        public int Pending { get; set; }
        public List<CommentRow> Added { get; } = [];
        public void Add(CommentRow row) => Added.Add(row);
        public IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int skip, int take) => [];
        public int CountApproved(Guid contentKey) => 0;
        public int DeleteForContent(Guid contentKey) => 0;
        public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take) { Calls++; return []; }
        public CommentCounts Counts() { Calls++; return new CommentCounts(Pending, 0, 0); }
        public CommentRow? Get(Guid id) => null;
        public bool SetStatus(Guid id, CommentStatus status) => false;
        public bool Delete(Guid id) => false;
    }

    private sealed class ScopedFake : PlainFake, ICommentRepositoryScoped
    {
        public IReadOnlyCollection<Guid>? ScopedListKeys { get; private set; }
        public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take, IReadOnlyCollection<Guid> contentKeys)
        {
            ScopedListKeys = contentKeys;
            return [];
        }
        public CommentCounts Counts(IReadOnlyCollection<Guid> contentKeys) => new(0, 0, 0);
        public IReadOnlyList<Guid> ContentKeys() => [];
    }

    // CommentsModel

    [Fact]
    public void CommentsModel_keeps_the_0_1_0_draft_members_and_constructor()
    {
        Type[] old =
        [
            typeof(Guid), typeof(string), typeof(IReadOnlyList<CommentView>), typeof(int), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(int), typeof(int), typeof(int),
        ];
        ConstructorInfo ctor = typeof(CommentsModel).GetConstructor(old)!;
        Assert.NotNull(ctor);
        Assert.True(IsObsolete(ctor));
        foreach (string name in new[] { "DraftName", "DraftEmail", "DraftBody" })
        {
            PropertyInfo p = typeof(CommentsModel).GetProperty(name)!;
            Assert.Equal(typeof(string), p.PropertyType);
            Assert.True(IsObsolete(p));
        }

        var model = new CommentsModel(Guid.NewGuid(), "/", [], 0, null, null, "typed name", "typed@x.test", "typed body", "tok", null, false, false, 80, 200, 2000);
        Assert.Equal(string.Empty, model.DraftName);
        Assert.Equal(string.Empty, model.DraftEmail);
        Assert.Equal(string.Empty, model.DraftBody);
        Assert.Equal("tok", model.FormToken);
        Assert.Equal(2000, model.MaxBodyLength);

        model.Deconstruct(out _, out _, out _, out _, out _, out _, out string n, out string e, out string b, out string t, out _, out _, out _, out _, out _, out _);
        Assert.Equal(string.Empty, n + e + b);
        Assert.Equal("tok", t);
    }

    [Fact]
    public void CommentsModel_still_takes_the_new_constructor()
    {
        var model = new CommentsModel(Guid.NewGuid(), "/", [], 0, null, null, "tok", null, false, false, 80, 200, 2000) { DraftName = "x" };
        Assert.Equal("x", model.DraftName);
    }

    // Controller consts and RateLimiter

    [Fact]
    public void CommentsController_keeps_the_Draft_consts()
    {
        foreach (var (name, value) in new[] { ("DraftName", "bdc.name"), ("DraftEmail", "bdc.email"), ("DraftBody", "bdc.body") })
        {
            FieldInfo f = typeof(CommentsController).GetField(name)!;
            Assert.True(f.IsLiteral);
            Assert.Equal(value, f.GetRawConstantValue());
            Assert.True(IsObsolete(f));
        }
    }

    [Fact]
    public void RateLimiter_keeps_the_0_1_0_constructor()
    {
        Assert.NotNull(typeof(RateLimiter).GetConstructor([typeof(TimeProvider)]));
        Assert.NotNull(typeof(RateLimiter).GetConstructor([typeof(TimeProvider), typeof(int)]));
        var limiter = new RateLimiter(new FakeTimeProvider());
        Assert.True(limiter.TryAcquire("c", 1, TimeSpan.FromMinutes(1)));
        Assert.False(limiter.TryAcquire("c", 1, TimeSpan.FromMinutes(1)));
    }
}
