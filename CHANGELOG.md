# Changelog

## Unreleased

- A failed post no longer puts the typed text into TempData. The default cookie TempData provider turned a long non-ASCII post into about 35 KB of cookies, and the next request got a 431. The draft is kept in sessionStorage in the visitor's tab instead.
- The form token is bound to the page it was issued for.
- The rate limiter tracks at most 50,000 clients. When it is full the least recently seen ones are dropped.
- The submit action accepts at most 32 KB per request.
- The dashboard list, counts and total cover only pages the editor can browse (key lists are queried in batches of 1,000).
- Removed the stale icon TODO from the README.

## 0.1.0

First public release for Umbraco 17. Plain-text comments under a post, held for moderation by default, with a backoffice dashboard to approve or delete them and a per-address rate limit.
