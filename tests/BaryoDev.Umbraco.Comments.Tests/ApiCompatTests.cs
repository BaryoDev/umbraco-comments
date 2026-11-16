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

    public class ScopeProxy : DispatchProxy
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

    private static readonly Guid Hidden = Guid.NewGuid();

    private static Task<bool> OnlyPage(Guid key) => Task.FromResult(key == Page);

    private static PlainFake WithRows() => new()
    {
        Pending = 5,
        Rows = [new CommentRow { ContentKey = Page, Body = "visible" }, new CommentRow { ContentKey = Hidden, Body = "hidden" }],
    };

    // Pins ModerationQuery.RunAsync, the Editor case on a repository that is not page-scoped (0.1.0 filtered rows per page).
    [Fact]
    public async Task An_editor_on_a_plain_repository_gets_the_rows_they_can_browse_and_zero_counts()
    {
        var repository = WithRows();

        var (rows, counts, _) = await ModerationQuery.RunAsync(repository, CommentStatus.Pending, 0, 50, ModerationQuery.Audience.Editor, OnlyPage);

        Assert.Equal(["visible"], rows.Select(r => r.Body));
        Assert.Equal(new CommentCounts(0, 0, 0), counts);
    }

    // Pins the HasMore line in the unscoped Editor arm: the page size is taken before the visibility filter.
    [Fact]
    public async Task A_full_page_that_is_entirely_hidden_still_reports_more()
    {
        var repository = new PlainFake { Rows = [new CommentRow { ContentKey = Hidden }, new CommentRow { ContentKey = Hidden }] };

        var (rows, counts, hasMore) = await ModerationQuery.RunAsync(repository, CommentStatus.Pending, 0, 2, ModerationQuery.Audience.Editor, OnlyPage);

        Assert.Empty(rows);
        Assert.Equal(new CommentCounts(0, 0, 0), counts);
        Assert.True(hasMore);
    }

    // Same line: a short unfiltered page is the last one.
    [Fact]
    public async Task A_short_unfiltered_page_reports_no_more()
    {
        var repository = new PlainFake { Rows = [new CommentRow { ContentKey = Page }] };

        var (_, _, hasMore) = await ModerationQuery.RunAsync(repository, CommentStatus.Pending, 0, 2, ModerationQuery.Audience.Editor, OnlyPage);

        Assert.False(hasMore);
    }

    // Pins the skip + take < total lines in the Administrator arm.
    [Fact]
    public async Task An_administrator_gets_HasMore_from_the_total()
    {
        var more = await ModerationQuery.RunAsync(new PlainFake { Pending = 3 }, CommentStatus.Pending, 0, 2, ModerationQuery.Audience.Administrator, OnlyPage);
        var last = await ModerationQuery.RunAsync(new PlainFake { Pending = 3 }, CommentStatus.Pending, 2, 2, ModerationQuery.Audience.Administrator, OnlyPage);

        Assert.True(more.HasMore);
        Assert.False(last.HasMore);
    }

    // Pins ModerationQuery.AudienceOf.
    [Fact]
    public void AudienceOf_maps_no_user_to_None()
    {
        Assert.Equal(ModerationQuery.Audience.None, ModerationQuery.AudienceOf(null));
    }

    [Fact]
    public void AudienceOf_maps_only_the_admin_group_to_Administrator()
    {
        Assert.Equal(ModerationQuery.Audience.Administrator, ModerationQuery.AudienceOf(["editor", global::Umbraco.Cms.Core.Constants.Security.AdminGroupAlias]));
        Assert.Equal(ModerationQuery.Audience.Editor, ModerationQuery.AudienceOf(["editor"]));
    }

    [Fact]
    public void AudienceOf_maps_a_user_with_no_groups_to_Editor_as_0_1_0_did()
    {
        Assert.Equal(ModerationQuery.Audience.Editor, ModerationQuery.AudienceOf([]));
    }

    // Pins the OperationCanceledException filter in ModerationQuery.Safe.
    [Fact]
    public async Task A_cancelled_check_is_not_swallowed()
    {
        var repository = WithRows();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ModerationQuery.RunAsync(repository, CommentStatus.Pending, 0, 50,
            ModerationQuery.Audience.Editor, _ => throw new OperationCanceledException()));
    }

    // Pins the try/catch in ModerationQuery.Safe: a failed check drops the row.
    [Fact]
    public async Task A_check_that_throws_drops_the_row()
    {
        var repository = WithRows();

        var (rows, _, _) = await ModerationQuery.RunAsync(repository, CommentStatus.Pending, 0, 50, ModerationQuery.Audience.Editor,
            key => key == Page ? throw new InvalidOperationException("permission service down") : Task.FromResult(true));

        Assert.Equal(["hidden"], rows.Select(r => r.Body));
    }

    // Pins the Administrator case: only an administrator gets the unfiltered plain list and the sitewide counts.
    [Fact]
    public async Task An_administrator_on_a_plain_repository_gets_everything_without_the_check()
    {
        var repository = WithRows();

        var (rows, counts, _) = await ModerationQuery.RunAsync(repository, CommentStatus.Pending, 0, 50, ModerationQuery.Audience.Administrator,
            _ => throw new InvalidOperationException("must not be called"));

        Assert.Equal(2, rows.Count);
        Assert.Equal(5, counts.Pending);
    }

    // Pins the default arm: no user gets nothing and the repository is not read.
    [Fact]
    public async Task No_user_gets_nothing()
    {
        var repository = WithRows();

        var (rows, counts, _) = await ModerationQuery.RunAsync(repository, CommentStatus.Pending, 0, 50, ModerationQuery.Audience.None, _ => Task.FromResult(true));

        Assert.Empty(rows);
        Assert.Equal(new CommentCounts(0, 0, 0), counts);
        Assert.Equal(0, repository.Calls);
    }

    // Pins the Editor case on a scoped repository: only the browsable keys reach the scoped query, the plain list is not used.
    [Fact]
    public async Task An_editor_on_a_scoped_repository_gets_the_scoped_query_over_browsable_pages()
    {
        var repository = new ScopedFake { Keys = [Page, Hidden] };

        await ModerationQuery.RunAsync(repository, CommentStatus.Pending, 0, 50, ModerationQuery.Audience.Editor, OnlyPage);

        Assert.Equal([Page], repository.ScopedListKeys);
        Assert.Equal(0, repository.Calls);
    }

    // Pins CommentSubmission.cs:47, tokens.Age(input.Token, input.ContentKey). The old Age(input.Token) can not read a page-bound token.
    [Fact]
    public void A_page_bound_token_that_is_too_young_is_rejected_as_too_fast()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        var tokens = new FormToken(new EphemeralDataProtectionProvider(), time);
        var submission = new CommentSubmission(new ScopedFake(), new RateLimiter(time), tokens,
            DispatchProxy.Create<IContentService, SubmissionTests.PublishedContentProxy>(), Options.Create(new CommentsOptions()), time, new AllowAllSpamCheck());
        string token = tokens.Issue(Page);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(SubmitResult.TooFast, submission.Submit(new CommentInput(Page, "Ana", null, "Hello there", null, token, "1.1.1.1")));
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
        public IReadOnlyList<CommentRow> Rows { get; set; } = [];
        public List<CommentRow> Added { get; } = [];
        public void Add(CommentRow row) => Added.Add(row);
        public IReadOnlyList<CommentRow> ListApproved(Guid contentKey, int skip, int take) => [];
        public int CountApproved(Guid contentKey) => 0;
        public int DeleteForContent(Guid contentKey) => 0;
        public IReadOnlyList<CommentRow> List(CommentStatus status, int skip, int take) { Calls++; return Rows; }
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
        public IReadOnlyList<Guid> ContentKeys() => Keys;
        public IReadOnlyList<Guid> Keys { get; set; } = [];
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
