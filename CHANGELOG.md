# Changelog

## Unreleased

- A failed post no longer puts the typed text into TempData. The default cookie TempData provider turned a long non-ASCII post into about 35 KB of cookies, and the next request got a 431. The draft is kept in sessionStorage in the visitor's tab instead, by a script file (no inline script, so a strict CSP does not block it).
- The form token is bound to the page it was issued for.
- Form tokens are single use, per server instance, with a bounded in-memory table. Tokens now carry a random id, so a form open during the upgrade is rejected once.
- New `ICommentSpamCheck` hook (allow, spam or reject) that runs after validation and before save. `CommentSubmission` gains `SubmitAsync` and a constructor that takes the hook. The old constructor and `Submit` still work.
- New options `MaxPendingPerPage` (default 100) and `MaxPendingTotal` (default 5000). At a cap new comments are refused and nothing is saved. Set `null` for no cap. This changes behaviour for sites with more pending comments than that.
- New opt-in `SpamRetentionDays` and `PendingRetentionDays` with an hourly cleanup job. Approved comments are never deleted by age.
- Options are validated at startup (negative caps, retention under 1 day).
- The rate limiter tracks at most 50,000 clients. When it is full the least recently seen ones are dropped.
- The submit action accepts at most 64 KB per request.
- The dashboard list, counts and total cover only pages the editor can browse (key lists are queried in batches of 1,000).
- Removed the stale icon TODO from the README.

## 0.1.0

First public release for Umbraco 17. Plain-text comments under a post, held for moderation by default, with a backoffice dashboard to approve or delete them and a per-address rate limit.
