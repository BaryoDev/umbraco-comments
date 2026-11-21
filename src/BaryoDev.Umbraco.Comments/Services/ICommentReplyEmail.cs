using System.Net;
using System.Text;
using BaryoDev.Umbraco.Comments.Data;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Mail;
using Umbraco.Cms.Core.Models.Email;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace BaryoDev.Umbraco.Comments.Services;

/// <summary>Tells a commenter that the site replied. Replace it to change the wording or the way mail goes out.</summary>
public interface ICommentReplyEmail
{
    /// <summary>False when mail can not go out yet, for example no SMTP settings.</summary>
    bool CanSend { get; }

    Task SendAsync(CommentRow parent, CommentRow reply, CancellationToken ct);
}

public sealed class CommentReplyEmail(
    IEmailSender sender,
    IOptions<GlobalSettings> globalSettings,
    IOptions<CommentsOptions> options,
    IContentService content,
    IPublishedUrlProvider urls,
    IUmbracoContextFactory contextFactory) : ICommentReplyEmail
{
    private const string EmailType = "BaryoComments.Reply";

    public bool CanSend => sender.CanSendRequiredEmail();

    public async Task SendAsync(CommentRow parent, CommentRow reply, CancellationToken ct)
    {
        CommentsOptions o = options.Value;
        string pageName = content.GetById(parent.ContentKey)?.Name ?? "the site";

        string pageUrl;
        using (contextFactory.EnsureUmbracoContext())
        {
            UrlMode mode = string.IsNullOrWhiteSpace(o.PublicBaseUrl) ? UrlMode.Absolute : UrlMode.Relative;
            pageUrl = urls.GetUrl(parent.ContentKey, mode);
        }

        string link = ReplyLink(o.PublicBaseUrl, pageUrl, reply.Id);
        (string subject, string html) = Compose(parent, reply, pageName, link);

        var message = new EmailMessage(
            globalSettings.Value.Smtp?.From,
            [parent.Email!],
            null,
            null,
            string.IsNullOrWhiteSpace(o.ReplyTo) ? null : [o.ReplyTo],
            subject,
            html,
            true,
            null);
        // true lets a SendEmailNotification handler deliver it, which CanSendRequiredEmail also counts.
        await sender.SendAsync(message, EmailType, true, null);
    }

    /// <summary>The link to the reply on the public page. Empty when the page has no URL.</summary>
    internal static string ReplyLink(string? publicBaseUrl, string pageUrl, Guid replyId)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || pageUrl == "#")
        {
            return string.Empty;
        }
        string anchor = "#comment-" + replyId;
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            return pageUrl + anchor;
        }
        // Relative mode still gives an absolute URL when the page has a domain other than the request's host.
        // Check the scheme: on Unix a path like /blog/post/ parses as an absolute file URI.
        string path = Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.PathAndQuery
            : pageUrl;
        return publicBaseUrl.TrimEnd('/') + path + anchor;
    }

    internal static (string Subject, string Html) Compose(CommentRow parent, CommentRow reply, string pageName, string link)
    {
        string subject = $"{reply.Name} replied to your comment on {pageName}".Replace('\r', ' ').Replace('\n', ' ');

        // Cut at 499 when 500 would split an emoji's surrogate pair.
        string quoted = parent.Body.Length <= 500 ? parent.Body : parent.Body[..(char.IsHighSurrogate(parent.Body[499]) ? 499 : 500)];
        string E(string s) => WebUtility.HtmlEncode(s);

        var sb = new StringBuilder();
        sb.Append("<p>Hi ").Append(E(parent.Name)).Append(",</p>");
        sb.Append("<p>").Append(E(reply.Name)).Append(" replied to your comment on ").Append(E(pageName)).Append(".</p>");
        sb.Append("<blockquote>").Append(E(quoted).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>")).Append("</blockquote>");
        sb.Append("<p>").Append(E(reply.Body).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>")).Append("</p>");
        if (link.Length > 0)
        {
            sb.Append("<p><a href=\"").Append(E(link)).Append("\">Read it on the page</a></p>");
        }
        sb.Append("<p><small>You got this email because you left your address with your comment. It is sent once per reply.</small></p>");
        return (subject, sb.ToString());
    }
}
