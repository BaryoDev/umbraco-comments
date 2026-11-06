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

## Abuse controls

Antiforgery token, a honeypot field, a minimum time on the form, a per-IP sliding window in memory (the address is hashed and not stored), and length limits.

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
      "PageSize": 50
    }
  }
}
```

`PageSize` is how many approved comments show before a "Show more comments" link. `MaxNameLength`, `MaxEmailLength` and `MaxBodyLength` are capped at the column sizes (80, 200, 4000). Email is stored only if given and is never shown on the page.

## Not in this version

Visitor sign-in, replies, reactions and notification emails. See `docs/design.md`.

## TODO

The `icon.png` is a placeholder copied from another BaryoDev package. Replace it before publishing.

## License

MIT
