namespace ServiceLib.ViewModels;

public class FullConfigTemplateViewModel : MyReactiveObject, ICloseable
{
    public event EventHandler? RequestClose;

    #region Reactive

    [Reactive]
    public bool EnableFullConfigTemplate4Ray { get; set; }

    [Reactive]
    public string FullConfigTemplate4Ray { get; set; } = string.Empty;

    [Reactive]
    public string FullTunConfigTemplate4Ray { get; set; } = string.Empty;

    [Reactive]
    public bool AddProxyOnly4Ray { get; set; }

    [Reactive]
    public string ProxyDetour4Ray { get; set; } = string.Empty;

    public ReactiveCommand<Unit, Unit> SaveCmd { get; }

    #endregion Reactive

    public FullConfigTemplateViewModel()
    {
        _config = AppManager.Instance.Config;
        SaveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SaveSettingAsync();
        });

        _ = Init();
    }

    private async Task Init()
    {
        var item = await AppManager.Instance.GetFullConfigTemplateItem(ECoreType.Xray);
        if (item == null)
        {
            return;
        }
        EnableFullConfigTemplate4Ray = item.Enabled;
        FullConfigTemplate4Ray = item.Config ?? string.Empty;
        FullTunConfigTemplate4Ray = item.TunConfig ?? string.Empty;
        AddProxyOnly4Ray = item.AddProxyOnly ?? false;
        ProxyDetour4Ray = item.ProxyDetour ?? string.Empty;
    }

    private async Task SaveSettingAsync()
    {
        if (!await SaveXrayConfigAsync())
        {
            return;
        }

        NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> SaveXrayConfigAsync()
    {
        var item = await AppManager.Instance.GetFullConfigTemplateItem(ECoreType.Xray);
        if (item == null)
        {
            return false;
        }
        item.Enabled = EnableFullConfigTemplate4Ray;
        item.Config = FullConfigTemplate4Ray;
        item.TunConfig = FullTunConfigTemplate4Ray;

        item.AddProxyOnly = AddProxyOnly4Ray;
        item.ProxyDetour = ProxyDetour4Ray;

        await ConfigHandler.SaveFullConfigTemplate(_config, item);
        return true;
    }
}