# BaryoDev.Umbraco.Comments

Moderated comments under posts for Umbraco 17.

Visitors send a name, an optional email and a plain-text comment. Comments wait for approval. Only approved comments show. Nothing is linkified and nothing renders as HTML. The form works without JavaScript.

## Install

```
dotnet add package BaryoDev.Umbraco.Comments
```

Start the site. The package creates its own table (`baryoComment`) through a migration plan.

## Use it

Put this under a post in your Razor view:

```cshtml
@await Component.InvokeAsync("BaryoComments", new { contentKey = Model.Key })
```

It shows approved comments and the form. It loads `/App_Plugins/BaryoDev.Comments/comments.css`. The CSS reads your own custom properties first (`--ink`, `--line`, `--green`, `--radius-card`, `--font-sans` and so on) and falls back to baryo.dev values. To override only for comments, set the `--bdc-*` variables, for example `--bdc-green` and `--bdc-ink`.

Behind a proxy or load balancer, enable forwarded headers so the rate limit sees the real client address. On several servers, share the ASP.NET Core Data Protection keys between them. Otherwise the antiforgery check and the form timestamp fail on a different server and visitors see an error (their text is kept). Do not output-cache the page region that holds the form.

Comments are removed when their page is deleted.

## Moderate

Content section, Comments dashboard. Tabs for pending, approved and spam. Approve, mark spam or delete. The API is at `/umbraco/management/api/v1/comments` and needs Content section access.

`ICommentRepositoryScoped` is implemented by the built-in repository. A custom `ICommentRepository` is used everywhere else, and the dashboard filters its rows per page (counts show zero for non-administrators).

## Replies

In the Approved tab, press Reply on a comment, write the reply and press Send reply. The reply is saved as approved and shows under the comment on the page, with your backoffice name and a "Reply" badge. Replies go one level deep, so a reply can not be replied to. You need Browse on the comment's page. Deleting a comment deletes its replies, and marking it spam hides them.

If the commenter left an email address, "Email {name}" is ticked and they get one email with the reply and a link to it on the page. The email goes through Umbraco's own sender, so it needs `Umbraco:CMS:Global:Smtp` or a handler for Umbraco's `SendEmailNotification`. Without either, the reply still posts and the dashboard shows "Not emailed". Once mail works, press Send email on the reply. Each reply is emailed at most once, even if two people press Send email at the same time. A failed send can be tried again.

`ReplyTo` is the address the commenter's mail app answers to. `PublicBaseUrl` (for example `https://baryo.dev`) is the host used for the link in the email. Set it when the backoffice runs on a different host from the public site, or the link points at the backoffice host. To change the wording or send mail another way, register your own `ICommentReplyEmail`.

Replies need the built-in repository. A custom `ICommentRepository` that does not implement `ICommentRepositoryThreaded` shows no Reply button, and the reply endpoints answer 400.

## Abuse controls

Antiforgery token, a honeypot field, a minimum time on the form, a per-IP sliding window in memory (the address is hashed and not stored), length limits, single-use form tokens, a cap on pending comments and an optional spam check hook.

Each form token works once. A second post with the same token is refused with "That comment was already sent". A token is released again when nothing was saved (a cap pause, a spam hook reject or error, a failed save), so the visitor can send the same form again. Used tokens are kept in memory on one server until they would have expired (`MaxFormAgeMinutes`), at most 50,000. If that table is full of live tokens, new posts get the "form was open too long" notice until some expire. On a load-balanced site without sticky sessions, single use holds per server, not across servers. Tokens now carry a random id, so a form that was open in a visitor's browser during the upgrade is rejected once. They reload and send again.

### Pending cap

`MaxPendingPerPage` and `MaxPendingTotal` limit pending comments. Default: none (off). Set them if spam could fill your database. The trade-off: at either cap, new comments are refused with "Comments are paused for review" and nothing is saved until a moderator clears the queue (approve, mark spam or delete rows). `null` means no cap, `0` pauses new comments, negative values fail at startup. Auto-approved comments are not counted.

### Spam check hook

Implement `ICommentSpamCheck` to run your own check after validation and before the comment is saved. It gets the cleaned name, email, body, the rate-limit client key and the `HttpContext`. Return `Allow`, `Spam` (saved with the Spam status, the visitor sees the normal notice) or `Reject` (nothing saved, the visitor sees an error). The default allows everything. If your check throws, nothing is saved.

```csharp
public sealed class LinkCheck : ICommentSpamCheck
{
    public ValueTask<SpamVerdict> CheckAsync(CommentSpamContext c, CancellationToken ct) =>
        ValueTask.FromResult(c.Body.Contains("http", StringComparison.OrdinalIgnoreCase) ? SpamVerdict.Spam : SpamVerdict.Allow);
}

// in a composer
builder.Services.AddSingleton<ICommentSpamCheck, LinkCheck>();
```

## Config

Section `BaryoDev:Comments` in appsettings:

```json
{
  "BaryoDev": {
    "Comments": {
      "AutoApprove": false,
      "RequireEmail": false,
      "MaxNameLength": 80,
      "MaxEmailLength": 200,
      "MaxBodyLength": 2000,
      "MinBodyLength": 2,
      "MinSecondsOnForm": 3,
      "MaxFormAgeMinutes": 120,
      "RateLimitMaxPosts": 5,
      "RateLimitWindowMinutes": 10,
      "PageSize": 50,
      "MaxPendingPerPage": null,
      "MaxPendingTotal": null,
      "SpamRetentionDays": null,
      "PendingRetentionDays": null,
      "ReplyTo": null,
      "PublicBaseUrl": null
    }
  }
}
```

`PageSize` is how many approved comments show per page, with "Earlier comments" and "Later comments" links. `MaxNameLength`, `MaxEmailLength` and `MaxBodyLength` are capped at the column sizes (80, 200, 4000). Email is stored only if given and is never shown on the page.

## Retention and email

Off by default. Set `SpamRetentionDays` or `PendingRetentionDays` (1 to 36500) and an hourly background job deletes spam or pending comments older than that. Approved comments are never deleted by age. On a site with several servers each one runs the job, which is harmless.

An email address is kept until its comment is deleted. To erase it, delete the comment in the dashboard or call `DELETE /umbraco/management/api/v1/comments/{id}`. Deleting a page deletes its comments.

## Not in this version

Visitor sign-in, replies from visitors, reactions and new-comment emails to editors. See `docs/design.md`.

## License

MIT
