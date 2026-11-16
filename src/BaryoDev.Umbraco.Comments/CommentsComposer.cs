using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;

namespace BaryoDev.Umbraco.Comments;

public sealed class CommentsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddOptions<CommentsOptions>().BindConfiguration(CommentsOptions.Section).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<CommentsOptions>, CommentsOptionsValidator>();
        builder.Services.TryAddSingleton<ICommentSpamCheck, AllowAllSpamCheck>();
        builder.Services.AddSingleton<ICommentRetentionStore, CommentRetentionStore>();
        builder.Services.AddSingleton<CommentsRetention>();
        builder.Services.AddHostedService<CommentsRetentionService>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICommentRepository, CommentRepository>();
        builder.Services.AddSingleton<RateLimiter>();
        builder.Services.AddSingleton<FormToken>();
        builder.Services.AddTransient<CommentSubmission>();
        builder.AddNotificationHandler<ContentDeletedNotification, CommentsContentDeletedHandler>();
    }
}
