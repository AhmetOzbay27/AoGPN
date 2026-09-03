namespace ServiceLib.Manager;

/// <summary>
/// Payload for a snackbar notification that carries an action button. The action
/// button is optional: dismissing or ignoring the notification leaves the
/// <see cref="Handler"/> uninvoked.
/// </summary>
public sealed record ActionNotice(string Content, string ActionContent, Action Handler);

public class NoticeManager
{
    private static readonly Lazy<NoticeManager> _instance = new(() => new());
    public static NoticeManager Instance => _instance.Value;

    public void Enqueue(string? content)
    {
        if (content.IsNullOrEmpty())
        {
            return;
        }
        AppEvents.SendSnackMsgRequested.Publish(content);
    }

    public void SendMessage(string? content)
    {
        if (content.IsNullOrEmpty())
        {
            return;
        }
        AppEvents.SendMsgViewRequested.Publish(content);
    }

    public void SendMessageEx(string? content)
    {
        if (content.IsNullOrEmpty())
        {
            return;
        }
        content = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {content}";
        SendMessage(content);
    }

    public void SendMessageAndEnqueue(string? msg)
    {
        Enqueue(msg);
        SendMessage(msg);
    }

    /// <summary>
    /// Enqueues a snackbar notification with an action button. Clicking the action
    /// invokes <paramref name="actionHandler"/> on the UI thread; the notification is
    /// purely informational if the user ignores or dismisses it. Nothing is published
    /// when <paramref name="content"/> is empty or <paramref name="actionHandler"/> is
    /// <c>null</c>.
    /// </summary>
    public void EnqueueAction(string? content, string? actionContent, Action? actionHandler)
    {
        if (content.IsNullOrEmpty() || actionHandler is null)
        {
            return;
        }
        AppEvents.SendSnackActionRequested.Publish(
            new ActionNotice(content, actionContent ?? string.Empty, actionHandler));
    }

    /// <summary>
    /// Sends each error and warning in <paramref name="validatorResult"/> to the message panel
    /// and enqueues a summary snack notification (capped at 10 messages).
    /// Returns <c>true</c> when there were any messages so the caller can decide on early-return
    /// based on <see cref="NodeValidatorResult.Success"/>.
    /// </summary>
    public bool NotifyValidatorResult(NodeValidatorResult validatorResult)
    {
        var msgs = new List<string>([.. validatorResult.Errors, .. validatorResult.Warnings]);
        if (msgs.Count == 0)
        {
            return false;
        }
        foreach (var msg in msgs)
        {
            SendMessage(msg);
        }
        Enqueue(Utils.List2String(msgs.Take(10).ToList(), true));
        return true;
    }
}
