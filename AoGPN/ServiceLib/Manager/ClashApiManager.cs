using static ServiceLib.Models.Dto.ClashProxies;

namespace ServiceLib.Manager;

public sealed class ClashApiManager
{
    private static readonly Lazy<ClashApiManager> instance = new(() => new());
    public static ClashApiManager Instance => instance.Value;

    private static readonly string _tag = "ClashApiHandler";
    private Dictionary<string, ProxiesItem>? _proxies;
    public Dictionary<string, object> ProfileContent { get; set; }

    /// <summary>
    /// Denetleyici (mihomo dış API) için devre kesici — çekirdek kapalı/başlarken
    /// tekrar tekrar bağlantı denemesi ve bekleme yapılmaz.
    /// </summary>
    private readonly ClashApiBackoffPolicy _controllerBackoff;

    public ClashApiManager()
        : this(new ClashApiBackoffPolicy())
    {
    }

    /// <summary>
    /// Geri çekilme politikası dışarıdan verilebilir: testler çok kısa aralıklarla
    /// gerçek beklemeler olmadan devre kesici davranışını doğrular.
    /// </summary>
    internal ClashApiManager(ClashApiBackoffPolicy controllerBackoff)
    {
        _controllerBackoff = controllerBackoff;
    }

    /// <summary>
    /// Denetleyiciden anlık proxy durumunu okur. Eskiden 3 deneme × 2 istek + 2 sn
    /// bekleme yapılıyordu: tek çağrı 6 başarısız bağlantı ve ~4 sn gecikme üretip
    /// GPN bağlanma penceresini ve log hacmini şişiriyordu. Yeni davranış:
    /// <list type="bullet">
    ///   <item>devre kesici açıksa hiç istek atılmaz (anında null),</item>
    ///   <item>tek deneme yapılır; <c>/proxies</c> yanıtsızsa <c>/providers/proxies</c> hiç denenmez,</item>
    ///   <item>başarısızlıklar kademeli geri çekilme ile ertelenir, ilk yanıtta sıfırlanır.</item>
    /// </list>
    /// </summary>
    /// <param name="forceAttempt">
    /// Karar-kritik okumalar (yumuşak düğüm geçişi, bypass egress, soft policy)
    /// için <c>true</c>: geri çekilme penceresi açık olsa bile denenir. Bu yollar
    /// boş okumayı "çalışan config'te grup yok" sayıp durdur/başlat fallback'ine
    /// düşer — bayat bir geri çekilme penceresi bunu tetiklememelidir. Anket/
    /// heartbeat yolları varsayılan (false) ile geri çekilmeye uyar.
    /// </param>
    public Task<Tuple<ClashProxies, ClashProviders>?> GetClashProxiesAsync(bool forceAttempt = false)
        => GetClashProxiesAsync(GetApiUrl(), HttpClientHelper.Instance.TryGetAsync, forceAttempt);

    /// <summary>
    /// <see cref="GetClashProxiesAsync(bool)"/>'in test edilebilir hali: adres ve ağ
    /// erişimi dışarıdan verilir (testler gerçek soket açmaz).
    /// </summary>
    internal async Task<Tuple<ClashProxies, ClashProviders>?> GetClashProxiesAsync(
        string apiUrl,
        Func<string, Task<string?>> fetchAsync,
        bool forceAttempt = false)
    {
        if (!forceAttempt && !_controllerBackoff.ShouldAttemptAt(DateTime.UtcNow))
        {
            return null;
        }

        var result = await fetchAsync($"{apiUrl}/proxies");
        if (result is null)
        {
            // Denetleyici yanıt vermiyor: providers uç noktası da kesin başarısız
            // olurdu — istek sayısı yarıya iner ve çağıran 4 sn beklemez.
            RecordControllerFailure("GET /proxies");
            return null;
        }

        // Yanıt geldi: denetleyici ayakta — geri çekilme kapanır.
        RecordControllerSuccess();

        var clashProxies = JsonUtils.Deserialize<ClashProxies>(result);

        var result2 = await fetchAsync($"{apiUrl}/providers/proxies");
        var clashProviders = JsonUtils.Deserialize<ClashProviders>(result2);

        if (clashProxies is null && clashProviders is null)
        {
            return null;
        }

        _proxies = clashProxies?.proxies;
        return new Tuple<ClashProxies, ClashProviders>(clashProxies, clashProviders);
    }

    /// <summary>
    /// Başarısız denetleyici erişimini kaydeder. Log yalnızca ardışık hataların
    /// İLKİNDE yazılır: anketlerin asıl gürültüsü buydu (her poll'da N satır).
    /// </summary>
    private void RecordControllerFailure(string what)
    {
        var firstFailure = _controllerBackoff.ConsecutiveFailures == 0;
        _controllerBackoff.RecordFailureAt(DateTime.UtcNow);
        if (firstFailure)
        {
            var cooldown = _controllerBackoff.RemainingCooldownAt(DateTime.UtcNow);
            Logging.SaveLog($"[{_tag}] denetleyici erişilemiyor ({what}) — {cooldown.TotalMilliseconds:F0} ms geri çekilme başladı");
        }
    }

    /// <summary>
    /// Denetleyici erişilebilir: geri çekilmeyi sıfırlar ve toparlanmayı bir kez loglar.
    /// </summary>
    private void RecordControllerSuccess()
    {
        if (_controllerBackoff.ConsecutiveFailures == 0)
        {
            return;
        }
        _controllerBackoff.RecordSuccess();
        Logging.SaveLog($"[{_tag}] denetleyici yeniden erişilebilir");
    }

    public void ClashProxiesDelayTest(bool blAll, List<ClashProxyModel> lstProxy, Func<ClashProxyModel?, string, Task> updateFunc)
    {
        Task.Run(async () =>
        {
            if (blAll)
            {
                if (_proxies == null)
                {
                    await GetClashProxiesAsync();
                }
                lstProxy = [];
                lstProxy.AddRange(from kv in _proxies ?? []
                                  where !Global.notAllowTestType.Contains(kv.Value.type?.ToLower())
                                  select new ClashProxyModel()
                                  {
                                      Name = kv.Value.name,
                                      Type = kv.Value.type?.ToLower(),
                                  });
            }

            if (lstProxy is not { Count: > 0 })
            {
                return;
            }
            var urlBase = $"{GetApiUrl()}/proxies";
            urlBase += @"/{0}/delay?timeout=10000&url=" + AppManager.Instance.Config.SpeedTestItem.SpeedPingTestUrl;

            var tasks = new List<Task>();
            foreach (var it in lstProxy)
            {
                if (Global.notAllowTestType.Contains(it.Type.ToLower()))
                {
                    continue;
                }
                var name = it.Name;
                var url = string.Format(urlBase, name);
                tasks.Add(Task.Run(async () =>
                {
                    var result = await HttpClientHelper.Instance.TryGetAsync(url);
                    await updateFunc?.Invoke(it, result);
                }));
            }
            await Task.WhenAll(tasks);
            await Task.Delay(1000);
            await updateFunc?.Invoke(null, "");
        });
    }

    public List<ProxiesItem>? GetClashProxyGroups()
    {
        try
        {
            var fileContent = ProfileContent;
            if (fileContent is null || fileContent?.ContainsKey("proxy-groups") == false)
            {
                return null;
            }
            return JsonUtils.Deserialize<List<ProxiesItem>>(JsonUtils.Serialize(fileContent["proxy-groups"]));
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    public async Task ClashSetActiveProxy(string name, string nameNode)
    {
        try
        {
            var url = $"{GetApiUrl()}/proxies/{name}";
            var headers = new Dictionary<string, string>();
            headers.Add("name", nameNode);
            await HttpClientHelper.Instance.PutAsync(url, headers);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public async Task ClashConfigUpdate(Dictionary<string, string> headers)
    {
        if (_proxies == null)
        {
            return;
        }

        var urlBase = $"{GetApiUrl()}/configs";

        await HttpClientHelper.Instance.PatchAsync(urlBase, headers);
    }

    /// <summary>
    /// Çekirdek config'ini SÜRECİ DURDURMADAN yeniden yükler
    /// (<c>PUT /configs?force=true</c>, mihomo hot reload). Mevcut TCP/UDP
    /// oturumları korunur — bağlantı kapatma (<c>DELETE /connections</c>) bilinçli
    /// olarak ÇAĞRILMAZ; oturum sürekliliği (soft reload) yolunun temelidir.
    /// Başarı = HTTP 2xx. Hata/reddedilme durumunda çağıran restart yoluna düşer.
    /// </summary>
    public async Task<bool> ClashConfigReload(string filePath)
    {
        try
        {
            var url = $"{GetApiUrl()}/configs?force=true";
            var headers = new Dictionary<string, string>
            {
                ["path"] = filePath,
            };
            var (ok, body) = await HttpClientHelper.Instance.TryPutAsync(url, headers);
            if (!ok)
            {
                Logging.SaveLog($"[{_tag}] config reload reddedildi: {body ?? "(yanıt yok)"}");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }

    /// <summary>
    /// Denetleyiciden canlı bağlantı listesini okur. Aynı devre kesiciye tabidir:
    /// çekirdek kapalı/başlarken saniyelik anketler artık üst üste reddedilen
    /// bağlantı ve istisna üretmez.
    /// </summary>
    public Task<ClashConnections?> GetClashConnectionsAsync(bool forceAttempt = false)
        => GetClashConnectionsAsync(GetApiUrl(), HttpClientHelper.Instance.TryGetAsync, forceAttempt);

    /// <summary><see cref="GetClashConnectionsAsync(bool)"/>'in test edilebilir hali.</summary>
    internal async Task<ClashConnections?> GetClashConnectionsAsync(
        string apiUrl,
        Func<string, Task<string?>> fetchAsync,
        bool forceAttempt = false)
    {
        if (!forceAttempt && !_controllerBackoff.ShouldAttemptAt(DateTime.UtcNow))
        {
            return null;
        }

        try
        {
            var result = await fetchAsync($"{apiUrl}/connections");
            if (result is null)
            {
                RecordControllerFailure("GET /connections");
                return null;
            }

            // Yanıt geldi = denetleyici ayakta; gövde boşsa çözümleme null dönebilir.
            RecordControllerSuccess();
            return JsonUtils.Deserialize<ClashConnections>(result);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            RecordControllerFailure(ex.Message);
        }

        return null;
    }

    public async Task ClashConnectionClose(string id)
    {
        try
        {
            var url = $"{GetApiUrl()}/connections/{id}";
            await HttpClientHelper.Instance.DeleteAsync(url);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private string GetApiUrl()
    {
        return $"{Global.HttpProtocol}{Global.Loopback}:{AppManager.Instance.StatePort2}";
    }
}
