# Comment replies (0.3.0)

## Why

A reader left a comment on the five-packages post saying he would like to try them. Arnel wants to answer him under the comment and, since he left an email, tell him by email so he sees it ("if you've tried it, report bugs"). Today the package has no way to reply.

## Decided by Arnel (Nov 21 2026)

- Replies and reply emails ship in the free package as 0.3.0. This moves them out of the Pro column of the Nov 7 free/Pro design, and they stay free from now on.
- SMTP on baryo.dev comes later. The feature has to work before it is set up.

## Approaches considered

1. Site code in baryoweb now, moved into Comments Pro later. This keeps the free/Pro split but means writing the feature twice.
2. **Free package, 0.3.0 (picked).** Everyone gets replies, and the free/Pro decision is reversed for this feature.
3. Comments Pro now. This waits on the license key and the private package setup.

## Goals

- From the dashboard, a backoffice user can reply to an approved comment.
- The reply shows on the page under that comment, marked as the site's reply, with "Replying to <name>".
- If the commenter left an email, they get one email for that reply.
- If email can't go out yet (no SMTP, or the send fails), the reply still posts and the email can be sent later from the dashboard.

## Non-goals

- Visitors replying to each other.
- Threads more than one level deep.
- Parsing @mentions in comment text.
- Visitor sign-in.
- Editing replies. Delete the reply and post it again.
- Emailing the site owner about new comments.

## Anti-goals

- Any path a visitor can use to make the site send email.
- Emailing someone who left no address, or emailing them more than once for the same reply.
- Breaking the 0.2.0 public API (see ApiCompatTests).

## Design

**Data.** An additive migration adds three nullable columns to `baryoComment`:

- `ParentId`, indexed, set on replies.
- `AuthorUserKey`, the backoffice user who wrote the reply. It drives the badge, so a visitor typing the owner's name can't pass as the owner.
- `NotifiedUtc`, set when the reply email was sent.

A reply is a row with `Status` Approved, `Name` taken from the backoffice user's name, and `Email` null.

**Repository.** `ICommentRepository` and `ICommentRepositoryScoped` keep their 0.2.0 members and meaning. The new members (top-level list and count for a page, replies for a set of parents, set notified) go on a new interface that `CommentRepository` implements, following the 0.2.0 `ICommentRepositoryScoped` pattern. A custom repository without it gets the 0.2.0 behavior: a flat list and no reply button.

**API** (same auth as approve: Content section plus Browse on the page):

- `POST /comments/{id}/reply` with `{ body, notify }` saves the reply and returns its id and whether the email went out.
- `POST /comments/{id}/notify` sends the email for a reply not emailed yet. It returns 409 if the reply was already sent and 400 if the parent has no email.
- Replying to a pending, spam or reply row is refused. A reply can only answer an approved top-level comment.
- New fields on `CommentItem` are init properties, not positional, so the 0.2.0 constructor stays.

**Email.** Sent through Umbraco's `IEmailSender`, using the existing Global Smtp settings, so the package takes no new dependency. The subject is "<author> replied to your comment on <page>". The body quotes their comment, then the reply, then a link to `<page>#comment-<replyId>`. It is sent as plain text plus escaped HTML, from Smtp `From`. A new optional option `ReplyTo` lets them answer by email. A send failure is logged and leaves `NotifiedUtc` null. The sender sits behind a replaceable interface (TryAdd, like `ICommentSpamCheck`), so a site can change the wording.

**Dashboard.** Each approved top-level comment gets a Reply button. It opens a textarea, plus an "Email <name>" checkbox (on by default) that appears only when the comment has an email. Reply rows show "Reply to <name>", then "Emailed <date>", or "Not emailed" with a Send email button.

**Public view:**

- Paging counts top-level comments only.
- Replies sit under their parent, oldest first, with a "Reply" badge and "Replying to <name>".
- Every comment gets an `id="comment-<id>"` anchor.
- A reply shows only while its parent is approved.

**Cascades.** Deleting a comment deletes its replies. Marking a comment spam hides its replies, because they only render under an approved parent. Page delete already removes every row for the page.

**Form.** The email hint becomes "Never shown on the page. Used only to email you if someone replies."

## Left to the builder

- Interface and member names.
- Migration step id.
- Email markup.
- Dashboard layout details.
- The exact 400, 404 and 409 bodies.

## Done means

- Unit tests cover the reply rules and the notify rules.
- An integration test covers the migration on an existing 0.2.0 table and the reply-then-notify flow.
- ApiCompatTests pass.
- README, CHANGELOG 0.3.0 and `docs/design.md` are updated.
- Released through a release PR like #8, then dogfooded on baryo.dev.
