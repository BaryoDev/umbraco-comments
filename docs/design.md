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

No IP address is stored. Delete removes the row.

## Endpoints

Public:

- `POST /baryo-comments/submit` (form post, antiforgery checked). Redirects back to the page with `?bdc=pending`, `?bdc=approved` or `?bdc=error-<code>` and the `#comments` anchor. Works without JavaScript.

Management API (backoffice token, content section access), under `/umbraco/management/api/v1/comments`:

- `GET ?status=pending|approved|spam&skip=&take=` list, with counts per status and a total, paged in SQL
- `POST {id}/approve`
- `POST {id}/spam`
- `DELETE {id}`

## Abuse controls

- Antiforgery token on the form.
- Honeypot field. If filled, the post is dropped and the visitor sees the normal pending message.
- Minimum time on form. The form carries a data-protected timestamp. Posts faster than `MinSecondsOnForm` or older than `MaxFormAgeMinutes` are rejected.
- Per-IP sliding window in memory. The IP is hashed with a random per-process key, so no raw IP is held and nothing survives a restart.
- IPv6 clients share one bucket per /64.
- Zero-width characters are stripped before length checks.
- A failed post (including an antiforgery failure) redirects back with an error code and nothing typed goes into TempData (cookie TempData overflows the header limit). The form script keeps the draft in sessionStorage in that tab.
- Deleting a page deletes its comments.
- Length limits on name, email and body, enforced on the server.
- The target node must exist and be published.
- Body is stored as plain text and rendered encoded. Nothing is linkified.

## Options

Section `BaryoDev:Comments`: `AutoApprove` (false), `RequireEmail` (false), `MaxNameLength`, `MaxEmailLength`, `MaxBodyLength`, `MinBodyLength`, `MinSecondsOnForm`, `MaxFormAgeMinutes`, `RateLimitMaxPosts`, `RateLimitWindowMinutes`, `PageSize`.

## Not in v1 (paid tier candidates)

- Visitor sign-in
- Threaded replies
- Reactions
- Notification emails to editors and to commenters
- Shared spam signals across sites
- Persistent rate limits across several servers
