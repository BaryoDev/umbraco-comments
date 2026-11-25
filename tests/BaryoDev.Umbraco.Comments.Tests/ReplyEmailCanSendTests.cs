using BaryoDev.Umbraco.Comments.Services;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Mail;
using Umbraco.Cms.Core.Models.Email;

namespace BaryoDev.Umbraco.Comments.Tests;

public class ReplyEmailCanSendTests
{
    private sealed class HandlerSender(bool hasHandler) : IEmailSender
    {
        public Task SendAsync(EmailMessage message, string emailType) => Task.CompletedTask;
        public Task SendAsync(EmailMessage message, string emailType, bool enableNotification) => Task.CompletedTask;
        public Task SendAsync(EmailMessage message, string emailType, bool enableNotification, TimeSpan? expires) => Task.CompletedTask;
        public bool CanSendRequiredEmail() => hasHandler;
    }

    private static bool CanSend(bool handler, bool trust, string? host = null, string? pickup = null)
    {
        var settings = new GlobalSettings { Smtp = host is null && pickup is null ? null : new SmtpSettings { Host = host!, PickupDirectoryLocation = pickup } };
        var email = new CommentReplyEmail(
            new HandlerSender(handler),
            Options.Create(settings),
            Options.Create(new CommentsOptions { TrustEmailHandlers = trust }),
            null!,
            null!,
            null!);
        return email.CanSend;
    }

    [Fact]
    public void Handler_without_smtp_and_option_off_can_not_send()
    {
        Assert.False(CanSend(handler: true, trust: false));
    }

    [Fact]
    public void Handler_without_smtp_and_option_on_can_send()
    {
        Assert.True(CanSend(handler: true, trust: true));
    }

    [Fact]
    public void No_handler_and_option_on_without_smtp_can_not_send()
    {
        Assert.False(CanSend(handler: false, trust: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Smtp_host_can_send_whatever_the_handler_state(bool handler)
    {
        Assert.True(CanSend(handler, trust: false, host: "smtp.example.com"));
    }

    [Fact]
    public void Pickup_directory_can_send()
    {
        Assert.True(CanSend(handler: false, trust: false, pickup: "/tmp/pickup"));
    }
}
