# Design

Moderated comments under posts for Umbraco 17. Plain text only. Pending by default.

## Data model

One table, `baryoComment`, created by a package migration plan.

| Column | Type | Notes |
| --- | --- | --- |
| Id | Guid | Primary key |
| ContentKey | Guid | Key of the content node the comment belongs to. Indexed. |
| Name | nvarchar(80) | Required |
| Email | nvarchar(200) | Optional. Never rendered publicly. |
| Body | nvarchar(4000) | Plain text. Encoded on render. |
| Status | int | 0 pending, 1 approved, 2 spam |
| CreatedUtc | datetime | |
| ParentId | Guid, null | Set on a reply, the comment it answers. Indexed. Added in 0.3.0. |
| AuthorUserKey | Guid, null | Backoffice user who wrote the reply. Added in 0.3.0. |
| NotifiedUtc | datetime, null | When the reply email was claimed. Set by one conditional update, so it is sent once. Added in 0.3.0. |

No IP address is stored. Delete removes the row and its replies. Retention also removes replies whose comment is gone.

## Endpoints

Public:

- `POST /baryo-comments/submit` (form post, antiforgery checked). Redirects back to the page with `?bdc=pending`, `?bdc=approved` or `?bdc=error-<code>` and the `#comments` anchor. Works without JavaScript.

Management API (backoffice token, content section access), under `/umbraco/management/api/v1/comments`:

- `GET ?status=pending|approved|spam&skip=&take=` list, with counts per status and a total, paged in SQL
- `POST {id}/approve`
- `POST {id}/spam`
- `DELETE {id}`
- `POST {id}/reply` with `{ body, notify }`. 201 with `{ id, emailed, emailStatus }`. 400 when the comment is not approved, is itself a reply, or the body is out of range.
- `POST {id}/notify` emails the commenter about reply `{id}`. 204 when sent, 409 when already sent, 503 when email is not set up, 502 when the send failed (it can be tried again).

Every route checks Browse on the comment's page.

## Abuse controls

- Antiforgery token on the form.
- Honeypot field. If filled, the post is dropped and the visitor sees the normal pending message.
- Minimum time on form. The form carries a data-protected timestamp. Posts faster than `MinSecondsOnForm` or older than `MaxFormAgeMinutes` are rejected.
- Per-IP sliding window in memory. The IP is hashed with a random per-process key, so no raw IP is held and nothing survives a restart.
- IPv6 clients share one bucket per /64.
- Zero-width characters are stripped before length checks.
- A failed post (including an antiforgery failure) redirects back with an error code and nothing typed goes into TempData (cookie TempData overflows the header limit). The form script keeps the draft in sessionStorage in that tab.
- Form tokens are single use. A nonce in the token is kept in memory (per instance, at most 50,000) until the token expires. A full table of live tokens refuses new posts.
- Opt-in pending cap per page and in total (`MaxPendingPerPage`, `MaxPendingTotal`, off by default). At a cap new posts are refused and nothing is written.
- `ICommentSpamCheck` runs after validation and before save and can allow, mark as spam or reject.
- Optional retention (`SpamRetentionDays`, `PendingRetentionDays`) deletes old spam and pending rows hourly. Approved rows are never deleted by age.
- Deleting a page deletes its comments.
- Length limits on name, email and body, enforced on the server.
- The target node must exist and be published.
- Body is stored as plain text and rendered encoded. Nothing is linkified.

## Options

Section `BaryoDev:Comments`: `AutoApprove` (false), `RequireEmail` (false), `MaxNameLength`, `MaxEmailLength`, `MaxBodyLength`, `MinBodyLength`, `MinSecondsOnForm`, `MaxFormAgeMinutes`, `RateLimitMaxPosts`, `RateLimitWindowMinutes`, `PageSize`, `ReplyTo`, `PublicBaseUrl`.

## Not in v1 (paid tier candidates)

- Visitor sign-in
- Replies from visitors, and replies more than one level deep
- Reactions
- Notification emails to editors when a comment arrives
- Shared spam signals across sites
- Persistent rate limits across several servers
