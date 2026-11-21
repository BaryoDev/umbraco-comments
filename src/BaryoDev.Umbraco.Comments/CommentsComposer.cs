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
        AddServices(builder.Services);
        builder.AddNotificationHandler<ContentDeletedNotification, CommentsContentDeletedHandler>();
    }

    internal static void AddServices(IServiceCollection services)
    {
        services.AddOptions<CommentsOptions>().BindConfiguration(CommentsOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<CommentsOptions>, CommentsOptionsValidator>();
        services.TryAddSingleton<ICommentSpamCheck, AllowAllSpamCheck>();
        services.AddSingleton<ICommentRetentionStore, CommentRetentionStore>();
        services.AddSingleton<CommentsRetention>();
        services.AddHostedService<CommentsRetentionService>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CommentRepository>();
        services.AddSingleton<ICommentRepository>(sp => sp.GetRequiredService<CommentRepository>());
        services.AddSingleton<ICommentRepositoryScoped>(sp => sp.GetRequiredService<CommentRepository>());
        services.AddSingleton<ICommentRepositoryThreaded>(sp => sp.GetRequiredService<CommentRepository>());
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<FormToken>();
        services.AddTransient<CommentSubmission>();
    }
}
