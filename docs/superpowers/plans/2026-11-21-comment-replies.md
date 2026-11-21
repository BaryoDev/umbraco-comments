# Comment Replies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Backoffice users reply to approved comments, the reply shows under the comment on the page, and the commenter gets one email if they left an address.

**Architecture:** Three nullable columns on `baryoComment` and a new repository interface carry replies. A `CommentReplies` service holds every rule (who can be replied to, body limits, send-once email) and the API and view stay thin. Email goes through Umbraco's `IEmailSender` behind a replaceable `ICommentReplyEmail`.

**Tech Stack:** .NET 10, Umbraco 17, NPoco, xUnit (unit), NUnit `UmbracoIntegrationTest` (integration), plain custom element JS for the dashboard.

**Spec:** `docs/superpowers/specs/2026-11-21-comment-replies-design.md`

## Global Constraints

- Every 0.2.0 public member keeps its signature and meaning. New fields on existing public records are `init` properties, never new positional parameters. `ICommentRepository`, `ICommentRepositoryScoped` and `ICommentRetentionStore` get no new members.
- No new package dependencies.
- Copy rules: plain words, no em dashes, no arrow glyphs.
- Implementers build only (`dotnet build -c Release`). The session queues tests: `brain queue add ~/repos/BaryoDev.Umbraco.Comments <test project dir> --filter <ClassOrMethod>`. CI runs the full suite on push.
- Commits: short imperative subject, no trailers.

## Review Focus

1. **Double send.** A double click or two moderators press Send email at once, and the commenter must still get exactly one email. Pinned by `Notify_twice_sends_once` (Task 2) and `TryClaimNotify_succeeds_once` (Task 1).
2. **No SMTP.** Umbraco's `EmailSender.SendAsync` returns silently when SMTP is not configured. The reply must report "not emailed" and stay sendable later, never "emailed". Pinned by `Notify_without_smtp_is_CannotSend_and_leaves_it_unsent` (Task 2).
3. **Wrong link host.** The API runs on the backoffice host (`edit.baryo.dev`, behind Cloudflare Access), so an absolute URL built from the request would send readers to a login wall. Pinned by `ReplyLink_uses_PublicBaseUrl` (Task 2).
4. **Hostile names.** A commenter name or page name holding `<script>`, or a subject holding CR/LF, must come out encoded in the body and on one line in the subject. Pinned by `Email_encodes_html_and_flattens_the_subject` (Task 2).
5. **Parent gone or spam.** Deleting a comment removes its replies. Marking it spam hides them. Retention deleting old spam leaves no orphan replies. Pinned by `Delete_removes_replies`, `Retention_removes_orphan_replies` (Task 1) and `Replies_of_a_spam_comment_are_hidden` (Task 4, `ReplyPagingTests`).

---

### Task 1: Reply columns, migration and threaded repository

**Files:**
- Modify: `src/BaryoDev.Umbraco.Comments/Data/CommentRow.cs`
- Create: `src/BaryoDev.Umbraco.Comments/Data/AddReplyColumns.cs`
- Modify: `src/BaryoDev.Umbraco.Comments/Data/CommentsMigrationPlan.cs`
- Create: `src/BaryoDev.Umbraco.Comments/Data/ICommentRepositoryThreaded.cs`
- Modify: `src/BaryoDev.Umbraco.Comments/Data/CommentRepository.cs` (implement the new interface, cascade in `Delete`)
- Modify: `src/BaryoDev.Umbraco.Comments/Data/ICommentRetentionStore.cs` (orphan cleanup in `DeleteOlderThan`)
- Modify: `src/BaryoDev.Umbraco.Comments/CommentsComposer.cs` (register `ICommentRepositoryThreaded` to the same `CommentRepository` singleton)
- Test: `tests/BaryoDev.Umbraco.Comments.IntegrationTests/ReplyDataTests.cs` (new, same base and setup as `CommentFlowTests`)

**Interfaces:**
- Produces, on `CommentRow`:
  - `Guid? ParentId`, column `ParentId`, nullable, index `IX_baryoComment_ParentId`
  - `Guid? AuthorUserKey`, column `AuthorUserKey`, nullable
  - `DateTime? NotifiedUtc`, column `NotifiedUtc`, nullable
- Produces:

```csharp
public interface ICommentRepositoryThreaded : ICommentRepository
{
    IReadOnlyList<CommentRow> ListApprovedTopLevel(Guid contentKey, int skip, int take); // ParentId IS NULL, CreatedUtc ASC, Id ASC
    int CountApprovedTopLevel(Guid contentKey);
    IReadOnlyList<CommentRow> ListReplies(IReadOnlyCollection<Guid> parentIds);        // Status Approved, CreatedUtc ASC, Id ASC, batched by CommentRepository.KeyBatch
    bool TryClaimNotify(Guid replyId, DateTime utc);   // UPDATE ... SET NotifiedUtc=@utc WHERE Id=@id AND ParentId IS NOT NULL AND NotifiedUtc IS NULL; true when 1 row
    void ReleaseNotify(Guid replyId);                  // SET NotifiedUtc = NULL
}
```

- `ListApproved` and `CountApproved` keep their 0.2.0 meaning, so they still include replies.

- [ ] **Step 1: Write the failing tests** in `ReplyDataTests`:
  - `Migration_adds_reply_columns_to_a_0_2_0_table`: drop `baryoComment`, recreate it in the 0.2.0 shape with raw SQL (the seven 0.2.0 columns), set the package's upgrader state key to the CreateTables step `b3d7c1a4-5e02-4f6a-8a31-7c9e0d2f4b18`, run the plan, then assert `ParentId`, `AuthorUserKey` and `NotifiedUtc` exist and an old row reads back with all three null.
  - `Migration_runs_twice_without_error_with_reply_columns`.
  - `Top_level_list_and_count_skip_replies`: seed 2 approved top-level and 3 approved replies on one page. Assert `CountApprovedTopLevel == 2`, `ListApprovedTopLevel(page, 0, 10).Count == 2`, and `CountApproved == 5` (unchanged meaning).
  - `ListReplies_returns_approved_replies_oldest_first`: also seed a pending reply. Only approved replies come back, in time order.
  - `Delete_removes_replies`: `Delete(parentId)` leaves no row with that `ParentId`.
  - `Retention_removes_orphan_replies`: an approved reply whose spam parent is older than the cutoff is gone after `DeleteOlderThan(Spam, cutoff)`.
  - `TryClaimNotify_succeeds_once`: first call true, second false. `TryClaimNotify` on a top-level row is false. After `ReleaseNotify`, it is true again.
- [ ] **Step 2:** Add the columns, `AddReplyColumns` (add each column only if missing, create the index only if missing) and `.To<AddReplyColumns>("5f1c9e72-3a4d-4b8e-9c06-d2e7a1b84f35")` after the existing step. Implement the interface and `Delete` cascade (`WHERE Id = @0 OR ParentId = @0` in one scope). Add the orphan delete (`ParentId IS NOT NULL AND ParentId NOT IN (SELECT Id FROM baryoComment)`) in the same scope as the age delete. The return value stays the count of the status rows.
- [ ] **Step 3:** `dotnet build -c Release`, expect 0 warnings as errors.
- [ ] **Step 4:** Session queues `ReplyDataTests` and `CommentFlowTests` (the existing migration and delete tests must stay green).
- [ ] **Step 5:** Commit `add reply columns and threaded comment queries`.

### Task 2: Reply service and reply email

**Files:**
- Create: `src/BaryoDev.Umbraco.Comments/Services/CommentReplies.cs`
- Create: `src/BaryoDev.Umbraco.Comments/Services/ICommentReplyEmail.cs` (interface plus default `CommentReplyEmail`)
- Modify: `src/BaryoDev.Umbraco.Comments/CommentsOptions.cs`, `CommentsOptionsValidator.cs`, `CommentsComposer.cs`
- Test: `tests/BaryoDev.Umbraco.Comments.Tests/ReplyTests.cs` (new, xUnit, in-memory fake of `ICommentRepositoryThreaded` and a recording `ICommentReplyEmail`)

**Interfaces:**
- Consumes: `ICommentRepositoryThreaded` (Task 1).
- Produces:

```csharp
public enum ReplyResult { Saved, NotFound, NotApproved, IsReply, BodyInvalid, Unsupported }
public enum NotifyResult { Sent, NotFound, NotAReply, AlreadySent, NoEmail, CannotSend, Failed, Unsupported }
public sealed record ReplyAuthor(Guid UserKey, string Name);
public sealed record ReplyOutcome(ReplyResult Result, Guid? ReplyId, NotifyResult? Email);

public sealed class CommentReplies(ICommentRepository repository, ICommentReplyEmail email,
    IOptions<CommentsOptions> options, TimeProvider time, ILogger<CommentReplies> logger)
{
    public Task<ReplyOutcome> ReplyAsync(Guid parentId, string? body, bool notify, ReplyAuthor author, CancellationToken ct = default);
    public Task<NotifyResult> NotifyAsync(Guid replyId, CancellationToken ct = default);
}

public interface ICommentReplyEmail
{
    bool CanSend { get; }                                   // default: IEmailSender.CanSendRequiredEmail()
    Task SendAsync(CommentRow parent, CommentRow reply, CancellationToken ct);
}
```

- `CommentsOptions` gains `string? ReplyTo` and `string? PublicBaseUrl`, both null by default.
- `CommentReplyEmail` exposes `internal static string ReplyLink(string? publicBaseUrl, string pageUrl, Guid replyId)` and `internal static (string Subject, string Html) Compose(CommentRow parent, CommentRow reply, string pageName, string link)` for tests.

**Rules (all inside `CommentReplies`):**
- `Unsupported` when the repository is not `ICommentRepositoryThreaded`.
- Missing parent gives `NotFound`. A parent that is not Approved gives `NotApproved`. A parent with a `ParentId` gives `IsReply`.
- The body is cleaned the way `CommentSubmission` cleans visitor bodies (reuse its helper, make it `internal` if needed). The length must be within `MinBodyLength` and `min(MaxBodyLength, HardBodyLimit)`, otherwise `BodyInvalid`.
- The saved row has a new Guid, the parent's `ContentKey`, `ParentId`, `AuthorUserKey = author.UserKey`, `Status = Approved`, `Email = null` and `CreatedUtc` from `time`. `Name` is `author.Name` cleaned like a visitor name and cut to `HardNameLimit`. An empty name becomes `Author`.
- When `notify` is true and the parent has an email, `ReplyAsync` calls `NotifyAsync` after the save. Otherwise `Email` is null. The reply is saved whatever the email does.
- `NotifyAsync` checks in order: `NotFound`, `NotAReply`, `AlreadySent` (NotifiedUtc set), `NoEmail` (parent gone or no email), `CannotSend` (`!email.CanSend`, nothing claimed), `AlreadySent` (`TryClaimNotify` false). Then it sends. An exception calls `ReleaseNotify`, logs a warning and returns `Failed`.

**Email (default `CommentReplyEmail`, depends on `IEmailSender`, `IOptions<GlobalSettings>`, `IOptions<CommentsOptions>`, `IContentService`, `IPublishedUrlProvider`, `IUmbracoContextFactory`):**
- Page URL: `IPublishedUrlProvider.GetUrl(contentKey, UrlMode.Relative)` inside `EnsureUmbracoContext()`. `ReplyLink` returns `PublicBaseUrl.TrimEnd('/') + pageUrl + "#comment-" + replyId` when `PublicBaseUrl` is set. Otherwise it uses `UrlMode.Absolute`.
- Subject: `{reply.Name} replied to your comment on {pageName}`, with CR and LF replaced by spaces.
- HTML body, every value HTML-encoded:
  - `Hi {parent.Name},`
  - `{reply.Name} replied to your comment on {pageName}.`
  - a blockquote of the first 500 characters of the parent body
  - the reply body, with newlines as `<br>`
  - a link `Read it on the page`
  - the footer `You got this email because you left your address with your comment. It is sent once per reply.`
- `EmailMessage`: from `GlobalSettings.Smtp.From`, to `parent.Email`, replyTo `[ReplyTo]` when set, `isBodyHtml: true`, email type `BaryoComments.Reply`.
- Validator: `ReplyTo`, when set, must parse as `MailAddress`. `PublicBaseUrl`, when set, must be an absolute http or https URL.
- DI: `TryAddSingleton<ICommentReplyEmail, CommentReplyEmail>()` and `AddTransient<CommentReplies>()`. Keep `AddServices` usable from the unit tests that call it today.

- [ ] **Step 1: Write the failing tests** in `ReplyTests`:
  - `Reply_to_an_approved_comment_is_saved_approved_with_author`
  - `Reply_to_pending_spam_missing_or_a_reply_is_refused` (Theory over the four results)
  - `Reply_body_outside_limits_is_BodyInvalid`
  - `Reply_saves_even_when_the_email_fails`
  - `Reply_without_notify_or_without_parent_email_sends_nothing`
  - `Notify_twice_sends_once`
  - `Notify_without_smtp_is_CannotSend_and_leaves_it_unsent`
  - `Notify_failure_releases_the_claim`, where the sender throws: the result is `Failed`, `NotifiedUtc` is null and a later notify can send.
  - `ReplyLink_uses_PublicBaseUrl`: `("https://baryo.dev/", "/blog/post/", id)` gives `https://baryo.dev/blog/post/#comment-{id}`.
  - `Email_encodes_html_and_flattens_the_subject`: parent name `<b>x</b>`, page name `A\r\nBcc: y`. The HTML holds `&lt;b&gt;`, and the subject has no CR or LF.
  - `Options_reject_a_bad_ReplyTo_or_PublicBaseUrl`
  - `Unsupported_repository_gets_Unsupported`
- [ ] **Step 2:** Implement to the rules above.
- [ ] **Step 3:** `dotnet build -c Release`.
- [ ] **Step 4:** Session queues `ReplyTests`, `SubmissionTests` (shared clean helper) and `ApiCompatTests`.
- [ ] **Step 5:** Commit `add comment replies and the reply email`.

### Task 3: Reply endpoints and dashboard

**Files:**
- Modify: `src/BaryoDev.Umbraco.Comments/Api/CommentsApiController.cs`
- Modify: `src/BaryoDev.Umbraco.Comments/wwwroot/App_Plugins/BaryoDev.Comments/comments-dashboard.js`, `brand.css`
- Test: `tests/BaryoDev.Umbraco.Comments.IntegrationTests/ReplyApiTests.cs` (new)

**Interfaces:**
- Consumes: `CommentReplies`, `ReplyAuthor`, `ReplyResult`, `NotifyResult` (Task 2), and the `CommentRow` fields (Task 1).
- Produces:
  - `public sealed record ReplyRequest(string? Body, bool Notify = true);`
  - `public sealed record ReplyResponse(Guid Id, bool Emailed, string? EmailStatus);`, where `EmailStatus` is the `NotifyResult` name in lower case, or null when no email was asked.
  - On `CommentItem`, `init` properties: `Guid? ParentId`, `string? ParentName`, `bool IsSiteReply` (AuthorUserKey not null), `DateTime? NotifiedUtc`, `bool CanReply` (approved, top level, threaded repository) and `bool CanNotify` (reply, NotifiedUtc null, parent has an email).

**Endpoints** (same `[Authorize]` and `CanSeeAsync(parent.ContentKey)` as approve, `Forbid` otherwise; the author is the current backoffice user's `Key` and `Name ?? Username`):
- `POST {id:guid}/reply` with `[FromBody] ReplyRequest`:
  - `Saved` gives 201 with a `ReplyResponse`
  - `NotFound` gives 404
  - `NotApproved`, `IsReply`, `BodyInvalid` and `Unsupported` give 400 with `{ error }` in plain words
- `POST {id:guid}/notify` (the check uses the reply's `ContentKey`):
  - `Sent` gives 204
  - `NotFound` gives 404
  - `NotAReply`, `NoEmail` and `Unsupported` give 400
  - `AlreadySent` gives 409
  - `CannotSend` gives 503 with `{ error: "Email is not set up on this site." }`
  - `Failed` gives 502

**Dashboard:**
- `call()` takes an optional JSON body and surfaces the server's `error` text.
- Items with `canReply` get a Reply button. It opens a textarea (maxlength 4000), a checkbox `Email {name}` (checked) shown only when the item has an email, and Send reply and Cancel buttons.
- The result note reads `Reply posted. Emailed {name}.` or `Reply posted. Not emailed: {reason}.`
- Reply rows show `Reply to {parentName}`, then `Emailed {date}` or `Not emailed`, plus a Send email button when `canNotify`.
- Every value goes through `esc()`.

- [ ] **Step 1: Write the failing tests** in `ReplyApiTests`, using the controller setup from `CommentFlowTests` with a recording `ICommentReplyEmail` swapped in:
  - `Reply_endpoint_saves_and_reports_email`
  - `Reply_endpoint_refuses_a_user_without_Browse`
  - `Reply_endpoint_is_400_for_a_pending_parent`
  - `Notify_endpoint_is_409_the_second_time`
  - `Notify_endpoint_is_503_without_smtp`
  - `List_marks_replies_and_reply_permissions`: approved list items carry `ParentId`, `ParentName`, `IsSiteReply`, `CanReply` and `CanNotify` as above.
- [ ] **Step 2:** Implement the endpoints, the `CommentItem` fields and the dashboard.
- [ ] **Step 3:** `dotnet build -c Release`.
- [ ] **Step 4:** Session queues `ReplyApiTests`, `CommentFlowTests` and `ApiCompatTests`.
- [ ] **Step 5:** Commit `reply to comments from the dashboard`.

### Task 4: Replies on the page

**Files:**
- Modify: `src/BaryoDev.Umbraco.Comments/CommentsViewComponent.cs`
- Modify: `src/BaryoDev.Umbraco.Comments/Views/Shared/Components/BaryoComments/Default.cshtml`
- Modify: `src/BaryoDev.Umbraco.Comments/wwwroot/App_Plugins/BaryoDev.Comments/comments.css`
- Test: `tests/BaryoDev.Umbraco.Comments.Tests/ReplyViewTests.cs` (new, xUnit) and `tests/BaryoDev.Umbraco.Comments.IntegrationTests/ReplyPagingTests.cs` (new). The test projects never render Razor views, so markup is checked against the view source.

**Interfaces:**
- Consumes: `ICommentRepositoryThreaded` (Task 1).
- Produces, on `CommentView`, `init` properties: `Guid Id`, `IReadOnlyList<CommentView> Replies` (default empty), `string? ReplyingTo` and `bool IsSiteReply`.
- Produces `internal static IReadOnlyList<CommentView> Thread(IReadOnlyList<CommentRow> parents, IReadOnlyList<CommentRow> replies)` on `CommentsViewComponent`: parents in the order given, each with its replies oldest first, `ReplyingTo` set to the parent name and `IsSiteReply` set from `AuthorUserKey`. Replies whose parent is not in `parents` are dropped.

**Behavior:**
- With `ICommentRepositoryThreaded`, the page count, the window and the list use the top-level members, and replies come from one `ListReplies` call for the page's parent ids. Without it, the 0.2.0 path is unchanged.
- Each comment `<li>` gets `id="comment-{Id}"`.
- Replies go in `<ol class="bdc__replies">` under the parent body. Each reply `<li class="bdc__item bdc__item--reply">` shows the name, a `<span class="bdc__badge">Reply</span>` only when `IsSiteReply`, the time, `<p class="bdc__replying">Replying to {ReplyingTo}</p>` and the body.
- The email hint becomes `Never shown on the page. Used only to email you if someone replies.`
- CSS: indent replies and style the badge with the existing `comments.css` variables. No new colors outside them.

- [ ] **Step 1: Write the failing tests.**
  - In `ReplyViewTests` (unit):
    - `Thread_puts_replies_under_their_parent_oldest_first`
    - `Thread_drops_replies_without_a_listed_parent`
    - `A_visitor_using_the_owner_name_gets_no_badge`: a top-level row named like the owner, with no `AuthorUserKey`, has `IsSiteReply` false.
    - `View_markup_has_anchor_badge_and_reply_hint`: reads `Default.cshtml` from the source tree and asserts it contains `id="comment-@`, `bdc__replies`, `bdc__badge` and the new hint text.
  - In `ReplyPagingTests` (integration):
    - `Paging_counts_top_level_comments_only`: 3 top-level comments and 4 replies with page size 2 give `Window(...).PageCount == 2`.
    - `Replies_of_a_spam_comment_are_hidden`: after the parent is marked spam, the page's thread holds neither the parent nor its replies.
- [ ] **Step 2:** Implement.
- [ ] **Step 3:** `dotnet build -c Release`.
- [ ] **Step 4:** Session queues `ReplyViewTests`, `ReplyPagingTests`, `CommentFlowTests` and `ApiCompatTests`.
- [ ] **Step 5:** Commit `show replies under comments on the page`.

### Task 5: Docs

**Files:** `README.md`, `CHANGELOG.md`, `docs/design.md`

- [ ] **Step 1:** README gets a Replies section:
  - how to reply from the dashboard
  - email needs Umbraco Global Smtp, and without it replies post and show "Not emailed"
  - the `ReplyTo` and `PublicBaseUrl` options, with `PublicBaseUrl` advised when the backoffice runs on a different host
- [ ] **Step 2:** CHANGELOG gets `## Unreleased` with Changes, the three columns added by migration, and the new options. It gets no Breaking section unless a task had to break something.
- [ ] **Step 3:** `docs/design.md`: add the three columns to Data model and the two routes to Endpoints, and take "Threaded replies" out of "Not in v1".
- [ ] **Step 4:** `style-scan --base main` is clean.
- [ ] **Step 5:** Commit `document comment replies`.

## After the tasks (session)

- Adversarial review of the branch, push, then a draft PR on GitHub with the test output.
- The release PR (Version 0.3.0 in the csproj and `umbraco-package.json`, CHANGELOG heading) waits for Arnel's go. Then comes the baryo.dev bump in baryoweb with `BaryoDev__Comments__PublicBaseUrl=https://baryo.dev` and, when SMTP lands, `ReplyTo`.
