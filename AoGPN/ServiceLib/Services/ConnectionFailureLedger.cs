using ServiceLib.Models;

namespace ServiceLib.Services;

/// <summary>
/// Bağlantı kurulamayınca dashboard hata kartına taşınan başarısızlık defteri.
/// Üç kaynağı saklar: ana çekirdeğin son <see cref="CoreHealthSnapshot"/> Failed
/// durumu (kullanıcı dostu Error), son <see cref="CoreStartupDiagnostic"/> (teknik
/// ayrıntı / kod / elevation / port) ve GPN koordinatörünün son Failed anlık
/// görüntüsü (<see cref="GpnConnectionSnapshot"/> — seçim/launcher hatası, çekirdek
/// hiç başlamamış olabilir).
///
/// WPF'e bağımlı değildir: DOM kanalı, WebView hazırlığı ve efektif bağlantı durumu
/// ctor-enjekte delege'lerdir (TrayWindowCoordinator deseni). Karar kuralı
/// (<see cref="Decide"/>) saf ve testlidir: 45 sn tazelik penceresi, GPN önceliği,
/// elevation/canRecover/port türetimi.
/// </summary>
public sealed class ConnectionFailureLedger
{
    /// <summary>Kartı gösterilebilir kılan tazelik penceresi (sn) — bu süre geçen kayıt bayat sayılır.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions CardJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Func<string, Task> _executeScript;
    private readonly Func<bool> _isWebViewReady;
    private readonly Func<bool> _readActualConnectionState;
    private readonly Func<DateTimeOffset> _clock;

    private CoreHealthSnapshot? _lastMainCoreFailure;
    private CoreStartupDiagnostic? _lastMainCoreDiagnostic;
    private GpnConnectionSnapshot? _lastGpnFailedSnapshot;

    public ConnectionFailureLedger(
        Func<string, Task> executeScript,
        Func<bool> isWebViewReady,
        Func<bool> readActualConnectionState,
        Func<DateTimeOffset>? clock = null)
    {
        _executeScript = executeScript ?? throw new ArgumentNullException(nameof(executeScript));
        _isWebViewReady = isWebViewReady ?? throw new ArgumentNullException(nameof(isWebViewReady));
        _readActualConnectionState = readActualConnectionState ?? throw new ArgumentNullException(nameof(readActualConnectionState));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Ana çekirdek sağlık olayı: Failed saklanır, Ready/Stopped temizler. Yalnızca Main rolü.</summary>
    public void RecordCoreHealth(CoreHealthSnapshot health)
    {
        if (health.Role != CoreHealthRole.Main)
        {
            return;
        }
        if (health.State == CoreHealthState.Failed)
        {
            _lastMainCoreFailure = health;
        }
        else if (health.State is CoreHealthState.Ready or CoreHealthState.Stopped)
        {
            _lastMainCoreFailure = null;
        }
    }

    /// <summary>Ana çekirdek başlatma teşhisini saklar (teknik ayrıntı / kod). Yalnızca Main rolü.</summary>
    public void RecordDiagnostic(CoreStartupDiagnostic diag)
    {
        if (diag.Role == CoreHealthRole.Main)
        {
            _lastMainCoreDiagnostic = diag;
        }
    }

    /// <summary>
    /// GPN koordinatör anlık görüntüsü: Connecting/Connected eski başarısızlığı
    /// temizler (yeni deneme/hazır tünel — eski hata kartına taşınmasın); Failed
    /// kaydedilir.
    /// </summary>
    public void RecordGpnSnapshot(GpnConnectionSnapshot snapshot)
    {
        switch (snapshot.State)
        {
            case GpnConnectionState.Connecting:
            case GpnConnectionState.Connected:
                _lastGpnFailedSnapshot = null;
                break;
            case GpnConnectionState.Failed:
                _lastGpnFailedSnapshot = snapshot;
                break;
        }
    }

    /// <summary>Bağlantı widget'ında kalıcı olmayan bir hata gösterir (yalnızca mesaj).</summary>
    public Task PushErrorAsync(string message)
        => _executeScript($"window.setConnectionError({JsonSerializer.Serialize(message)});");

    /// <summary>
    /// Bağlanma denemesi başarısız olduysa dashboard'a nedeni gösteren hata kartını
    /// basar. Yalnızca bağlantı kurma akışlarının sonunda çağrılır; normal disconnect
    /// ve kurulmuş bağlantı no-op'tur.
    /// </summary>
    public async Task TryPushAsync()
    {
        if (!_isWebViewReady() || _readActualConnectionState())
        {
            return; // bağlantı kuruldu — hata kartı gösterilmez
        }

        var card = Decide(_lastGpnFailedSnapshot, _lastMainCoreFailure, _lastMainCoreDiagnostic, _clock());
        if (card is null)
        {
            return;
        }

        await ShowAsync(card);
    }

    /// <summary>
    /// Saf karar: saklanan başarısızlıklardan hangi kartın gösterileceğini seçer.
    /// Öncelik GPN koordinatöründedir (seçim/launcher hatası — çekirdek hiç
    /// başlamamış olabilir), ardından ana çekirdek başlatma teşhisi gelir; ikisi de
    /// tazelik penceresinin dışındaysa (45 sn) kart yoktur.
    /// </summary>
    public static ConnectionFailureCard? Decide(
        GpnConnectionSnapshot? gpnFailed,
        CoreHealthSnapshot? coreFailure,
        CoreStartupDiagnostic? coreDiagnostic,
        DateTimeOffset now)
    {
        // 1) GPN koordinatörü yakın zamanda Failed durumuna düştüyse mesajını kullan.
        if (gpnFailed is { State: GpnConnectionState.Failed }
            && !string.IsNullOrEmpty(gpnFailed.Error)
            && now - gpnFailed.UpdatedAt < Freshness)
        {
            return new ConnectionFailureCard(gpnFailed.Error, null, false, false, null);
        }

        // 2) Ana çekirdek başlatma hatası (CoreHealthSnapshot.Error kullanıcı
        //    dostudur; teknik ayrıntı CoreStartupDiagnostic.TechnicalDetails).
        if (coreFailure is not null
            && !string.IsNullOrEmpty(coreFailure.Error)
            && now - coreFailure.ChangedAt < Freshness)
        {
            var diag = coreDiagnostic is { } d && now - d.CreatedAt < Freshness ? d : null;
            return new ConnectionFailureCard(
                coreFailure.Error,
                diag?.TechnicalDetails,
                diag?.Code is CoreStartupErrorCode.ElevationRequired or CoreStartupErrorCode.ElevationFailed,
                diag?.CanRecover == true,
                diag?.Port ?? coreFailure.Port);
        }

        return null;
    }

    /// <summary>Hata kartını dashboard'a basar (window.setConnectionError).</summary>
    private async Task ShowAsync(ConnectionFailureCard card)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                Message = card.Message,
                Details = card.Details,
                Elevation = card.Elevation,
                CanRecover = card.CanRecover,
                Port = card.Port,
            }, CardJsonOptions);
            await _executeScript($"window.setConnectionError({payload});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN connection-failure card push failed", ex);
        }
    }
}

/// <summary>Dashboard hata kartı içeriği (window.setConnectionError yükü).</summary>
public sealed record ConnectionFailureCard(
    string Message,
    string? Details,
    bool Elevation,
    bool CanRecover,
    int? Port);
