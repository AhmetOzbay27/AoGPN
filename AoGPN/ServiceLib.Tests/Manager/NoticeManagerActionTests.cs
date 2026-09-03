using AwesomeAssertions;
using ServiceLib.Events;
using ServiceLib.Manager;
using Xunit;

namespace ServiceLib.Tests.Manager;

public sealed class NoticeManagerActionTests
{
    [Fact]
    public void EnqueueAction_PublishesActionNoticeWithContentAndHandler()
    {
        ActionNotice? received = null;
        using var subscription = AppEvents.SendSnackActionRequested
            .AsObservable()
            .Subscribe(notice => received = notice);

        var invoked = false;
        NoticeManager.Instance.EnqueueAction(
            "content", "action",
            () => invoked = true);

        received.Should().NotBeNull();
        received!.Content.Should().Be("content");
        received.ActionContent.Should().Be("action");

        received.Handler();
        invoked.Should().BeTrue();
    }

    [Fact]
    public void EnqueueAction_NullContent_DoesNotPublish()
    {
        ActionNotice? received = null;
        using var subscription = AppEvents.SendSnackActionRequested
            .AsObservable()
            .Subscribe(notice => received = notice);

        NoticeManager.Instance.EnqueueAction(null, "action", () => { });

        received.Should().BeNull();
    }

    [Fact]
    public void EnqueueAction_EmptyContent_DoesNotPublish()
    {
        ActionNotice? received = null;
        using var subscription = AppEvents.SendSnackActionRequested
            .AsObservable()
            .Subscribe(notice => received = notice);

        NoticeManager.Instance.EnqueueAction(string.Empty, "action", () => { });

        received.Should().BeNull();
    }

    [Fact]
    public void EnqueueAction_NullHandler_DoesNotPublish()
    {
        ActionNotice? received = null;
        using var subscription = AppEvents.SendSnackActionRequested
            .AsObservable()
            .Subscribe(notice => received = notice);

        NoticeManager.Instance.EnqueueAction("content", "action", null);

        received.Should().BeNull();
    }
}
