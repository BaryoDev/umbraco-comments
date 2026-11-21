using BaryoDev.Umbraco.Comments.Data;

namespace BaryoDev.Umbraco.Comments.Tests;

public class ReplyViewTests
{
    private static CommentRow Row(string name, int minute, Guid? parent = null, Guid? author = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentKey = Guid.NewGuid(),
        Name = name,
        Body = "body " + name,
        Status = (int)CommentStatus.Approved,
        CreatedUtc = new DateTime(2026, 11, 21, 12, minute, 0, DateTimeKind.Utc),
        ParentId = parent,
        AuthorUserKey = author,
    };

    [Fact]
    public void Thread_puts_replies_under_their_parent_oldest_first()
    {
        var first = Row("Ana", 1);
        var second = Row("Ben", 2);
        var late = Row("Site", 30, first.Id, Guid.NewGuid());
        var early = Row("Cleo", 10, first.Id);
        var other = Row("Dan", 20, second.Id);

        var thread = CommentsViewComponent.Thread([first, second], [late, other, early]);

        Assert.Equal(2, thread.Count);
        Assert.Equal(first.Id, thread[0].Id);
        Assert.Equal(["Cleo", "Site"], thread[0].Replies.Select(r => r.Name).ToArray());
        Assert.All(thread[0].Replies, r => Assert.Equal("Ana", r.ReplyingTo));
        Assert.True(thread[0].Replies[1].IsSiteReply);
        Assert.False(thread[0].Replies[0].IsSiteReply);
        Assert.Equal(["Dan"], thread[1].Replies.Select(r => r.Name).ToArray());
        Assert.Equal("Ben", thread[1].Replies[0].ReplyingTo);
    }

    [Fact]
    public void Thread_drops_replies_without_a_listed_parent()
    {
        var parent = Row("Ana", 1);
        var orphan = Row("Eve", 5, Guid.NewGuid());

        var thread = CommentsViewComponent.Thread([parent], [orphan]);

        Assert.Single(thread);
        Assert.Empty(thread[0].Replies);
    }

    [Fact]
    public void A_visitor_using_the_owner_name_gets_no_badge()
    {
        var visitor = Row("Owner", 1);

        var thread = CommentsViewComponent.Thread([visitor], []);

        Assert.False(thread[0].IsSiteReply);
        Assert.Null(thread[0].ReplyingTo);
    }

    [Fact]
    public void View_markup_has_anchor_badge_and_reply_hint()
    {
        string markup = File.ReadAllText(FindView());

        Assert.Contains("id=\"comment-@", markup);
        Assert.Contains("bdc__replies", markup);
        Assert.Contains("bdc__badge", markup);
        Assert.Contains("Never shown on the page. Used only to email you if someone replies.", markup);
    }

    private static string FindView()
    {
        string relative = Path.Combine("src", "BaryoDev.Umbraco.Comments", "Views", "Shared", "Components", "BaryoComments", "Default.cshtml");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("Default.cshtml not found above " + AppContext.BaseDirectory);
    }
}
