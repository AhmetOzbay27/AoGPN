namespace ServiceLib.Services;

// ─────────────────────────────────────────────────────────────────────────
// GpnHairpinDetector — hairpin (öz-erişim / kendi genel IP'si) teşhisi
//
// GpnServerSelectionService'in hairpin katmanı: sunucu uç noktasının makinenin
// KENDİ genel IP'siyle eşleşip eşleşmediğini denetler ve bu sunucuları aday
// sıralamasının sonuna atar / eler (tünel-içi öz-erişim el sıkışması genellikle
// yanıt almaz — NAT hairpin gerektirir). Durum (önbellek) bu sınıfta tutulur;
// karar fonksiyonları saf olduğu için GpnDecision katmanında kalır.
//
// Önbellek davranışı GpnServerSelectionService'ten BİREBİR taşınmıştır:
//  * kendi genel IP'si 60 sn TTL (OwnIpCacheSeconds),
//  * hairpin kimlik kümesi aday-kümesi imzasıyla 60 sn TTL (sıfır küme de saklanır),
//  * HTTP çözücü yalnızca provider yoksa ve önbellek bayatsa çağrılır.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Hairpin (öz-erişim) teşhis motoru: makinenin kendi genel IP'sini çözer ve
/// hedef sunucuların bu IP ile eşleşip eşleşmediğini (hairpin) tespit eder.
/// GpnServerSelectionService ile aynı yaşam döngüsünü paylaşır (singleton);
/// önbelleği sınıf içinde tutar.
/// </summary>
internal sealed class GpnHairpinDetector
{
    private const string Tag = "GpnSelect";
    private const int OwnIpCacheSeconds = 60;

    private readonly Func<string?>? _ownPublicIpProvider;

    // Kendi genel IP'si için kısa zaman aşımlı HTTP çözücü; seçim ağını engellememesi
    // için 60 sn önbelleklenir (object-memcache benzeri). Bağlan akışında bir kez yavas
    // net HTTP çağrısı yeterli — her seçimde hafif tutulur.
    private static readonly HttpClient _ownIpHttp = new() { Timeout = TimeSpan.FromSeconds(3) };
    private string? _cachedOwnIp;
    private DateTime _ownIpUtc;

    // Hairpin kimlikleri önbelleği: aynı aday kümesiyle tekrar çağrıldığında (ör. her
    // failover izleyicisi başlangıcı) kendi-IP TTL'si (OwnIpCacheSeconds) içinde seti
    // YENİDEN hesaplamaz — HTTP çözümü VE candidate taraması önlenir. Sıfır küme de
    // saklanır ("hairpin yok" sonucu da TTL boyunca sabittir). Aday kimliği değişirse
    // (farklı sunucu listesi) anahtar eşleşmediği için otomatik yenilenir.
    private string? _cachedHairpinKey;
    private HashSet<string>? _cachedHairpinIds;
    private DateTime _cachedHairpinUtc;

    public GpnHairpinDetector(Func<string?>? ownPublicIpProvider = null)
    {
        _ownPublicIpProvider = ownPublicIpProvider;
    }

    /// <summary>
    /// Hairpin (öz-erişim): hedef sunucunun genel IP'si makinenin KENDİ genel IP'siyle
    /// eşleşiyorsa, istemci tünelin içinden kendi sunucusuna erişmeye çalışıyor demektir
    /// (tünel-içi hairpin NAT). Böyle bir sunucuya el sıkışma genellikle yanıt almaz;
    /// bu teşhis bağlanmaya çalışmadan önce uyarır ve sunucuyu aday sırasının sonuna atar.
    /// </summary>
    internal static bool IsHairpin(GpnServerProfile server, string? ownPublicIp)
    {
        if (string.IsNullOrWhiteSpace(ownPublicIp)
            || !IPAddress.TryParse(server.EndpointHost, out var endpoint)
            || !IPAddress.TryParse(ownPublicIp.Trim(), out var own))
        {
            return false;
        }
        return endpoint.Equals(own);
    }

    /// <summary>
    /// Makinenin kendi genel IP'sini döndürür (hairpin teşhisi için). Öncelik:
    ///  1. Constructor'a enjekte edilen <c>ownPublicIpProvider</c> (testler sabit döner),
    ///  2. önbellekli HTTP çözücü (api.ipify.org → ifconfig.me → icanhazip), 60 sn TTL.
    /// Hiçbir uç nokta yanıt vermezse null — bu durumda hairpin tespiti sessizce devre dışı
    /// kalır (seçim asla ağ hatasıyla düşmez).
    /// </summary>
    internal async Task<string?> ResolveOwnPublicIpAsync(CancellationToken cancellationToken)
    {
        if (_ownPublicIpProvider is not null)
        {
            return _ownPublicIpProvider();
        }

        var now = DateTime.UtcNow;
        if (_cachedOwnIp is not null && now - _ownIpUtc < TimeSpan.FromSeconds(OwnIpCacheSeconds))
        {
            return _cachedOwnIp;
        }

        foreach (var url in new[] { "https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com" })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(1500);
                var body = (await _ownIpHttp.GetStringAsync(url, cts.Token).ConfigureAwait(false)).Trim();
                if (IPAddress.TryParse(body, out _))
                {
                    _cachedOwnIp = body;
                    _ownIpUtc = DateTime.UtcNow;
                    return body;
                }
            }
            catch (OperationCanceledException)
            {
                // zaman aşımı — sonraki uç noktayı dene
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"[{Tag}] Kendi genel IP çözülemedi ({url}): {ex.Message}");
            }
        }
        Logging.SaveLog($"[{Tag}] Kendi genel IP'si hiçbir uç noktadan çözülemedi "
            + "(ipify→ifconfig.me→icanhazip) — hairpin teşhisi bu turda devre dışı.");
        return null;
    }

    /// <summary>
    /// Tünel kalıntısı olan "kendi genel IP"sini ayıklar: kendi IP'si önceki GPN
    /// oturumunda tünel İÇİNDEN çözülmüşse sunucunun kendi genel IP'si (örn.
    /// 92.4.220.236) önbelleğe yazılır; bağlantı sonrası o sunucu yanlışlıkla
    /// "hairpin" sanılıp atlanır (canlı gözlenen yanlış-seçim kaynağı). Çözülen IP
    /// etkin bir sunucunun uç noktasıyla birebir eşleşiyorsa güvenilir değildir —
    /// null döner, hairpin teşhisi o turda sessizce devre dışı kalır.
    /// </summary>
    internal string? GuardOwnIpArtifact(string? ownIp, IReadOnlyList<GpnServerProfile> enabled)
    {
        if (ownIp is null)
        {
            return null;
        }

        var matchesServerEndpoint = enabled.Any(s =>
            string.Equals(s.EndpointHost, ownIp.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!matchesServerEndpoint)
        {
            return ownIp;
        }

        Logging.SaveLog($"[{Tag}] ownIp={ownIp} bir GPN sunucusunun uç noktasıyla eşleşiyor "
            + "(tünel kalıntısı) — hairpin teşhisi bu turda devre dışı.");
        return null;
    }

    /// <summary>
    /// Verilen adaylar arasındaki hairpin (öz-erişim, kendi genel IP'si) sunucuların
    /// kimliklerini döndürür. failover izleyicisi başlarken bir kez çağrılır.
    ///
    /// Önbellekleme: sonuç (sıfır küme dahil) aday-kümesi imzasıyla birlikte kendi-IP
    /// TTL'si (<see cref="OwnIpCacheSeconds"/> = 60 sn) boyunca saklanır. Aynı sunucu
    /// listesiyle gelen her izleyici başlangıcı önbelleği YENİDEN kullanır — ne HTTP
    /// çözümü ne candidate taraması tekrarlanır. Aday listesi değişirse anahtar değişir ve
    /// otomatik yenilenir.
    ///
    /// Hata davranışı: kendi genel IP'si hiçbir uç noktadan çözülemezse hairpin tespiti
    /// devre dışı kalır — boş küme döner VE bu durum TTL boyunca önbelleklenir (IP
    /// servisine döngüsel yük olmaz). Karar, hairpin-elenmeden normal seçim akışına
    /// döner: kendi IP'sine sahip sunucu aday/sunucu değişim hedefi olarak seçilebilir
    /// (öz-erişim el sıkışması yanıtı donanıma/yönlendiriciye bağlıdır). Açıkça loglanır.
    /// </summary>
    internal async Task<IReadOnlySet<string>> ResolveHairpinServerIdsAsync(
        IReadOnlyList<GpnServerProfile> candidates,
        CancellationToken cancellationToken)
    {
        var key = string.Join(",", candidates.Where(c => c.IsEnabled)
                                              .Select(c => c.ServerId)
                                              .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        if (_cachedHairpinIds is not null
            && _cachedHairpinKey == key
            && DateTime.UtcNow - _cachedHairpinUtc < TimeSpan.FromSeconds(OwnIpCacheSeconds))
        {
            return _cachedHairpinIds;
        }

        var ownIp = await ResolveOwnPublicIpAsync(cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(ownIp))
        {
            Logging.SaveLog($"[{Tag}] Kendi genel IP çözülemedi — hairpin (öz-erişim) tespiti devre dışı. "
                + "Aday/sunucu-değişimine kendi IP'li sunucu girebilir (el sıkışma yanıtı yönlendiriciye bağlıdır).");
            DiagLog.Write($"GPN_FAILOVER ownIp-unresolved → hairpin detection disabled (empty set — cached {OwnIpCacheSeconds}s)");

            // Sıfır küme de saklanır: başarısız çözüm TTL boyunca tekrarlanmaz.
            _cachedHairpinIds = ids;
            _cachedHairpinKey = key;
            _cachedHairpinUtc = DateTime.UtcNow;
            return ids;
        }

        foreach (var c in candidates)
        {
            if (c.IsEnabled && IsHairpin(c, ownIp))
            {
                ids.Add(c.ServerId);
                DiagLog.Write($"GPN_FAILOVER hairpin server={c.ServerId} endpoint={c.EndpointHost} ownIp={ownIp} — non-hairpin varken hedef değildir");
            }
        }

        _cachedHairpinIds = ids;
        _cachedHairpinKey = key;
        _cachedHairpinUtc = DateTime.UtcNow;
        return ids;
    }
}