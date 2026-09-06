using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// ConnectionFailureLedger — dashboard hata kartı defterinin birim testleri.
/// W4-B çıkarımının regresyon ağı: GPN önceliği, 45 sn tazelik penceresi,
/// elevation/canRecover/port türetimi, kayıt temizleme kuralları ve DOM yükünün
/// camelCase sözleşmesi (window.setConnectionError).
/// </summary>
public class ConnectionFailureLedgerTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow;

    private static CoreHealthSnapshot CoreFailure(
        string? error = "core failed",
        DateTimeOffset? at = null,
        int? port = null,
        CoreHealthRole role = CoreHealthRole.Main)
        => new(role, CoreHealthState.Failed, null, port, error, at ?? Base);

    private static CoreStartupDiagnostic Diagnostic(
        CoreStartupErrorCode code = CoreStartupErrorCode.ConfigInvalid,
        string? technical = null,
        int? port = null,
        bool canRecover = false,
        DateTimeOffset? at = null,
        CoreHealthRole role = CoreHealthRole.Main)
        => new(role, null, CoreStartupStage.StartProcess, code, "diag msg", technical, port, canRecover, at ?? Base);

    private static GpnConnectionSnapshot GpnFailed(
        string? error = "gpn failed",
        DateTimeOffset? at = null)
        => new(GpnConnectionState.Failed, ConnectionMode.WireGuardUDP, null, null, at ?? Base, error);

    private static ConnectionFailureLedger CreateLedger(
        List<string> scripts,
        out Action<DateTimeOffset> advanceClock,
        Func<bool>? webViewReady = null,
        Func<bool>? connected = null)
    {
        var now = Base;
        advanceClock = next => now = next;
        return new ConnectionFailureLedger(
            executeScript: script => { scripts.Add(script); return Task.CompletedTask; },
            isWebViewReady: webViewReady ?? (() => true),
            readActualConnectionState: connected ?? (() => false),
            clock: () => now);
    }

    // ── Decide: saf karar ────────────────────────────────────────────────────

    [Fact]
    public void Decide_FreshCoreFailure_ReturnsCardWithError()
    {
        var card = ConnectionFailureLedger.Decide(
            null, CoreFailure(at: Base - TimeSpan.FromSeconds(5)), null, Base);

        Assert.NotNull(card);
        Assert.Equal("core failed", card!.Message);
        Assert.Null(card.Details);
        Assert.False(card.Elevation);
        Assert.False(card.CanRecover);
        Assert.Null(card.Port);
    }

    [Fact]
    public void Decide_GpnFailureTakesPrecedenceOverCoreFailure()
    {
        var card = ConnectionFailureLedger.Decide(
            GpnFailed(at: Base - TimeSpan.FromSeconds(2)),
            CoreFailure(at: Base - TimeSpan.FromSeconds(1)),
            null,
            Base);

        Assert.NotNull(card);
        Assert.Equal("gpn failed", card!.Message);
        Assert.False(card.Elevation);
    }

    [Fact]
    public void Decide_ElevationDiagnostic_CarriesDetailsElevationCanRecoverAndPort()
    {
        var card = ConnectionFailureLedger.Decide(
            null,
            CoreFailure(port: 1, at: Base - TimeSpan.FromSeconds(5)),
            Diagnostic(
                code: CoreStartupErrorCode.ElevationRequired,
                technical: "needs elevation",
                port: 2080,
                canRecover: true,
                at: Base - TimeSpan.FromSeconds(3)),
            Base);

        Assert.NotNull(card);
        Assert.Equal("core failed", card!.Message);
        Assert.Equal("needs elevation", card.Details);
        Assert.True(card.Elevation);
        Assert.True(card.CanRecover);
        Assert.Equal(2080, card.Port);
    }

    [Fact]
    public void Decide_StaleCoreFailureWithStaleDiagnostic_FallsBackToHealthPort()
    {
        // Teşhis 45 sn penceresinin dışında → teknik ayrıntı/elevation düşer,
        // port ana çekirdeğin portuna geri döner.
        var card = ConnectionFailureLedger.Decide(
            null,
            CoreFailure(port: 1080, at: Base - TimeSpan.FromSeconds(5)),
            Diagnostic(code: CoreStartupErrorCode.ElevationFailed, canRecover: true, port: 2080,
                at: Base - TimeSpan.FromSeconds(60)),
            Base);

        Assert.NotNull(card);
        Assert.Null(card!.Details);
        Assert.False(card.Elevation);
        Assert.False(card.CanRecover);
        Assert.Equal(1080, card.Port);
    }

    [Fact]
    public void Decide_StaleGpnFailure_FallsThroughToCoreFailure()
    {
        var card = ConnectionFailureLedger.Decide(
            GpnFailed(at: Base - TimeSpan.FromSeconds(60)),
            CoreFailure(at: Base - TimeSpan.FromSeconds(2)),
            null,
            Base);

        Assert.NotNull(card);
        Assert.Equal("core failed", card!.Message);
    }

    [Fact]
    public void Decide_AllStale_ReturnsNull()
    {
        var card = ConnectionFailureLedger.Decide(
            GpnFailed(at: Base - TimeSpan.FromSeconds(60)),
            CoreFailure(at: Base - TimeSpan.FromSeconds(60)),
            null,
            Base);

        Assert.Null(card);
    }

    [Fact]
    public void Decide_EmptyError_ReturnsNull()
    {
        Assert.Null(ConnectionFailureLedger.Decide(
            GpnFailed(error: "", at: Base), null, null, Base));
        Assert.Null(ConnectionFailureLedger.Decide(
            null, CoreFailure(error: null, at: Base), null, Base));
    }

    // ── Kayıt kuralları ──────────────────────────────────────────────────────

    [Fact]
    public async Task RecordCoreHealth_ReadyOrStopped_ClearsFailure()
    {
        var scripts = new List<string>();
        var ledger = CreateLedger(scripts, out _);

        ledger.RecordCoreHealth(CoreFailure(at: Base - TimeSpan.FromSeconds(2)));
        ledger.RecordCoreHealth(new CoreHealthSnapshot(
            CoreHealthRole.Main, CoreHealthState.Ready, null, null));

        scripts.Clear();
        await ledger.TryPushAsync();
        Assert.Empty(scripts);
    }

    [Fact]
    public async Task RecordCoreHealth_NonMainRole_IsIgnored()
    {
        var scripts = new List<string>();
        var ledger = CreateLedger(scripts, out _);

        ledger.RecordCoreHealth(CoreFailure(role: CoreHealthRole.PreSocks));
        scripts.Clear();
        await ledger.TryPushAsync();
        Assert.Empty(scripts);
    }

    [Fact]
    public async Task RecordGpnSnapshot_ConnectingAfterFailure_ClearsGpnCard()
    {
        var scripts = new List<string>();
        var ledger = CreateLedger(scripts, out _);

        ledger.RecordGpnSnapshot(GpnFailed(at: Base));
        ledger.RecordGpnSnapshot(new GpnConnectionSnapshot(
            GpnConnectionState.Connecting, ConnectionMode.WireGuardUDP, null, null, Base));

        scripts.Clear();
        await ledger.TryPushAsync();
        Assert.Empty(scripts);
    }

    [Fact]
    public async Task RecordGpnSnapshot_Connected_ClearsGpnCard()
    {
        var scripts = new List<string>();
        var ledger = CreateLedger(scripts, out _);

        ledger.RecordGpnSnapshot(GpnFailed(at: Base));
        ledger.RecordGpnSnapshot(new GpnConnectionSnapshot(
            GpnConnectionState.Connected, ConnectionMode.WireGuardUDP, null, null, Base));

        scripts.Clear();
        await ledger.TryPushAsync();
        Assert.Empty(scripts);
    }

    // ── Uçtan uca TryPushAsync ───────────────────────────────────────────────

    [Fact]
    public async Task TryPush_Connected_DoesNotPush()
    {
        var scripts = new List<string>();
        var ledger = CreateLedger(scripts, out _, connected: () => true);
        ledger.RecordCoreHealth(CoreFailure(at: Base));

        await ledger.TryPushAsync();
        Assert.Empty(scripts);
    }

    [Fact]
    public async Task TryPush_WebViewNotReady_DoesNotPush()
    {
        var scripts = new List<string>();
        var ledger = CreateLedger(scripts, out _, webViewReady: () => false);
        ledger.RecordCoreHealth(CoreFailure(at: Base));

        await ledger.TryPushAsync();
        Assert.Empty(scripts);
    }

    [Fact]
    public async Task TryPush_ElevationCard_EmitsCamelCaseSetConnectionError()
    {
        var scripts = new List<string>();
        var ledger = CreateLedger(scripts, out _);
        ledger.RecordCoreHealth(CoreFailure(port: 1, at: Base - TimeSpan.FromSeconds(2)));
        ledger.RecordDiagnostic(Diagnostic(
            code: CoreStartupErrorCode.ElevationRequired,
            technical: "tun needs elevation",
            port: 2080,
            canRecover: true,
            at: Base - TimeSpan.FromSeconds(1)));

        await ledger.TryPushAsync();

        var script = Assert.Single(scripts);
        Assert.StartsWith("window.setConnectionError(", script);
        Assert.Contains("\"message\":\"core failed\"", script);
        Assert.Contains("\"details\":\"tun needs elevation\"", script);
        Assert.Contains("\"elevation\":true", script);
        Assert.Contains("\"canRecover\":true", script);
        Assert.Contains("\"port\":2080", script);
    }

    [Fact]
    public async Task TryPush_ClockPastFreshness_SkipsCard()
    {
        var scripts = new List<string>();
        var ledger = CreateLedger(scripts, out var advanceClock);
        ledger.RecordCoreHealth(CoreFailure(at: Base));

        // Kartı gösterilebilir kılan pencereyi (45 sn) aş: kayıt bayat sayılır.
        advanceClock(Base + TimeSpan.FromSeconds(60));

        await ledger.TryPushAsync();
        Assert.Empty(scripts);
    }

    [Fact]
    public async Task PushError_EmitsPlainMessage()
    {
        var scripts = new List<string>();
        var ledger = CreateLedger(scripts, out _);

        await ledger.PushErrorAsync("tun needs admin");

        var script = Assert.Single(scripts);
        Assert.Equal("window.setConnectionError(\"tun needs admin\");", script);
    }
}
