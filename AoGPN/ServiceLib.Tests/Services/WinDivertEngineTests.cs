using System.Runtime.InteropServices;
using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// WinDivert engine testleri — gerçek sürücüye DOKUNMAZ (makinede kurulu
/// olmayabilir). Natif katman <see cref="FakeWinDivertApi"/> ile sahteleştirilir;
/// yalnızca filtre dizgesi, handle yaşam döngüsü ve yakalama-kuyruk döngüsü
/// doğrulanır.
/// </summary>
public class WinDivertEngineTests
{
    // ── Filtre dizgesi üreticisi (pure) ───────────────────────────────────

    [Fact]
    public void BuildFilter_Default_OutboundUdpIpv4_NoPidClause()
    {
        var filter = WinDivertFilterBuilder.BuildFilter();

        filter.Should().Contain("outbound");
        filter.Should().Contain("udp");
        filter.Should().Contain("(ip)");
        // NETWORK katmanı processId tanımaz (gerçek WinDivert 2.2.2 bu sözcükle
        // hata 87 döndürür — saha probe ile doğrulandı). Süreç filtresi asla
        // dizgede OLMAMALI; PID ayrımı kullanıcı modunda port→PID tablosuyla yapılır.
        filter.Should().NotContain("processId");
        // Varsayılan IPv4 — yanlış alan adları da olmamalı.
        filter.Should().NotContain("ip6");
        filter.Should().NotContain("ipv6");
        // Loopback dışlama (127.0.0.0/8) her zaman eklenir — WinDivert loopback
        // UDP'yi yakalar ve geri enjeksiyon yerel döngüyü tamamlayamaz (canlı
        // doğrulama: bağlıyken 127.0.0.1 echo timeout). CIDR/`not` gramerde yok,
        // aralık (>,<) biçimi probe ile derleme doğrulandı.
        filter.Should().Contain("ip.DstAddr < 127.0.0.0");
        filter.Should().Contain("ip.DstAddr > 127.255.255.255");
        filter.Should().NotContain("127.0.0.0/8");
    }

    [Fact]
    public void BuildFilter_Ipv4AndIpv6_UsesOr()
    {
        var filter = WinDivertFilterBuilder.BuildFilter(ipv4: true, ipv6: true);

        filter.Should().Contain("(ip or ipv6)");
        filter.Should().NotContain("ip6"); // resmî alan adı ipv6 — ip6 hata 87 üretir
        // Çift aile: loopback dışlama her aile için AYRI cümlelerle (or) eklenir —
        // tek cümle yanlış ailede tüm paketleri filtre dışı bırakırdı.
        filter.Should().Contain("(ip and (ip.DstAddr < 127.0.0.0 or ip.DstAddr > 127.255.255.255))");
        filter.Should().Contain("(ipv6 and (ipv6.DstAddr != ::1))");
    }

    [Fact]
    public void BuildFilter_Ipv6Only_UsesIpv6()
    {
        var filter = WinDivertFilterBuilder.BuildFilter(ipv4: false, ipv6: true);

        filter.Should().Contain("(ipv6)");
        filter.Should().NotContain("(ip)");
        filter.Should().NotContain("ip6");
        filter.Should().Contain("ipv6.DstAddr != ::1"); // IPv6 loopback dışlama
    }

    [Fact]
    public void BuildFilter_ExcludedTunnelEndpoint_UsesNotEqualsDeMorgan()
    {
        var filter = WinDivertFilterBuilder.BuildFilter(
            excludedDstHost: "92.4.220.236", excludedDstPort: 51820);

        // WinDivert gramerinde mantıksal DEĞİL (!) YOKTUR — dışlama != ile De Morgan
        // biçiminde yazılır (probe: !(...) hata 87, != biçimi derlenir).
        filter.Should().NotContain("!(");
        filter.Should().Contain("(ip.DstAddr != 92.4.220.236 or udp.DstPort != 51820)");
        filter.Should().NotContain("processId");
    }

    [Fact]
    public void BuildFilter_InvalidExclusion_HasNoClause()
    {
        var filter = WinDivertFilterBuilder.BuildFilter(
            excludedDstHost: "92.4.220.236", excludedDstPort: 0);

        // Geçersiz port → uç nokta dışlaması EKLENMEZ (loopback dışlaması kalır).
        filter.Should().NotContain("92.4.220.236");
        filter.Should().Contain("ip.DstAddr < 127.0.0.0");
    }

    [Fact]
    public void BuildFilter_NoIPVersion_Throws()
    {
        var act = () => WinDivertFilterBuilder.BuildFilter(ipv4: false, ipv6: false);
        act.Should().Throw<ArgumentException>();
    }

    // ── Struct layout ─────────────────────────────────────────────────────

    [Fact]
    public void WinDivertAddress_Size_MatchesNativeLayout()
    {
        // Natif WINDIVERT_ADDRESS (WinDivert 2.x): 8 (Timestamp) + 4 (bitfield)
        // + 4 (Reserved2) + 64 (union) = 80 bayt — x86 ve x64'te aynı.
        WinDivertAddress.Size.Should().Be(80);
    }

    // ── Engine yaşam döngüsü (sahte API) ─────────────────────────────────

    [Fact]
    public void Open_CallsApi_AndReportsOpen()
    {
        var fake = new FakeWinDivertApi();
        using var engine = new WinDivertEngine(fake);

        engine.Open(WinDivertFilterBuilder.BuildFilter());

        fake.OpenCalls.Should().Be(1);
        engine.IsOpen.Should().BeTrue();
        engine.Filter.Should().Contain("outbound and udp");
    }

    [Fact]
    public void OpenEx_CallsApi_WithOpenParams_AndFlags()
    {
        var fake = new FakeWinDivertApi();
        using var engine = new WinDivertEngine(fake);

        var ps = WinDivertOpenParams.Default;
        ps.CurrentLayer = 1;
        ps.QueueTime0 = 100; // indeks 0 = NETWORK + inbound

        engine.OpenEx("outbound and udp and (processId == 9)", ps, flags: WinDivertNative.FlagQueueTime);

        fake.OpenExCalls.Should().Be(1);
        fake.OpenCalls.Should().Be(0);
        fake.LastOpenParams.Version.Should().Be(WinDivertNative.OpenParamsVersion0);
        fake.LastOpenParams.CurrentLayer.Should().Be(1);
        fake.LastOpenParams.QueueTime0.Should().Be(100);
        engine.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void OpenEx_DriverMissing_ThrowsWinDivertException()
    {
        var fake = new FakeWinDivertApi(openResult: IntPtr.Zero, lastError: 2 /* ERROR_FILE_NOT_FOUND */);
        using var engine = new WinDivertEngine(fake);

        var act = () => engine.OpenEx("outbound and udp", WinDivertOpenParams.Default);

        var ex = act.Should().Throw<WinDivertException>().Which;
        ex.NativeError.Should().Be(2);
        ex.Message.Should().Contain("WinDivert.dll bulunamadı");
    }

    [Fact]
    public void OpenSniff_CallsOpenEx_WithSniffFlag()
    {
        var fake = new FakeWinDivertApi();
        using var engine = new WinDivertEngine(fake);

        engine.OpenSniff(WinDivertFilterBuilder.BuildFilter(), WinDivertOpenParams.Default);

        fake.OpenExCalls.Should().Be(1);
        fake.OpenCalls.Should().Be(0);
        (fake.LastOpenFlags & WinDivertNative.FlagSniff).Should().NotBe(0ul);
        (fake.LastOpenFlags & WinDivertNative.FlagRecvOnly).Should().Be(0ul);
        engine.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void OpenRecvOnly_CallsOpenEx_WithRecvOnlyFlag()
    {
        var fake = new FakeWinDivertApi();
        using var engine = new WinDivertEngine(fake);

        engine.OpenRecvOnly(WinDivertFilterBuilder.BuildFilter(), WinDivertOpenParams.Default);

        fake.OpenExCalls.Should().Be(1);
        fake.OpenCalls.Should().Be(0);
        (fake.LastOpenFlags & WinDivertNative.FlagRecvOnly).Should().NotBe(0ul);
        (fake.LastOpenFlags & WinDivertNative.FlagSniff).Should().Be(0ul);
        engine.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void OpenRecvOnly_WithExtraFlags_OrsThemIn()
    {
        var fake = new FakeWinDivertApi();
        using var engine = new WinDivertEngine(fake);

        engine.OpenRecvOnly("outbound and udp", WinDivertOpenParams.Default, extraFlags: WinDivertNative.FlagUseSequenceNumbers);

        (fake.LastOpenFlags & WinDivertNative.FlagRecvOnly).Should().NotBe(0ul);
        (fake.LastOpenFlags & WinDivertNative.FlagUseSequenceNumbers).Should().NotBe(0ul);
    }

    [Fact]
    public void OpenDivert_NoSniffNoRecvOnly_SendStaysEnabled()
    {
        var fake = new FakeWinDivertApi();
        using var engine = new WinDivertEngine(fake);

        engine.OpenDivert(WinDivertFilterBuilder.BuildFilter(), WinDivertOpenParams.Default);

        fake.OpenExCalls.Should().Be(1);
        fake.OpenCalls.Should().Be(0);
        (fake.LastOpenFlags & (WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly)).Should().Be(0ul,
            "düz divert: Send etkin kalır (hedef dışı paketler geri enjekte edilir)");
        engine.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void OpenEx_WhenAlreadyOpen_Throws()
    {
        var fake = new FakeWinDivertApi();
        using var engine = new WinDivertEngine(fake);
        engine.Open("outbound");

        var act = () => engine.OpenEx("outbound and udp", WinDivertOpenParams.Default);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WinDivertOpenParams_NativeSize_MirrorsC()
    {
        // WINDIVERT_OPEN_PARAMS: UINT16 + 2×UINT8 (4 başlık) + 3×4×UINT32 (48) = 52 bayt.
        WinDivertOpenParams.NativeSize.Should().Be(52);
    }

    [Fact]
    public void WinDivertOpenParams_Default_ZeroesQueues_AndVersion0()
    {
        var ps = WinDivertOpenParams.Default;

        ps.Version.Should().Be(0);
        ps.QueueLen0.Should().Be(0);
        ps.QueueLen1.Should().Be(0);
        ps.QueueLen2.Should().Be(0);
        ps.QueueLen3.Should().Be(0);
        ps.QueueTime0.Should().Be(0);
        ps.QueueSize3.Should().Be(0);
    }

    [Fact]
    public void Open_DriverMissing_ThrowsWinDivertException()
    {
        var fake = new FakeWinDivertApi(openResult: IntPtr.Zero, lastError: 2 /* ERROR_FILE_NOT_FOUND */);
        using var engine = new WinDivertEngine(fake);

        var act = () => engine.Open("outbound and udp");

        var ex = act.Should().Throw<WinDivertException>().Which;
        ex.NativeError.Should().Be(2);
        ex.Message.Should().Contain("WinDivert.dll bulunamadı");
    }

    [Fact]
    public void Open_WhenDllMissing_ThrowsWinDivertExceptionWithClearMessage()
    {
        // Dağıtım eksikse (WinDivert.dll uygulama klasöründe yok) P/Invoke
        // DllNotFoundException fırlatır — köprü fault mesajı kullanıcıya ham .NET
        // hatası yerine "hangi dosya eksik" diye net mesajla ulaşmalı.
        var fake = new ThrowingDllApi(exception: new DllNotFoundException("Unable to load DLL 'WinDivert.dll'"));
        using var engine = new WinDivertEngine(fake);

        var act = () => engine.Open("outbound and udp");

        var ex = act.Should().Throw<WinDivertException>().Which;
        ex.NativeError.Should().Be(126); // ERROR_MODULE_NOT_FOUND
        ex.Message.Should().Contain("WinDivert.dll bulunamadı");
        ex.Message.Should().Contain("WinDivert64.sys");
        ex.InnerException.Should().BeOfType<DllNotFoundException>();
    }

    [Fact]
    public void OpenEx_WhenBothEntryPointsMissing_ThrowsWinDivertException()
    {
        // DLL'de ne OpenEx ne de Open export'u yok: ham EntryPointNotFound yerine
        // sürüm uyarısıyla sarılmalı.
        var fake = new ThrowingDllApi(exception: new EntryPointNotFoundException("Unable to find an entry point named 'WinDivertOpenEx'"));
        using var engine = new WinDivertEngine(fake);

        var act = () => engine.OpenEx("outbound and udp", WinDivertOpenParams.Default);

        var ex = act.Should().Throw<WinDivertException>().Which;
        ex.NativeError.Should().Be(127); // ERROR_PROC_NOT_FOUND
        ex.Message.Should().Contain("sürümü uyumsuz");
        ex.Message.Should().Contain("WinDivertOpenEx");
    }

    [Fact]
    public void OpenEx_WhenDllLacksOpenEx_FallsBackToClassicOpen_WithMaskedFlagsAndSetParam()
    {
        // Resmî WinDivert 2.2.2 dağıtımı WinDivertOpenEx'i dışa aktarmaz (yalnızca
        // klassik WinDivertOpen). Köprü bu dağıtımla da çalışmalı: fork/OpenEx'e
        // özgü bitler (QUEUE_*) maskelenir, kuyruk değerleri WinDivertSetParam ile
        // uygulanır, resmî RECV_ONLY biti korunur.
        var fake = new MissingOpenExApi();
        using var engine = new WinDivertEngine(fake);

        var ps = WinDivertOpenParams.Default;
        ps.SetQueueLen(0, 1000);
        ps.SetQueueTime(0, 2000);
        engine.OpenRecvOnly("outbound and udp", ps, extraFlags: WinDivertNative.FlagQueueLength | WinDivertNative.FlagQueueTime);

        fake.OpenCalls.Should().Be(1, "OpenEx yokken klasik Open çağrılır");
        fake.OpenExCalls.Should().Be(1, "önce OpenEx denenir, export yoksa düşülür");
        engine.IsOpen.Should().BeTrue();
        // Fork/OpenEx'e özgü bitler maskelenir; resmî RECV_ONLY korunur.
        (fake.LastOpenFlags & WinDivertNative.FlagRecvOnly).Should().NotBe(0ul);
        (fake.LastOpenFlags & WinDivertNative.ClassicFlagMask).Should().Be(fake.LastOpenFlags,
            "klasik yola yalnızca resmî bitler girer");
        // Kuyruk değerleri SetParam ile uygulanır (resmî parametreler globaldir).
        fake.SetParamCalls.Should().Contain((WinDivertNative.QueueLen, 1000ul));
        fake.SetParamCalls.Should().Contain((WinDivertNative.QueueTime, 2000ul));
    }

    [Fact]
    public void Open_ClassicPath_MasksForkOnlyFlags()
    {
        // Doğrudan Open çağrısında da fork/OpenEx'e özgü bitler resmî sürücüye
        // gitmemeli (bilinmeyen bit hata 87 üretir).
        var fake = new FakeWinDivertApi();
        using var engine = new WinDivertEngine(fake);

        engine.Open("outbound and udp", flags: WinDivertNative.FlagQueueLength | WinDivertNative.FlagQueueSize);

        engine.IsOpen.Should().BeTrue();
        fake.LastOpenFlags.Should().Be(0ul, "yalnızca resmî bitler korunur — fork bitleri maskelenir");
    }

    [Fact]
    public void Open_AccessDenied_HintMentionsAdministrator()
    {
        // Sürücü dosyaları yerinde ama işlem yönetici değil: WinDivertOpen 5 döner
        // (sürücü ilk Open'ta sessizce kurulur — yönetici şart). Mesaj bunu net
        // söylemeli.
        var fake = new FakeWinDivertApi(openResult: IntPtr.Zero, lastError: 5 /* ERROR_ACCESS_DENIED */);
        using var engine = new WinDivertEngine(fake);

        var act = () => engine.Open("outbound and udp");

        var ex = act.Should().Throw<WinDivertException>().Which;
        ex.NativeError.Should().Be(5);
        ex.Message.Should().Contain("yönetici");
    }

    [Fact]
    public void OpenEx_ClassicFallbackOpenFails_CapturesRealErrorBeforeStaleZero()
    {
        // Canlıda görülen kök neden: resmî 2.2.2 (OpenEx export yok) + klasik
        // WinDivertOpen başarısız. Hata kodu çağrının HEMEN ardından yakalanmazsa
        // aradaki P/Invoke'lar (günlük dosya yazma, başarısız SetParam) iş
        // parçacığının son hata değerini sıfırlar ve tanı "Win32 hata kodu: 0" ile
        // gerçek nedeni (örn. 5 = yönetici yetkisi) gizlerdi. Bu test: klasik
        // fallback açılışı başarısızken gerçek kod raporlanmalı ve geçersiz
        // handle'a kuyruk parametresi uygulanmamalı.
        var fake = new StaleLastErrorApi();
        using var engine = new WinDivertEngine(fake);

        var act = () => engine.OpenEx("outbound and udp", WinDivertOpenParams.Default);

        var ex = act.Should().Throw<WinDivertException>().Which;
        ex.NativeError.Should().Be(5, "gerçek Win32 kodu raporlanmalı (bayat 0 değil)");
        ex.Message.Should().NotContain("Win32 hata kodu: 0");
        fake.SetParamCalls.Should().Be(0, "geçersiz handle'a kuyruk parametresi uygulanmaz");
    }

    [Fact]
    public void Open_DriverBlocked_HintMentionsSecuritySoftware()
    {
        // 1275 — güvenlik yazılımı/virtüel ortam driver'ı engelliyor (yaygın
        // tüketici VPN toollarında). Mesaj neden arayacaklarını söylemeli.
        var fake = new FakeWinDivertApi(openResult: IntPtr.Zero, lastError: 1275 /* ERROR_DRIVER_BLOCKED */);
        using var engine = new WinDivertEngine(fake);

        var act = () => engine.Open("outbound and udp");

        var ex = act.Should().Throw<WinDivertException>().Which;
        ex.NativeError.Should().Be(1275);
        ex.Message.Should().Contain("güvenlik yazılımı");
    }

    [Fact]
    public void Send_ForwardsBytesAndAddress_ToApi()
    {
        var fake = new FakeWinDivertApi();
        using var engine = new WinDivertEngine(fake);
        engine.Open("outbound and udp");

        var payload = new byte[] { 0x45, 0x00, 0x00, 0x1c, 0x01, 0x02, 0x03, 0x04 };
        var addr = new WinDivertAddress { IfIdx = 11, Direction = 1 };

        engine.Send(payload, addr);

        fake.SentPayload.Should().Equal(payload);
        fake.SentAddress.IfIdx.Should().Be(11);
        fake.SentAddress.Direction.Should().Be(1);
        fake.SendCalls.Should().Be(1);
    }

    [Fact]
    public void Send_WhenNotOpen_Throws()
    {
        var engine = new WinDivertEngine(new FakeWinDivertApi());
        var act = () => engine.Send(new byte[] { 1 }, default);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Close_ClosesHandleOnce_AndResetsState()
    {
        var fake = new FakeWinDivertApi();
        var engine = new WinDivertEngine(fake);
        engine.Open("outbound and udp");
        engine.Close();
        engine.Close(); // idempotent — ikinci kapanış handle'ı tekrar çağırmaz

        fake.CloseCalls.Should().Be(1);
        engine.IsOpen.Should().BeFalse();
        engine.Filter.Should().BeNull();
    }

    [Fact]
    public void Dispose_WithoutOpen_DoesNotCallApi()
    {
        var fake = new FakeWinDivertApi();
        using (var engine = new WinDivertEngine(fake))
        {
            // hiç açılmadı
        }
        fake.CloseCalls.Should().Be(0);
    }

    // ── DivertWorker — yakalama → kuyruk döngüsü ─────────────────────────

    [Fact]
    public async Task Worker_ReadsPackets_FromBoundChannel_InOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var packets = new[] { new byte[] { 1, 2, 3 }, new byte[] { 10, 20, 30, 40, 50 } };
        var fake = new FakeWinDivertApi(recvPackets: packets);
        using var engine = new WinDivertEngine(fake);
        engine.Open("outbound and udp");

        var worker = engine.StartCapture(ct);
        var collected = new List<DivertedPacket>();
        // xUnit1051 yanlış-pozitif: token'ı içeride ReadAllAsync'e akıttığımız halde
        // analizör IAsyncEnumerable dönüşlü çağrıda TestContext token'ını tanımıyor.
#pragma warning disable xUnit1051
        var enumerator = worker.GetPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
#pragma warning restore xUnit1051
        try
        {
            // Üretici tam olarak 2 paket üretir, sonra false döner.
            for (var i = 0; i < packets.Length; i++)
            {
                (await enumerator.MoveNextAsync()).Should().BeTrue();
                collected.Add(enumerator.Current);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            await worker.StopAsync();
        }

        collected.Count.Should().Be(2);
        // Tier 4: Data havuz kapasitesi olabilir — mantıksal uzunluk (Length) üzerinden karşılaştır.
        collected[0].Data.AsSpan(0, collected[0].Length).SequenceEqual(packets[0]).Should().BeTrue();
        collected[1].Data.AsSpan(0, collected[1].Length).SequenceEqual(packets[1]).Should().BeTrue();
        collected[0].Length.Should().Be(packets[0].Length, "mantıksal uzunluk gerçek paket boyutuyla eşleşmeli");
        collected[0].Address.Direction.Should().Be(1); // outbound işaretlendi
    }

    [Fact]
    public async Task Worker_Stop_CompletesChannel_NoMoreItems()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeWinDivertApi(recvPackets: new[] { new byte[] { 1 } });
        using var engine = new WinDivertEngine(fake);
        engine.Open("outbound and udp");

        var worker = engine.StartCapture(ct);
#pragma warning disable xUnit1051
        var enumerator = worker.GetPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
#pragma warning restore xUnit1051
        try
        {
            (await enumerator.MoveNextAsync()).Should().BeTrue(); // ilk paket
            await worker.StopAsync();
            (await enumerator.MoveNextAsync()).Should().BeFalse(); // kanal tamamlandı
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    // ── Tier 4 — DivertWorker sıfır tahsisat: ArrayPool tamponları ──────

    [Fact]
    public async Task Worker_PooledPacket_IsPooled_ReleaseIdempotent()
    {
        // Yakalanan paketlerin Data tamponu artık ArrayPool'dan kiralanır
        // (paket başına new byte[] yok). Kiralanan paket Release ile havuza
        // döner; çift Release güvenlidir (idempotent).
        var ct = TestContext.Current.CancellationToken;
        var fake = new FakeWinDivertApi(recvPackets: new[] { new byte[] { 0x45, 0, 0, 20 } });
        using var engine = new WinDivertEngine(fake);
        engine.Open("outbound and udp");

        var worker = engine.StartCapture(ct);
#pragma warning disable xUnit1051
        var enumerator = worker.GetPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
#pragma warning restore xUnit1051
        try
        {
            (await enumerator.MoveNextAsync()).Should().BeTrue();
            var packet = enumerator.Current;
            packet.IsPooled.Should().BeTrue("DivertWorker Data tamponunu ArrayPool'dan kiralar");
            packet.Length.Should().Be(4, "havuz kapasitesi değil mantıksal uzunluk bildirilir");
            packet.Data.AsSpan(0, packet.Length).SequenceEqual(new byte[] { 0x45, 0, 0, 20 }).Should().BeTrue();
            packet.Data.Length.Should().BeGreaterThanOrEqualTo(4); // ArrayPool dilimi (kapasite)

            packet.Release();
            packet.Release(); // idempotent — ikinci iade no-op
        }
        finally
        {
            await enumerator.DisposeAsync();
            await worker.StopAsync();
        }
    }

    [Fact]
    public void ManualDivertedPacket_Release_IsNoOp()
    {
        // Doğrudan kurulan paketler (testler, telemetri) havuza ait değildir —
        // Release güvenle no-op'tur ve Data'ya dokunmaz.
        var data = new byte[] { 9, 8, 7 };
        var packet = new DivertedPacket(data, default, DateTimeOffset.UtcNow);

        packet.IsPooled.Should().BeFalse("yalnızca DivertWorker'ın kiraladığı paketler havuzludur");
        packet.Release();
        packet.Data.Should().Equal(data, "manual paketin tamponu iade edilmez");
    }

    // ── DivertWorker kapanış sertleştirmesi (ham iş parçacığı çökme regresyonu) ──
    //
    // DivertWorker.Loop HAM bir Thread üzerinde koşar: döngüden kaçan TEK bir
    // istisna AppDomain'e ulaşır (CurrentDomain_UnhandledException) ve uygulamayı
    // anında kapatır — VPN/rota değişiminde köprü kapanırken canlıda görülen çökme
    // sınıfı (WireGuardNoiseTransport.ReceiveLoop'taki aynı desenin WinDivert
    // karşılığı). Bu testler, Recv beklenmedik bir hata fırlatsa bile döngünün
    // istisna kaçırmadığını sabitler: iş parçacığı temiz biter, kanal tamamlanır,
    // süreç yaşar.

    [Fact]
    public async Task Worker_RecvThrowsUnexpected_ThreadEndsGracefully_NoUnhandledCrash()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ThrowingRecvApi(new InvalidOperationException("natif Recv hatası (kapanış yarışı simülasyonu)"));
        using var engine = new WinDivertEngine(fake);
        engine.Open("outbound and udp");

        using var unhandled = new UnhandledCrashProbe();
        var worker = engine.StartCapture(ct);
#pragma warning disable xUnit1051
        var enumerator = worker.GetPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
#pragma warning restore xUnit1051
        try
        {
            // İlk Recv patlar → döngü güvenle biter → üretici kanalı tamamlar.
            var completed = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            completed.Should().BeFalse("Recv hatası sonrası kanal tamamlanmalı (üretici çıktı)");
        }
        finally
        {
            await enumerator.DisposeAsync();
            await worker.StopAsync();
        }

        // Ham iş parçacığı istisnayı AppDomain'e kaçırmadan sonlandı (kaçsaydı
        // süreç anında kapanır, bu satıra ulaşılamazdı).
        await Task.Delay(150, ct);
        unhandled.Observed.Should().BeFalse("döngü istisnayı AppDomain'e kaçırmamalı");
    }

    [Fact]
    public async Task Worker_RecvThrowsForeignOCE_ThreadEndsGracefully_NoUnhandledCrash()
    {
        // Kapanış yarışı: sürücü katmanı KENDİ token'ı ile OCE fırlatır (bizim cts
        // iptal DEĞİLDİR). Eski `when (_cts.IsCancellationRequested)` filtresi bu
        // OCE'yi kaçırıp ham iş parçacığını öldürürdü (UdpClient.ReceiveAsync'teki
        // aynı çökme deseni) — filtre artık yok, her OCE normal duruştur.
        var ct = TestContext.Current.CancellationToken;
        var fake = new ThrowingRecvApi(new OperationCanceledException("dahili token (cts iptal değil)"));
        using var engine = new WinDivertEngine(fake);
        engine.Open("outbound and udp");

        using var unhandled = new UnhandledCrashProbe();
        var worker = engine.StartCapture(ct);
#pragma warning disable xUnit1051
        var enumerator = worker.GetPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
#pragma warning restore xUnit1051
        try
        {
            var completed = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            completed.Should().BeFalse("OCE sonrası kanal tamamlanmalı (üretici çıktı)");
        }
        finally
        {
            await enumerator.DisposeAsync();
            await worker.StopAsync();
        }

        await Task.Delay(150, ct);
        unhandled.Observed.Should().BeFalse("döngü OCE'yi AppDomain'e kaçırmamalı");
    }

    // ── Yakala → enjekte round-trip (yeni 2.x layout korunur) ─────────────

    [Fact]
    public async Task CaptureToInject_RoundTrips_Full2xAddress_IfIdxAndOutboundPreserved()
    {
        // DivertWorker, gerçek engine üzerinden Recv ile TAM 2.x WinDivertAddress
        // doldurur (Timestamp/Layer/Outbound/…/IfIdx/SubIfIdx); paket + adres
        // kanaldan çıkar, engine.Send aynen geri enjekte eder. Eski 32-baytlık
        // struct'ta Timestamp yoktu ve alanlar 8 bayt kayık okunuyordu — yön/arayüz
        // bozulurdu. Bu test, yeni 80-bayt layout'un yakalamadan enjeksiyona BIT-BIT
        // korunduğunu iddia eder.
        var api = new RoundTripAddressApi();
        using var engine = new WinDivertEngine(api);

        engine.Open("outbound and udp");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var worker = engine.StartCapture(cts.Token);

        var enumerator = worker.GetPacketsAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        DivertedPacket captured;
        try
        {
            (await enumerator.MoveNextAsync()).Should().BeTrue("fake Recv tek paket üretir");
            captured = enumerator.Current;
        }
        finally
        {
            await enumerator.DisposeAsync();
            await worker.StopAsync();
        }

        // Yakalanan adres, fake'in doldurduğu 2.x değerlerini taşıyor.
        captured.Address.IfIdx.Should().Be(11);
        captured.Address.SubIfIdx.Should().Be(22);
        captured.Address.IsOutbound.Should().BeTrue();
        captured.Address.Layer.Should().Be(WinDivertNative.LayerNetwork);
        captured.Address.Loopback.Should().BeTrue();
        captured.Address.IpChecksum.Should().BeTrue();
        captured.Address.Timestamp.Should().Be(0x0102030405060708L);

        // Enjeksiyon: yakalanan adresle aynen geri gönder.
        engine.Send(captured.Data, captured.Address);

        // SentAddress anlamsal olarak yakalananla aynı — yön + arayüz/id korundu.
        api.SentAddress.IfIdx.Should().Be(11, "IfIdx enjeksiyonda korunur");
        api.SentAddress.SubIfIdx.Should().Be(22);
        api.SentAddress.IsOutbound.Should().BeTrue("Outbound (bit 17) korunur");
        api.SentAddress.Layer.Should().Be(WinDivertNative.LayerNetwork);
        api.SentAddress.Timestamp.Should().Be(0x0102030405060708L);
        api.SentAddress.Loopback.Should().BeTrue();
        api.SentAddress.IpChecksum.Should().BeTrue();
        api.SentAddress.Event.Should().Be(0, "Event alanı karıştırılmadı");

        // Bit-bit: 80-bayt adres, yakalamadan enjeksiyona birebir aynı.
        MarshalAddressRoundTripIsEqual(captured.Address, api.SentAddress).Should().BeTrue(
            "2.x layout bit-bit korunur — eski yanlış düzen IfIdx/yönü bozardı");
    }

    private static bool MarshalAddressRoundTripIsEqual(WinDivertAddress a, WinDivertAddress b)
    {
        var ba = MarshalStruct(a);
        var bb = MarshalStruct(b);
        return ba.SequenceEqual(bb);
    }

    private static byte[] MarshalStruct<T>(T value) where T : struct
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return bytes;
    }

    // ── Sahte natif API ──────────────────────────────────────────────────

    private sealed class FakeWinDivertApi : IWinDivertApi
    {
        private readonly byte[][] _recvPackets;
        private int _recvIndex;
        private readonly bool _limited;
        private int _closeCount;

        public FakeWinDivertApi(IntPtr? openResult = null, int lastError = 0, byte[][]? recvPackets = null)
        {
            // openResult null → varsayılan başarı handle'ı; IntPtr.Zero ise gerçek başarısızlık.
            _openResult = openResult ?? new IntPtr(0xCAFE);
            LastError = lastError;
            _recvPackets = recvPackets ?? Array.Empty<byte[]>();
            _limited = recvPackets is not null;
        }

        private readonly IntPtr _openResult;
        public int LastError { get; }
        public int OpenCalls { get; private set; }
        public int OpenExCalls { get; private set; }
        public WinDivertOpenParams LastOpenParams { get; private set; }
        public ulong LastOpenFlags { get; private set; }
        public int SendCalls { get; private set; }
        public byte[]? SentPayload { get; private set; }
        public WinDivertAddress SentAddress { get; private set; }
        public int CloseCalls => _closeCount;

        public IntPtr Open(string? filter, int layer, short priority, ulong flags)
        {
            OpenCalls++;
            return _openResult;
        }

        public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams)
        {
            OpenExCalls++;
            LastOpenParams = openParams;
            LastOpenFlags = flags;
            return _openResult;
        }

        public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address)
        {
            recvLen = 0;
            if (_limited && _recvIndex < _recvPackets.Length)
            {
                var data = _recvPackets[_recvIndex++];
                Marshal.Copy(data, 0, packet, data.Length);
                recvLen = data.Length;
                address.Direction = WinDivertNative.DirectionOutbound;
                address.IfIdx = 5;
                return true;
            }
            return false;
        }

        public bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address)
        {
            sendLen = length;
            SentPayload = new byte[length];
            Marshal.Copy(packet, SentPayload, 0, length);
            SentAddress = address;
            SendCalls++;
            return true;
        }

        public bool Close(IntPtr handle)
        {
            _closeCount++;
            return true;
        }

        public bool SetParam(IntPtr handle, int param, ulong value)
        {
            SetParamCalls.Add((param, value));
            return true;
        }

        public int GetLastError() => LastError;

        public List<(int Param, ulong Value)> SetParamCalls { get; } = new();
    }

    /// <summary>Recv çağrısında her seferinde verilen istisnayı fırlatan sahte API —
    /// natif katmanın kapanış/rota değişimi yarışında üretebileceği hataları simüle
    /// eder (DivertWorker ham iş parçacığı sertleştirme testleri).</summary>
    private sealed class ThrowingRecvApi : IWinDivertApi
    {
        private readonly Exception _exception;

        public ThrowingRecvApi(Exception exception) => _exception = exception;

        public IntPtr Open(string? filter, int layer, short priority, ulong flags) => new(0xCAFE);
        public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams) => new(0xCAFE);

        public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address)
            => throw _exception;

        public bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address)
        {
            sendLen = 0;
            return true;
        }

        public bool Close(IntPtr handle) => true;
        public bool SetParam(IntPtr handle, int param, ulong value) => true;
        public int GetLastError() => 0;
    }

    /// <summary>AppDomain.UnhandledException'a geçici dinleyici — bir arka plan
    /// iş parçacığı istisna kaçırırsa bayrak set edilir (süreç ölmeden önce yakalar).</summary>
    private sealed class UnhandledCrashProbe : IDisposable
    {
        private readonly UnhandledExceptionEventHandler _handler;

        public bool Observed { get; private set; }

        public UnhandledCrashProbe()
        {
            _handler = (_, e) =>
            {
                if (e.ExceptionObject is not null)
                {
                    Observed = true;
                }
            };
            AppDomain.CurrentDomain.UnhandledException += _handler;
        }

        public void Dispose() => AppDomain.CurrentDomain.UnhandledException -= _handler;
    }

    /// <summary>OpenEx export'u OLMAYAN resmî 2.2.2 dağıtımını simüle eder —
    /// OpenEx çağrısı EntryPointNotFound fırlatır, klasik Open çalışır.</summary>
    private sealed class MissingOpenExApi : IWinDivertApi
    {
        public IntPtr Open(string? filter, int layer, short priority, ulong flags)
        {
            OpenCalls++;
            LastOpenFlags = flags;
            return new IntPtr(0xCAFE);
        }

        public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams)
        {
            OpenExCalls++;
            throw new EntryPointNotFoundException("Unable to find an entry point named 'WinDivertOpenEx' in DLL 'WinDivert.dll'.");
        }

        public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address)
        {
            recvLen = 0;
            return false;
        }

        public bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address)
        {
            sendLen = 0;
            return true;
        }

        public bool Close(IntPtr handle) => true;

        public bool SetParam(IntPtr handle, int param, ulong value)
        {
            SetParamCalls.Add((param, value));
            return true;
        }

        public int GetLastError() => 0;

        public int OpenCalls { get; private set; }
        public int OpenExCalls { get; private set; }
        public ulong LastOpenFlags { get; private set; }
        public List<(int Param, ulong Value)> SetParamCalls { get; } = new();
    }

    /// <summary>
    /// OpenEx export'u OLMAYAN resmî 2.2.2 dağıtımı + başarısız klasik Open.
    /// GetLastError yalnızca İLK okumada gerçek kodu (5) verir, sonra 0 döner —
    /// gerçek dünyada aradaki P/Invoke'ların (günlük dosya yazma, SetParam)
    /// iş parçacığının son hata değerini sıfırlamasını simüle eder.
    /// </summary>
    private sealed class StaleLastErrorApi : IWinDivertApi
    {
        private int _errorReads;

        public int SetParamCalls { get; private set; }

        public IntPtr Open(string? filter, int layer, short priority, ulong flags)
            => IntPtr.Zero; // klasik açılış başarısız

        public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams)
            => throw new EntryPointNotFoundException("Unable to find an entry point named 'WinDivertOpenEx' in DLL 'WinDivert.dll'.");

        public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address)
        {
            recvLen = 0;
            return false;
        }

        public bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address)
        {
            sendLen = 0;
            return false;
        }

        public bool Close(IntPtr handle) => true;

        public bool SetParam(IntPtr handle, int param, ulong value)
        {
            SetParamCalls++;
            return false;
        }

        public int GetLastError()
        {
            _errorReads++;
            return _errorReads == 1 ? 5 : 0; // ilk okuma gerçek kod, sonrası bayat 0
        }
    }

    /// <summary>Open/OpenEx çağrısında verilen istisnayı fırlatan sahte API — DLL
    /// yükleme hatalarını (DllNotFoundException / EntryPointNotFoundException)
    /// simüle eder.</summary>
    private sealed class ThrowingDllApi : IWinDivertApi
    {
        private readonly Exception _exception;

        public ThrowingDllApi(Exception exception) => _exception = exception;

        public IntPtr Open(string? filter, int layer, short priority, ulong flags) => throw _exception;
        public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams) => throw _exception;
        public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address) => throw _exception;
        public bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address) => throw _exception;
        public bool Close(IntPtr handle) => true;
        public bool SetParam(IntPtr handle, int param, ulong value) => true;
        public int GetLastError() => 0;
    }

    /// <summary>
    /// Recv'te TAM 2.x WinDivertAddress dolduran sahte API — Timestamp, Layer
    /// (bitfield), Outbound/Loopback/IPChecksum bayrakları ve IfIdx/SubIfIdx;
    /// SentAddress ile enjeksiyona geri yansıyanı kaydeder.
    /// </summary>
    private sealed class RoundTripAddressApi : IWinDivertApi
    {
        private bool _captured;
        private readonly byte[] _packet =
        {
            // IPv4 + UDP başlığı (sniff/send kontrolü için yalnızca bayt taşınır)
            0x45, 0x00, 0x00, 0x1C, 0x12, 0x34, 0x00, 0x00,
            0x40, 0x11, 0x00, 0x00, 10, 0, 0, 2, 10, 0, 0, 1,
            0x77, 0x77, 0x00, 0x35, 0x00, 0x08, 0x00, 0x00,
        };

        public WinDivertAddress SentAddress { get; private set; }

        public IntPtr Open(string? filter, int layer, short priority, ulong flags) => new(0xCAFE);
        public IntPtr OpenEx(string? filter, int layer, short priority, ulong flags, in WinDivertOpenParams openParams) => new(0xCAFE);

        public bool Recv(IntPtr handle, IntPtr packet, int length, out int recvLen, ref WinDivertAddress address)
        {
            recvLen = 0;
            if (_captured)
            {
                return false; // tek paket yakala
            }
            _captured = true;

            Marshal.Copy(_packet, 0, packet, _packet.Length);
            recvLen = _packet.Length;
            address.Timestamp = 0x0102030405060708L;
            address.Layer = WinDivertNative.LayerNetwork;   // bit 0-7
            address.Event = 0;                               // bit 8-15
            address.Direction = WinDivertNative.DirectionOutbound; // bit 17
            address.Loopback = true;                         // bit 18
            address.IpChecksum = true;                       // bit 21
            address.IfIdx = 11;
            address.SubIfIdx = 22;
            return true;
        }

        public bool Send(IntPtr handle, IntPtr packet, int length, out int sendLen, ref WinDivertAddress address)
        {
            sendLen = length;
            SentAddress = address;
            return true;
        }

        public bool Close(IntPtr handle) => true;
        public bool SetParam(IntPtr handle, int param, ulong value) => true;
        public int GetLastError() => 0;
    }
}
