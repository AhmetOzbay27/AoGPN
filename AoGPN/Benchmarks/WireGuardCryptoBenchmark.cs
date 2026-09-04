using BenchmarkDotNet.Attributes;
using ServiceLib.Services;

namespace Benchmarks;

/// <summary>
/// Tier 4 veri düzlemi kripto sıcak yolu (hot path) ölçümü.
///
/// Tahsisat bütçesi: Tier 4'ten önce paket başına ~5 ayrı tahsisat vardı
/// (nonce, AEAD anahtar tutamacı, padding ara kopyası, yakalama kopyası +
/// çıktı). Şimdi geriye yalnızca dönüş sözleşmesinin zorunlu kıldığı çıktı
/// tamponları kalmalıdır:
///   Encrypt → 1  (wire tamponu: 16 + pad + 16)
///   Decrypt → 1-2 (plaintext tamponu + pad kırpma kopyası)
///
/// Çalıştırma (BenchmarkDotNet Debug modu reddeder — Release zorunlu):
///   dotnet run -c Release --project Benchmarks
/// </summary>
[MemoryDiagnoser]
// Sabit ölçüm yapılandırması: otomatik ayar (InvocationCount=1) bu kripto
// hattında dağılımı bozuyordu; 200k çağrı/iterasyon ile kararlı ortalamalar.
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10, invocationCount: 200_000)]
public class WireGuardCryptoBenchmark
{
    // Gerçek çift yönlü ilişki: istemci (seed) local=Client, remote=Server;
    // sunucu (Decrypt oturumu) local=Server, remote=Client, recv=istemcinin send'i.
    private const uint ClientIndex = 0x11111111;
    private const uint ServerIndex = 0x22222222;

    private byte[] _sendKey = [];
    private byte[] _recvKey = [];
    private byte[] _packet = [];
    private byte[] _wire = [];
    private WireGuardSession? _session;

    /// <summary>Küçük paket (oyun ACK/status) ve MTU'ya yakın büyük paket.</summary>
    [Params(64, 1400)]
    public int PacketSize { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sendKey = new byte[32];
        Random.Shared.NextBytes(_sendKey);
        _recvKey = new byte[32];
        Random.Shared.NextBytes(_recvKey);

        _packet = new byte[PacketSize];
        Random.Shared.NextBytes(_packet);

        // Wire örneğini gerçek Encrypt ile üret — aynı anahtarlar + counter 0,
        // taze bir oturum için her zaman geçerlidir.
        using var seed = new WireGuardSession(_sendKey, _recvKey, ClientIndex, ServerIndex, true);
        _wire = seed.Encrypt(_packet)!;
    }

    /// <summary>
    /// Her iterasyon öncesi taze oturum: counter/replay durumu sıfırlanır, böylece
    /// Decrypt gerçek başarı yolunu (AEAD + replay + pad kırpma) ölçer — aynı wire'ı
    /// tekrarlı çözmek replay filtresine takılıp kırpma yolunu ölçemezdi. Oturum
    /// kurulumu sıcak yola dahil DEĞİLDİR (gerçekte oturum başına bir kez olur;
    /// AEAD örnekleri de tembelce bir kez kurulur).
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        // Sunucu tarafı oturum: wire'ın receiver alanı ServerIndex olduğu için
        // local=ServerIndex; recv anahtarı istemcinin send anahtarıdır. Index
        // uyuşmazlığı Decrypt'i erken red yoluna (17 ns, 0 B) düşürürdü — ölçüm
        // gerçek AEAD + replay + pad kırpma yolunu değil, reddi ölçerdi.
        _session = new WireGuardSession(_recvKey, _sendKey, ServerIndex, ClientIndex, false);
    }

    [Benchmark]
    public byte[]? Encrypt() => _session!.Encrypt(_packet);

    [Benchmark]
    public byte[]? Decrypt() => _session!.Decrypt(_wire);
}