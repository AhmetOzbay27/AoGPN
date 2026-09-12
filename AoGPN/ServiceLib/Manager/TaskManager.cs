namespace ServiceLib.Manager;

public class TaskManager
{
    private static readonly Lazy<TaskManager> _instance = new(() => new());
    public static TaskManager Instance => _instance.Value;
    /// <summary>
    /// Çekirdek güncelleme kontrolünün İLK çalışma dakikası (dakika cinsinden).
    ///
    /// Eskiden 1 idi: sayaç 1'den başladığı için kontrol açılıştan ~1 dakika sonra,
    /// yani tam ilk bağlanma penceresinde çalışıyordu ve GitHub'a giden ağ trafiği
    /// bağlanmayı yavaşlatıyordu (canlı gözlenen). 10. dakikaya alındı — açılış ve
    /// ilk bağlanma tamamen bu trafikten arınmış kalır, 24 saatlik periyot korunur.
    /// </summary>
    private const int UpdateCheckFirstRunMinute = 10;

    private Config _config;
    private Func<bool, string, Task>? _updateFunc;

    /// <summary>
    /// "Şu an bakım tiki çalıştırılmasın" sinyali (Faz 1 — tünel koruması).
    /// Bağlanma uçuştayken veya tünel yeni kurulmuşken TRUE döner: abonelik
    /// güncellemesi tamamlandığında <c>UpdateTaskHandler → Reload()</c> zinciri
    /// çalışır ve maç ortasında kesinti üretmemelidir. Erteleme KAYIP DEĞİLDİR:
    /// süresi gelmiş abonelik bir sonraki dakikada yeniden denenir.
    /// </summary>
    private Func<bool>? _shouldDefer;

    public void RegUpdateTask(Config config, Func<bool, string, Task> updateFunc, Func<bool>? shouldDefer = null)
    {
        _config = config;
        _updateFunc = updateFunc;
        _shouldDefer = shouldDefer;

        Task.Run(ScheduledTasks);
    }

    private async Task ScheduledTasks()
    {
        Logging.SaveLog("Setup Scheduled Tasks");

        var numOfExecuted = 1;
        while (true)
        {
            //1 minute
            await Task.Delay(1000 * 60);

            //Execute once 1 minute
            try
            {
                await UpdateTaskRunSubscription();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ScheduledTasks - UpdateTaskRunSubscription", ex);
            }

            //Execute once 20 minute
            if (numOfExecuted % 20 == 0)
            {
                //Logging.SaveLog("Execute save config");

                try
                {
                    await ConfigHandler.SaveConfig(_config);
                    await ProfileExManager.Instance.SaveTo();
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - SaveConfig", ex);
                }
            }

            //Execute once 1 hour
            if (numOfExecuted % 60 == 0)
            {
                //Logging.SaveLog("Execute delete expired files");

                FileUtils.DeleteExpiredFiles(Utils.GetBinConfigPath(), DateTime.Now.AddHours(-1), "Test");
                FileUtils.DeleteExpiredFiles(Utils.GetLogPath(), DateTime.Now.AddMonths(-1));
                FileUtils.DeleteExpiredFiles(Utils.GetTempPath(), DateTime.Now.AddMonths(-1));

                try
                {
                    await UpdateTaskRunGeo(numOfExecuted / 60);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - UpdateTaskRunGeo", ex);
                }
            }

            //Execute once 24 hour (ilk çalışma UpdateCheckFirstRunMinute'da)
            if (numOfExecuted % 1440 == UpdateCheckFirstRunMinute)
            {
                try
                {
                    await UpdateTaskRunCheckUpdate();
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ScheduledTasks - UpdateTaskRunCheckUpdate", ex);
                }
            }
            numOfExecuted++;
        }
    }

    private async Task UpdateTaskRunSubscription()
    {
        if (_shouldDefer?.Invoke() == true)
        {
            Logging.SaveLog("ScheduledTasks - subscription update deferred (connection in progress)");
            return;
        }

        var updateTime = ((DateTimeOffset)DateTime.Now).ToUnixTimeSeconds();
        var lstSubs = (await AppManager.Instance.SubItems())?
            .Where(t => t.AutoUpdateInterval > 0)
            .Where(t => updateTime - t.UpdateTime >= t.AutoUpdateInterval * 60)
            .ToList();

        if (lstSubs is not { Count: > 0 })
        {
            return;
        }

        Logging.SaveLog("Execute update subscription");

        foreach (var item in lstSubs)
        {
            await SubscriptionHandler.UpdateProcess(_config, item.Id, true, async (success, msg) =>
            {
                await _updateFunc?.Invoke(success, msg);
                if (success)
                {
                    Logging.SaveLog($"Update subscription end. {msg}");
                }
            });
            item.UpdateTime = updateTime;
            await ConfigHandler.AddSubItem(_config, item);
            await Task.Delay(1000);
        }
    }

    private async Task UpdateTaskRunGeo(int hours)
    {
        if (_config.GuiItem.AutoUpdateInterval > 0 && hours > 0 && hours % _config.GuiItem.AutoUpdateInterval == 0)
        {
            Logging.SaveLog("Execute update geo files");

            await new UpdateService(_config, async (success, msg) =>
            {
                await _updateFunc?.Invoke(false, msg);
            }).UpdateGeoFileAll();
        }
    }

    private async Task UpdateTaskRunCheckUpdate()
    {
        if (_shouldDefer?.Invoke() == true)
        {
            Logging.SaveLog("ScheduledTasks - update check deferred (connection in progress)");
            return;
        }

        Logging.SaveLog("Execute check update");

        var updateService = new UpdateService(_config, async (success, msg) => await Task.CompletedTask);

        var msgs = await updateService.CheckHasUpdateOnlyAll(_config.CheckUpdateItem.CheckPreReleaseUpdate);
        foreach (var msg in msgs)
        {
            await _updateFunc?.Invoke(false, msg);
        }
        NoticeManager.Instance.Enqueue(string.Join("\n", msgs));

        if (msgs.Count > 0)
        {
            AppEvents.HasUpdateNotified.Publish(true);
        }
    }
}
