# Changelog

## Unreleased

### Breaking

- Forms open during the upgrade are rejected once, because form tokens now carry a page key and a random id.
- For a custom `ICommentRepository`, the dashboard counts show zero for non-administrators and when no user is signed in (0.1.0 showed sitewide counts, which leaked). Rows still show, filtered per page as in 0.1.0, and paging keeps working through a new `HasMore` flag on the list response. Implement `ICommentRepositoryScoped` for page-scoped lists and counts. If you set `MaxPendingPerPage`, a custom repository uses the total pending count for it.
- The restored 0.1.0 members are `[Obsolete]`, so builds with warnings as errors need `NoWarn` CS0618 until they move to the new members.
- `SubmitResult` has new members (`Reused`, `Paused`, `Rejected`). If you switch over it exhaustively, add a default arm.

Every 0.1.0 public member is kept. The ones whose behaviour changed are marked `[Obsolete]`: `FormToken.Issue()` and `Age(string?)` (a token from `Issue()` is not bound to a page and is refused on submit), `CommentsModel.DraftName`, `DraftEmail`, `DraftBody` and the 0.1.0 constructor (always empty now), and `CommentsController.DraftName`, `DraftEmail`, `DraftBody`. `ICommentRepository` has its 0.1.0 members again, and the page-scoped members live in `ICommentRepositoryScoped`.

### Changes

- A failed post no longer puts the typed text into TempData. The default cookie TempData provider turned a long non-ASCII post into about 35 KB of cookies, and the next request got a 431. The draft is kept in sessionStorage in the visitor's tab instead, by a script file (no inline script, so a strict CSP does not block it).
- The form token is bound to the page it was issued for.
- Form tokens are single use, per server instance, with a bounded in-memory table. A token is released again when nothing was saved (paused by a cap, rejected or failed by the spam hook, failed save), so the visitor can resend the same form. Tokens now carry a random id, so a form open during the upgrade is rejected once.
- New `ICommentSpamCheck` hook (allow, spam or reject) that runs after validation and before save. `CommentSubmission` gains `SubmitAsync` and a constructor that takes the hook. The old constructor and `Submit` still work. The new `SubmitResult` members (`Reused`, `Paused`, `Rejected`) are appended, so every 0.1.0 member keeps its number. An unknown verdict value counts as `Reject`.
- New opt-in options `MaxPendingPerPage` and `MaxPendingTotal` (off by default, `null`). At a cap new comments are refused and nothing is saved until a moderator clears the queue.
- New opt-in `SpamRetentionDays` and `PendingRetentionDays` with an hourly cleanup job. Approved comments are never deleted by age.
- Options are validated at startup (negative caps, retention under 1 day or over 36,500 days). One failed retention delete no longer skips the other, and the first run happens after host start-up.
- The rate limiter tracks at most 50,000 clients. When it is full the least recently seen ones are dropped.
- The submit action accepts at most 64 KB per request.
- The dashboard list, counts and total cover only pages the editor can browse (key lists are queried in batches of 1,000).
- Removed the stale icon TODO from the README.

## 0.1.0

First public release for Umbraco 17. Plain-text comments under a post, held for moderation by default, with a backoffice dashboard to approve or delete them and a per-address rate limit.
