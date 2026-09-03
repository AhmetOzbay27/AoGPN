using ServiceLib.Models.Dto;

namespace ServiceLib.ViewModels;

/// <summary>
/// Edit dialog for a manual route entry (app or domain). Keeps working copies of the
/// editable fields; the caller applies them to the real item only when the dialog
/// closes with OK, so Cancel / window-X never mutates the list.
/// </summary>
public class ManualRouteEditViewModel : MyReactiveObject, ICloseable
{
    public event EventHandler? RequestClose;

    /// <summary>Entry type: "app", "domain" or "ip".</summary>
    public string EntryType { get; }

    public bool IsApp => EntryType == "app";

    /// <summary>Localized entry type shown in the dialog.</summary>
    public string EntryTypeLabel => EntryType switch
    {
        "ip" => ResUI.ManualEntryIp,
        "app" => ResUI.ManualEntryApp,
        _ => ResUI.ManualEntryDomain,
    };

    /// <summary>Localized label for the value field (process name / domain / IP).</summary>
    public string ValueLabel => EntryType switch
    {
        "ip" => ResUI.ManualEditValueIp,
        "app" => ResUI.ManualEditValueApp,
        _ => ResUI.ManualEditValueDomain,
    };

    [Reactive]
    public string DisplayName { get; set; }

    /// <summary>Process name (game.exe) for apps, or the host (domain/IP) without the port.</summary>
    [Reactive]
    public string Value { get; set; }

    /// <summary>Port parsed from the value field (e.g. "443"). Empty when none.</summary>
    [Reactive]
    public string Port { get; set; }

    [Reactive]
    public string? Action { get; set; }

    public List<ComboItem> ManualActions { get; } = new();

    public ReactiveCommand<Unit, Unit> SaveCmd { get; }

    public ManualRouteEditViewModel(SplitTunnelAppItem item, List<ComboItem> actions)
    {
        EntryType = item.EntryType;
        DisplayName = item.DisplayName;
        Value = item.Value;
        Action = item.Action;

        ManualActions.AddRange(actions);
        SaveCmd = ReactiveCommand.Create(Save);
    }

    private void Save()
    {
        var input = Value?.Trim() ?? "";
        if (input.IsNullOrEmpty() || input.Contains(' '))
        {
            NoticeManager.Instance.Enqueue(ResUI.ManualInvalidValue);
            return;
        }

        if (IsApp)
        {
            if (input.Contains('/') || input.Contains('\\'))
            {
                NoticeManager.Instance.Enqueue(ResUI.ManualInvalidValue);
                return;
            }
            Value = input;
            Port = "";
        }
        else
        {
            // Domain / IP — accept an optional ":port" suffix.
            if (!ManualRouteParser.TrySplitPort(input, out var host, out var port) || host.IsNullOrEmpty())
            {
                NoticeManager.Instance.Enqueue(ResUI.ManualInvalidDomain);
                return;
            }
            if (!ManualRouteParser.IsValidHost(host, EntryType))
            {
                NoticeManager.Instance.Enqueue(ResUI.ManualInvalidDomain);
                return;
            }
            Value = host;
            Port = port;
        }

        DisplayName = DisplayName?.Trim().IsNotEmpty() == true ? DisplayName.Trim() : input;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
