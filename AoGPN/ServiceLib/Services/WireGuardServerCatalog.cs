using System.Text;
using System.Text.RegularExpressions;
using ServiceLib.Common;
using ServiceLib.Handler.Fmt;
using ServiceLib.Manager;

namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// WireGuardServerCatalog — "Bağlan" için GPN aday kaynağı (Faz 3)
//
// Birincil kaynak artık SQLite `gpn_servers` tablosudur (GpnServerItem):
// İtalya/Almanya WireGuard sunucuları bu tabloda saklanır ve GpnServerProfile
// adaylarına eşlenir. İstemci özel anahtarı DpapiCryptor ile şifrelenerek
// yazılır (düz metin diske çıkmaz) ve yalnızca okuma/bağlantı anında çözülür.
//
// - .conf / wireguard:// içe aktarımı (Faz 1) bir ProfileItem üretir; bu sınıf
//   ProfileItem'ı gpn_servers satırına yükseltir (upsert — ServerId uç noktadan
//   türetildiği için aynı .conf'i yeniden içe aktarmak yeni satır oluşturmaz).
// - gpn_servers boşsa LoadAsync, mevcut saklı WireGuard ProfileItem'larını
//   tohumlayarak (seed) tabloyu doldurur — eski içe aktarımlar da geriye dönük
//   çalışır.
// ─────────────────────────────────────────────────────────────────────────

public static class WireGuardServerCatalog
{
    private const string Tag = "WgCatalog";

    /// <summary>
    /// Uygulamanın SQLite veri tabanındaki tüm GPN sunucularını
    /// <see cref="GpnServerProfile"/> adaylarına eşler. Tablo boşsa mevcut
    /// WireGuard ProfileItem'larından tohumlanır; özel anahtar DPAPI ile çözülür.
    /// </summary>
    public static async Task<IReadOnlyList<GpnServerProfile>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (await TableCountAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            await SeedAsync(cancellationToken).ConfigureAwait(false);
        }

        List<GpnServerItem>? rows;
        try
        {
            rows = await SQLiteHelper.Instance.TableAsync<GpnServerItem>().ToListAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] gpn_servers okunamadı: {ex.Message}");
            return Array.Empty<GpnServerProfile>();
        }

        var result = new List<GpnServerProfile>();
        foreach (var row in rows ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryMap(row, out var profile))
            {
                result.Add(profile);
            }
        }

        return result;
    }

    /// <summary>
    /// gpn_servers'a tek bir WireGuard sunucusu ekler/günceller. Girdi ya bir
    /// GpnServerItem ya da bir GpnServerProfile olabilir (<see cref="UpsertAsync(GpnServerProfile)"/>).
    /// Private key DPAPI ile şifrelenir. Başarılıysa 1, atlanırsa/hatada 0.
    /// </summary>
    public static async Task<int> UpsertAsync(GpnServerItem item, CancellationToken cancellationToken = default)
    {
        if (item is null || item.EndpointHost.IsNullOrEmpty() || item.EndpointPort is <= 0 or >= 65536
            || item.ServerPublicKey.IsNullOrEmpty() || item.ClientPrivateKeyEnc.IsNullOrEmpty())
        {
            return 0;
        }

        if (!TryMap(item, out _))
        {
            return 0;
        }

        return await PersistAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GpnServerProfile'ı gpn_servers'a yazar (private key DPAPI ile şifrelenir).</summary>
    public static async Task<int> UpsertAsync(GpnServerProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile is null
            || profile.EndpointHost.IsNullOrEmpty() || profile.EndpointPort is <= 0 or >= 65536
            || profile.ServerPublicKey.IsNullOrEmpty() || profile.ClientPrivateKey.IsNullOrEmpty())
        {
            return 0;
        }

        var item = ToItem(profile);
        return await PersistAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bir WireGuard ProfileItem'ı gpn_servers'a eşler ve (Geçerli) yazar.
    /// Faz 1 içe aktarım akışı (AddBatchServers4Wireguard / AddServer) burayı çağırır.
    /// </summary>
    public static async Task<int> UpsertFromProfileAsync(ProfileItem item, CancellationToken cancellationToken = default)
    {
        if (item?.ConfigType != EConfigType.WireGuard)
        {
            return 0;
        }
        if (!TryMap(item, out var profile))
        {
            return 0;
        }

        var gpnItem = ToItem(profile);
        gpnItem.SourceProfileId = item.IndexId;
        return await PersistAsync(gpnItem, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> RemoveAsync(string serverId, CancellationToken cancellationToken = default)
    {
        if (serverId.IsNullOrEmpty())
        {
            return 0;
        }
        try
        {
            var item = await SQLiteHelper.Instance.TableAsync<GpnServerItem>()
                .FirstOrDefaultAsync(t => t.ServerId == serverId).ConfigureAwait(false);
            if (item is null)
            {
                return 0;
            }
            return await SQLiteHelper.Instance.DeleteAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Silme hatası: {ex.Message}");
            return 0;
        }
    }

    public static async Task<List<GpnServerItem>?> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await SQLiteHelper.Instance.TableAsync<GpnServerItem>().ToListAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] gpn_servers okunamadı: {ex.Message}");
            return null;
        }
    }

    /// <summary>Tek bir WireGuard ProfileItem'ı GpnServerProfile'a eşler (ProbeTool + geriye dönük uyum).</summary>
    public static bool TryMap(ProfileItem item, out GpnServerProfile profile)
    {
        profile = null!;
        var extra = item.GetProtocolExtra();

        if (item.Password.IsNullOrEmpty()          // istemci özel anahtarı (base64)
            || extra.WgPublicKey.IsNullOrEmpty()   // sunucu genel anahtarı
            || item.Address.IsNullOrEmpty()
            || item.Port is <= 0 or >= 65536)
        {
            return false;
        }

        profile = new GpnServerProfile(
            // İçe aktarılan conf'tan gelen profillerde IndexId boş olabilir; o zaman
            // uç noktadan kararlı kimlik türet (failover matrisinin anahtarlama kusuru
            // olmasın ve aynı .conf yeniden içe aktarılınca satır çoğalmasın).
            ServerId: item.IndexId.IsNotEmpty()
                ? item.IndexId
                : StableServerId(item.Address, item.Port),
            Name: item.Remarks ?? $"WireGuard {item.Address}:{item.Port}",
            EndpointHost: item.Address,
            EndpointPort: item.Port,
            ServerPublicKey: extra.WgPublicKey,
            ClientPrivateKey: item.Password,
            ClientAddress: extra.WgInterfaceAddress ?? "10.66.66.2/24",
            Mtu: extra.WgMtu ?? Global.GpnRecommendedMtu,
            PersistentKeepalive: extra.WgPersistentKeepalive ?? 25);
        return true;
    }

    /// <summary>
    /// Disk satırını GpnServerProfile'a eşler: <see cref="GpnServerItem.ClientPrivateKeyEnc"/>
    /// DPAPI ile çözülür; çözülemezse satır atlanır (bozuk/başka kullanıcı).
    /// </summary>
    public static bool TryMap(GpnServerItem item, out GpnServerProfile profile)
    {
        profile = null!;
        if (item is null
            || item.EndpointHost.IsNullOrEmpty()
            || item.EndpointPort is <= 0 or >= 65536
            || item.ServerPublicKey.IsNullOrEmpty()
            || item.ClientPrivateKeyEnc.IsNullOrEmpty())
        {
            return false;
        }

        var privateKey = DpapiCryptor.Decrypt(item.ClientPrivateKeyEnc);
        if (privateKey.IsNullOrEmpty())
        {
            return false;
        }

        profile = new GpnServerProfile(
            ServerId: item.ServerId,
            Name: item.Name,
            EndpointHost: item.EndpointHost,
            EndpointPort: item.EndpointPort,
            ServerPublicKey: item.ServerPublicKey,
            ClientPrivateKey: privateKey,
            ClientAddress: item.ClientAddress,
            Mtu: item.Mtu > 0 ? item.Mtu : Global.GpnRecommendedMtu,
            Dns: item.Dns,
            PersistentKeepalive: item.Keepalive > 0 ? item.Keepalive : 25,
            IsEnabled: item.IsEnabled);
        return true;
    }

    private static GpnServerItem ToItem(GpnServerProfile profile)
    {
        return new GpnServerItem
        {
            ServerId = StableServerId(profile.EndpointHost, profile.EndpointPort),
            Name = profile.Name,
            EndpointHost = profile.EndpointHost,
            EndpointPort = profile.EndpointPort,
            ServerPublicKey = profile.ServerPublicKey,
            ClientPrivateKeyEnc = DpapiCryptor.Encrypt(profile.ClientPrivateKey),
            ClientAddress = profile.ClientAddress,
            Mtu = profile.Mtu > 0 ? profile.Mtu : Global.GpnRecommendedMtu,
            Dns = profile.Dns,
            Keepalive = profile.PersistentKeepalive > 0 ? profile.PersistentKeepalive : 25,
            IsEnabled = profile.IsEnabled,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    internal static string StableServerId(string host, int port) => $"{host}:{port}";

    // ── Gömülü varsayılan şablonlar: durum + geri yükleme ────────────────

    /// <summary>
    /// Gömülü ANAHTARSIZ şablonun uygulanabilir varsayılan alanları (istemci özel
    /// anahtarı hariç — sürüm kontrolüne yazılmaz). "Varsayılanları geri yükle"
    /// aksiyonu bu alanları mevcut kayda uygular; anahtar DPAPI'li kalır.
    /// </summary>
    public sealed record GpnDefaultServerTemplate(
        string Resource,
        string EndpointHost,
        int EndpointPort,
        string? ServerPublicKey,
        string? ClientAddress,
        int? Mtu,
        string? Dns,
        int? PersistentKeepalive)
    {
        public string ServerId => $"{EndpointHost}:{EndpointPort}";
    }

    /// <summary>Tek şablonun mevcut katalog kaydına göre durumu.</summary>
    public sealed record GpnDefaultsStatus(
        string ServerId,
        string EndpointHost,
        int EndpointPort,
        string? ServerPublicKey,
        bool Seeded,          // uç nokta için kayıt var mı
        bool KeyPresent,      // kaydın DPAPI'li anahtarı çözülebiliyor mu
        bool UpToDate,        // şablon alanları kayıtla eşleşiyor mu
        IReadOnlyList<string> Differences);

    /// <summary>Geri yükleme sonucu: güncellenen kayıt sayısı + güncel durumlar.</summary>
    public sealed record GpnDefaultsRestoreResult(
        int RestoredCount,
        IReadOnlyList<GpnDefaultsStatus> Statuses);

    /// <summary>Gömülü anahtarsız şablonları ayrıştırır (uç nokta, sunucu anahtarı, adres/MTU/DNS/keepalive).</summary>
    public static List<GpnDefaultServerTemplate> GetDefaultTemplates()
    {
        var templates = new List<GpnDefaultServerTemplate>();
        foreach (var resource in new[] { Global.GpnSampleItalyConf, Global.GpnSampleGermanyConf })
        {
            var confText = EmbedUtils.GetEmbedText(resource);
            if (confText.IsNullOrEmpty())
            {
                continue;
            }

            var endpoint = ExtractEndpointFromConf(confText);
            if (endpoint is null)
            {
                continue;
            }
            var sep = endpoint.LastIndexOf(':');
            if (sep <= 0 || !int.TryParse(endpoint[(sep + 1)..], out var port))
            {
                continue;
            }

            templates.Add(new GpnDefaultServerTemplate(
                resource,
                endpoint[..sep],
                port,
                MatchValue(confText, @"(?m)^\s*PublicKey\s*=\s*(\S+)"),
                MatchValue(confText, @"(?m)^\s*Address\s*=\s*(\S+)"),
                MatchInt(confText, @"(?m)^\s*MTU\s*=\s*(\d+)"),
                MatchValue(confText, @"(?m)^\s*DNS\s*=\s*(\S+)"),
                MatchInt(confText, @"(?m)^\s*PersistentKeepalive\s*=\s*(\d+)")));
        }
        return templates;
    }

    /// <summary>
    /// Gömülü şablonların katalog kayıtlarıyla durumunu hesaplar (saf okuma —
    /// ağ yok, yazma yok): hangi varsayılan sunucu tohumlandı, anahtarı var mı,
    /// şablon alanlarıyla güncel mi, farklı alanlar neler.
    /// </summary>
    public static async Task<IReadOnlyList<GpnDefaultsStatus>> GetDefaultsStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var templates = GetDefaultTemplates();
        var rows = await GetItemsAsync(cancellationToken).ConfigureAwait(false) ?? [];
        var statuses = new List<GpnDefaultsStatus>(templates.Count);

        foreach (var template in templates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows.FirstOrDefault(r => r.ServerId == template.ServerId);
            var differences = row is null ? [] : CompareWithTemplate(row, template);
            statuses.Add(new GpnDefaultsStatus(
                template.ServerId,
                template.EndpointHost,
                template.EndpointPort,
                template.ServerPublicKey,
                row is not null,
                row is not null && HasUsableKey(row),
                row is not null && differences.Count == 0,
                differences));
        }
        return statuses;
    }

    /// <summary>
    /// "Varsayılanları geri yükle": gömülü anahtarsız şablonların alanlarını
    /// (sunucu genel anahtarı, adres, MTU, DNS, keepalive) mevcut kayıtlara
    /// yeniden uygular. İstemci özel anahtarı DPAPI'li blob, ad ve etkinlik
    /// bayrağı KORUNUR; kayıt yoksa atlanır (anahtarsız şablon tek başına
    /// sunucu üretemez — kullanıcı Sunucu Yönetimi'nden içe aktarmalı).
    /// Idempotent: ikinci çağrı 0 döndürür.
    /// </summary>
    public static async Task<GpnDefaultsRestoreResult> RestoreDefaultsAsync(
        CancellationToken cancellationToken = default)
    {
        var templates = GetDefaultTemplates();
        var rows = await GetItemsAsync(cancellationToken).ConfigureAwait(false) ?? [];
        var restored = 0;
        var statuses = new List<GpnDefaultsStatus>(templates.Count);

        foreach (var template in templates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows.FirstOrDefault(r => r.ServerId == template.ServerId);
            if (row is null)
            {
                statuses.Add(new GpnDefaultsStatus(template.ServerId, template.EndpointHost, template.EndpointPort,
                    template.ServerPublicKey, false, false, false, []));
                continue;
            }

            if (CompareWithTemplate(row, template).Count > 0)
            {
                row.ServerPublicKey = template.ServerPublicKey ?? row.ServerPublicKey;
                row.ClientAddress = template.ClientAddress ?? row.ClientAddress;
                row.Mtu = template.Mtu ?? row.Mtu;
                row.Dns = template.Dns ?? row.Dns;
                row.Keepalive = template.PersistentKeepalive ?? row.Keepalive;
                row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try
                {
                    await SQLiteHelper.Instance.ReplaceAsync(row).ConfigureAwait(false);
                    restored++;
                }
                catch (Exception ex)
                {
                    Logging.SaveLog($"[{Tag}] Varsayılan geri yükleme hatası ({row.ServerId}): {ex.Message}");
                }
            }

            var after = CompareWithTemplate(row, template);
            statuses.Add(new GpnDefaultsStatus(template.ServerId, template.EndpointHost, template.EndpointPort,
                template.ServerPublicKey, true,
                HasUsableKey(row),
                after.Count == 0,
                after));
        }

        return new GpnDefaultsRestoreResult(restored, statuses);
    }

    /// <summary>Satırın şablondan farklı alan adlarını döndürür (şablon alanı boşsa karşılaştırılmaz).</summary>
    private static List<string> CompareWithTemplate(GpnServerItem row, GpnDefaultServerTemplate template)
    {
        var diffs = new List<string>();
        if (template.ServerPublicKey.IsNotEmpty() && row.ServerPublicKey != template.ServerPublicKey)
        {
            diffs.Add(nameof(GpnServerItem.ServerPublicKey));
        }
        if (template.ClientAddress.IsNotEmpty() && row.ClientAddress != template.ClientAddress)
        {
            diffs.Add(nameof(GpnServerItem.ClientAddress));
        }
        if (template.Mtu is { } mtu && row.Mtu != mtu)
        {
            diffs.Add(nameof(GpnServerItem.Mtu));
        }
        if (template.Dns.IsNotEmpty() && row.Dns != template.Dns)
        {
            diffs.Add(nameof(GpnServerItem.Dns));
        }
        if (template.PersistentKeepalive is { } keepalive && row.Keepalive != keepalive)
        {
            diffs.Add(nameof(GpnServerItem.Keepalive));
        }
        return diffs;
    }

    /// <summary>DPAPI'li anahtarın çözülebilir olup olmadığı (bozuk/başka kullanıcı → false).</summary>
    private static bool HasUsableKey(GpnServerItem row)
        => !row.ClientPrivateKeyEnc.IsNullOrEmpty()
           && DpapiCryptor.Decrypt(row.ClientPrivateKeyEnc).IsNotEmpty();

    private static string? MatchValue(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int? MatchInt(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static async Task<int> PersistAsync(GpnServerItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // ── Rozet rotasyonu ─────────────────────────────────────────────
            // Satır her zaman uç noktadan türetilen kanonik ServerId altında
            // yaşar (eski içe aktarımlarda IndexId'den gelen özel ServerId'ler
            // burada kanonik forma çekilir). Aynı uç noktaya farklı sunucu
            // genel / istemci özel anahtarı geldiğinde:
            //   1) eski rozet silinir — aynı uç noktanın farklı ServerId'li
            //      kayıtları kaldırılır (tek endpoint = tek satır), ve
            //   2) kanonik satır yeni anahtarlarla güncellenir (ReplaceAsync).
            var stableId = StableServerId(item.EndpointHost, item.EndpointPort);
            if (item.ServerId != stableId)
            {
                item.ServerId = stableId;
            }

            var existing = await SQLiteHelper.Instance.TableAsync<GpnServerItem>()
                .FirstOrDefaultAsync(t => t.ServerId == stableId).ConfigureAwait(false);
            if (existing is not null)
            {
                // DPAPI çıktısı her çağrıda farklıdır (rastgele tuz) — karşılaştırma
                // şifresiz anahtarlar üzerinden yapılır, yoksa aynı anahtarın
                // yeniden içe aktarımı bile "rotasyon" görünürdü.
                var existingPriv = DpapiCryptor.Decrypt(existing.ClientPrivateKeyEnc);
                var incomingPriv = DpapiCryptor.Decrypt(item.ClientPrivateKeyEnc);
                if (existing.ServerPublicKey != item.ServerPublicKey || existingPriv != incomingPriv)
                {
                    Logging.SaveLog($"[{Tag}] Rozet rotasyonu: {stableId} anahtarları değişti, kayıt güncelleniyor.");
                }
            }

            await RemoveSameEndpointOtherIdsAsync(item.EndpointHost, item.EndpointPort, stableId, cancellationToken)
                .ConfigureAwait(false);

            await SQLiteHelper.Instance.ReplaceAsync(item).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] gpn_servers yazılamadı: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Aynı uç nokta (host:port) altındaki kanonik olmayan eski satırları siler
    /// (örn. eski içe aktarımlardan kalan IndexId tabanlı ServerId'ler). Böylece
    /// anahtar rotasyonunda uç nokta başına tek kayıt kalır.
    /// </summary>
    private static async Task RemoveSameEndpointOtherIdsAsync(
        string host, int port, string keepServerId, CancellationToken cancellationToken)
    {
        List<GpnServerItem>? rows;
        try
        {
            rows = await SQLiteHelper.Instance.TableAsync<GpnServerItem>().ToListAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Rozet temizliği okuma hatası: {ex.Message}");
            return;
        }

        foreach (var row in rows ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.ServerId != keepServerId
                && row.EndpointPort == port
                && string.Equals(row.EndpointHost, host, StringComparison.OrdinalIgnoreCase))
            {
                Logging.SaveLog($"[{Tag}] Rozet rotasyonu: eski kayıt silindi {row.ServerId} ({host}:{port}).");
                await SQLiteHelper.Instance.DeleteAsync(row).ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> TableCountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await SQLiteHelper.Instance.TableAsync<GpnServerItem>().ToListAsync();
            return rows?.Count ?? 0;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] gpn_servers sayım hatası: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// İlk kurulum tohumlaması — İSTEMCİ ÖZEL ANAHTARLARI SÜRÜM KONTROLÜNE
    /// YAZILMAZ; gömülü şablonlar anahtarsızdır ve anahtar dışarıdan gelir:
    ///
    ///   1. Kullanıcının sakladığı WireGuard profilleri (geriye dönük uyum).
    ///   2. env: GPN_ITALY_CONF_B64 / GPN_GERMANY_CONF_B64 — TAM .conf (base64,
    ///      CI secret biçimi); anahtarı conf içinde taşır, doğrudan içe aktarılır.
    ///   3. Gömülü ANAHTARSIZ şablonlar + harici anahtar (env GPN_*_PRIVATE_KEY
    ///      veya ayar GuiItem.GpnSeed*PrivateKey) — anahtar şablona enjekte edilir.
    ///
    /// Anahtar sağlanmayan şablon ASLA tohumlanmaz (anahtarsız profil yararsızdır).
    /// Tüm yollar idempotent'tir (ServerId uç noktadan türetilir → ReplaceAsync
    /// çoğaltmaz); anahtar UpsertFromProfileAsync'te DPAPI ile şifrelenir.
    /// </summary>
    private static async Task SeedAsync(CancellationToken cancellationToken)
    {
        await SeedFromProfileItemsAsync(cancellationToken).ConfigureAwait(false);
        if (await TableCountAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return;
        }

        await SeedFromEnvironmentConfsAsync(cancellationToken).ConfigureAwait(false);
        if (await TableCountAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return;
        }

        await SeedFromTemplatesWithExternalKeysAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// GPN_ITALY_CONF_B64 / GPN_GERMANY_CONF_B64 (tam .conf, base64 — CI secret
    /// biçimi) tohumlaması. Anahtarlar conf içinde gelir; diske DPAPI ile yazılır.
    /// </summary>
    internal static async Task SeedFromEnvironmentConfsAsync(CancellationToken cancellationToken)
    {
        foreach (var envVar in new[] { "GPN_ITALY_CONF_B64", "GPN_GERMANY_CONF_B64" })
        {
            cancellationToken.ThrowIfCancellationRequested();

            var b64 = Environment.GetEnvironmentVariable(envVar);
            if (b64.IsNullOrEmpty())
            {
                continue;
            }

            string confText;
            try
            {
                confText = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim()));
            }
            catch (FormatException ex)
            {
                Logging.SaveLog($"[{Tag}] {envVar} geçersiz base64: {ex.Message}");
                continue;
            }

            await SeedFromConfTextAsync(confText, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gömülü ANAHTARSIZ İtalya/Almanya şablonlarını harici anahtarlarla tohumlar:
    /// uç noktası (host:port) eşleşen anahtar (env GPN_*_PRIVATE_KEY veya ayar
    /// GuiItem.GpnSeed*PrivateKey — env önceliklidir) şablona enjekte edilir ve
    /// içe aktarılır. Anahtarı olmayan şablon atlanır (boş katalog kalır; kullanıcı
    /// Sunucu Yönetimi'nden içe aktarabilir).
    /// </summary>
    internal static async Task SeedFromTemplatesWithExternalKeysAsync(CancellationToken cancellationToken)
    {
        var keys = CollectExternalClientKeys();

        foreach (var resource in new[] { Global.GpnSampleItalyConf, Global.GpnSampleGermanyConf })
        {
            cancellationToken.ThrowIfCancellationRequested();

            var confText = EmbedUtils.GetEmbedText(resource);
            if (confText.IsNullOrEmpty())
            {
                continue;
            }

            var endpoint = ExtractEndpointFromConf(confText);
            if (endpoint is null || !keys.TryGetValue(endpoint, out var privateKey))
            {
                Logging.SaveLog($"[{Tag}] {resource} şablonu için harici anahtar yok — tohumlama atlandı " +
                                "(env GPN_*_PRIVATE_KEY, ayar GpnSeed*PrivateKey veya Sunucu Yönetimi içe aktarımı kullanın).");
                continue;
            }

            var merged = InjectPrivateKey(confText, privateKey);
            if (merged is null)
            {
                continue;
            }

            await SeedFromConfTextAsync(merged, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Harici istemci anahtarı haritası: uç nokta (host:port) → base64 özel anahtar.
    /// Öncelik: ayar (GuiItem.GpnSeed*PrivateKey) &lt; env (GPN_*_PRIVATE_KEY) — env
    /// daha açık/nakil odaklıdır ve ayarı ezer.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> CollectExternalClientKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var gui = AppManager.Instance.Config?.GuiItem;
        AddExternalKey(keys, "92.4.220.236:51820", gui?.GpnSeedItalyPrivateKey);
        AddExternalKey(keys, "130.61.223.36:51820", gui?.GpnSeedGermanyPrivateKey);
        AddExternalKey(keys, "92.4.220.236:51820", Environment.GetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY"));
        AddExternalKey(keys, "130.61.223.36:51820", Environment.GetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY"));

        return keys;
    }

    private static void AddExternalKey(Dictionary<string, string> keys, string endpoint, string? base64Key)
    {
        if (base64Key.IsNullOrEmpty())
        {
            return;
        }

        // Geçerli base64 değilse atla — bozuk anahtar şablona enjekte edilmez.
        try
        {
            _ = Convert.FromBase64String(base64Key.Trim());
        }
        catch (FormatException)
        {
            return;
        }

        keys[endpoint] = base64Key.Trim();
    }

    /// <summary>conf metnindeki Endpoint satırından host:port çıkarır (şablon eşleştirmesi).</summary>
    internal static string? ExtractEndpointFromConf(string confText)
    {
        var match = Regex.Match(confText, @"(?m)^\s*Endpoint\s*=\s*([^:\s]+):(\d+)\s*$");
        return match.Success ? $"{match.Groups[1].Value}:{match.Groups[2].Value}" : null;
    }

    /// <summary>
    /// conf metnine istemci özel anahtarını enjekte eder: PrivateKey satırı varsa
    /// değerini değiştirir, yoksa [Interface]'in hemen altına ekler.
    /// </summary>
    internal static string? InjectPrivateKey(string confText, string privateKeyBase64)
    {
        var key = privateKeyBase64?.Trim();
        if (confText.IsNullOrEmpty() || key.IsNullOrEmpty())
        {
            return null;
        }

        if (Regex.IsMatch(confText, @"(?m)^\s*PrivateKey\s*="))
        {
            return Regex.Replace(confText, @"(?m)^(\s*PrivateKey\s*=\s*)[^\r\n]*", "${1}" + key);
        }

        var interfaceMatch = Regex.Match(confText, @"(?m)^\s*\[Interface\]\s*$");
        if (!interfaceMatch.Success)
        {
            return null;
        }

        return confText.Insert(interfaceMatch.Index + interfaceMatch.Length, $"\nPrivateKey = {key}");
    }

    /// <summary>conf metnini ayrıştırıp her WireGuard peer'ını gpn_servers'a yazar (DPAPI).</summary>
    private static async Task SeedFromConfTextAsync(string confText, CancellationToken cancellationToken)
    {
        var peers = WireguardFmt.ResolveConfig(confText);
        if (peers is null)
        {
            return;
        }

        foreach (var peer in peers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (peer.ConfigType != EConfigType.WireGuard)
            {
                continue;
            }
            await UpsertFromProfileAsync(peer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Mevcut saklı WireGuard ProfileItem'larını gpn_servers'a tohumla (idempotent).</summary>
    private static async Task SeedFromProfileItemsAsync(CancellationToken cancellationToken)
    {
        List<ProfileItem>? profiles;
        try
        {
            profiles = await AppManager.Instance.ProfileItems("").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[{Tag}] Profil okunamadı (tohumlama atlandı): {ex.Message}");
            return;
        }

        var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in profiles ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ConfigType != EConfigType.WireGuard)
            {
                continue;
            }
            if (!TryMap(item, out var profile))
            {
                continue;
            }
            var id = StableServerId(profile.EndpointHost, profile.EndpointPort);
            if (!seeded.Add(id))
            {
                continue;
            }

            var gpnItem = ToItem(profile);
            gpnItem.SourceProfileId = item.IndexId;
            await PersistAsync(gpnItem, cancellationToken).ConfigureAwait(false);
        }
    }
}