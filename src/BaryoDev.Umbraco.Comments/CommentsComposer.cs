using BaryoDev.Umbraco.Comments.Data;
using BaryoDev.Umbraco.Comments.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace BaryoDev.Umbraco.Comments;

public sealed class CommentsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddOptions<CommentsOptions>().BindConfiguration(CommentsOptions.Section);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICommentRepository, CommentRepository>();
        builder.Services.AddSingleton<RateLimiter>();
        builder.Services.AddSingleton<FormToken>();
        builder.Services.AddTransient<CommentSubmission>();
    }
}
