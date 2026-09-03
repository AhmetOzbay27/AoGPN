namespace ServiceLib.ViewModels;

public class SubSettingViewModel : MyReactiveObject
{
    public Interaction<string, bool> ShowYesNoInteraction { get; } = new();
    public Interaction<string, Unit> ShareSubInteraction { get; } = new();
    public Interaction<List<FreeNodeSources.Source>, List<FreeNodeSources.Source>> ImportPublicSourcesInteraction { get; } = new();

    public IObservableCollection<SubItem> SubItems { get; } = new ObservableCollectionExtended<SubItem>();

    [Reactive]
    public SubItem SelectedSource { get; set; }

    public IList<SubItem> SelectedSources { get; set; }

    public ReactiveCommand<Unit, Unit> SubAddCmd { get; }
    public ReactiveCommand<Unit, Unit> SubDeleteCmd { get; }
    public ReactiveCommand<Unit, Unit> SubEditCmd { get; }
    public ReactiveCommand<Unit, Unit> SubShareCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportPublicSourcesCmd { get; }
    public bool IsModified { get; set; }

    public SubSettingViewModel()
    {
        _config = AppManager.Instance.Config;

        var canEditRemove = this.WhenAnyValue(
           x => x.SelectedSource,
           selectedSource => selectedSource != null && !selectedSource.Id.IsNullOrEmpty());

        SubAddCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await EditSubAsync(true);
        });
        SubDeleteCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await DeleteSubAsync();
        }, canEditRemove);
        SubEditCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await EditSubAsync(false);
        }, canEditRemove);
        SubShareCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ShareSubInteraction.Handle(SelectedSource?.Url);
        }, canEditRemove);
        ImportPublicSourcesCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ImportPublicSourcesAsync();
        });

        _ = Init();
    }

    private async Task Init()
    {
        SelectedSource = new();

        await RefreshSubItems();
    }

    public async Task RefreshSubItems()
    {
        SubItems.Clear();
        SubItems.AddRange(await AppManager.Instance.SubItems());
    }

    public async Task EditSubAsync(bool blNew)
    {
        SubItem item;
        if (blNew)
        {
            item = new();
        }
        else
        {
            item = await AppManager.Instance.GetSubItem(SelectedSource?.Id);
            if (item is null)
            {
                return;
            }
        }
        var subEditViewModel = new SubEditViewModel(item);
        if (await AppManager.Instance.WindowDialog.ShowDialogAsync(subEditViewModel) == true)
        {
            await RefreshSubItems();
            IsModified = true;
        }
    }

    private async Task DeleteSubAsync()
    {
        if (await ShowYesNoInteraction.Handle(ResUI.RemoveServer) == false)
        {
            return;
        }

        foreach (var it in SelectedSources ?? [SelectedSource])
        {
            await ConfigHandler.DeleteSubItem(_config, it.Id);
        }
        await RefreshSubItems();
        NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        IsModified = true;
    }

    private async Task ImportPublicSourcesAsync()
    {
        var allSources = FreeNodeSources.All.ToList();
        var selected = await ImportPublicSourcesInteraction.Handle(allSources);
        if (selected is not { Count: > 0 })
        {
            return;
        }

        var countAdded = 0;
        foreach (var source in selected)
        {
            // Skip if URL already exists
            var existing = await SQLiteHelper.Instance.TableAsync<SubItem>()
                .FirstOrDefaultAsync(e => e.Url == source.Url);
            if (existing != null)
            {
                continue;
            }

            var subItem = new SubItem
            {
                Id = string.Empty,
                Url = source.Url,
                Remarks = $"{(source.Flag.IsNotEmpty() ? source.Flag + " " : "")}{source.Name}",
                Enabled = true,
                AutoUpdateInterval = 360, // 6 hours default
            };

            if (await ConfigHandler.AddSubItem(_config, subItem) == 0)
            {
                countAdded++;
            }
        }

        await RefreshSubItems();
        IsModified = countAdded > 0;
        NoticeManager.Instance.Enqueue(
            countAdded > 0
                ? string.Format(ResUI.MsgPublicSourcesImported, countAdded)
                : ResUI.MsgPublicSourcesAlreadyImported);
    }
}
