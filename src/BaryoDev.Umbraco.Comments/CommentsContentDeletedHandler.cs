using BaryoDev.Umbraco.Comments.Data;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace BaryoDev.Umbraco.Comments;

/// <summary>Removes a page's comments, and the emails in them, when the page is deleted.</summary>
public sealed class CommentsContentDeletedHandler(ICommentRepository repository) : INotificationHandler<ContentDeletedNotification>
{
    public void Handle(ContentDeletedNotification notification)
    {
        foreach (var item in notification.DeletedEntities)
        {
            repository.DeleteForContent(item.Key);
        }
    }
}
