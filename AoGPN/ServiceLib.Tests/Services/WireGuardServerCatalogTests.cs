using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Helper;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services;

[Collection(ServiceLib.Tests.SqliteCatalogCollection.Name)]
public class WireGuardServerCatalogTests : IAsyncLifetime
{
    private const string ClientPriv = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string ServerPub = "5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=";

    public async ValueTask InitializeAsync()
    {
        SQLiteHelper.Instance.CreateTable<GpnServerItem>();
        // Diğer seri DB sınıfları profileitems tablosuna WireGuard profili bırakabilir
        // (örn. RoutingDrift e2e testleri 'gpn-de' düğümünü). SeedFromProfileItemsAsync
        // bu kalıntıları tohumlar ve şablon tohumlamasına (İtalya dahil) hiç sıra
        // bırakmaz — beklentiyi bozan yabancı WG profillerini her testten önce kaldır.
        try
        {
            SQLiteHelper.Instance.CreateTable<ProfileItem>();
            var profiles = await SQLiteHelper.Instance.TableAsync<ProfileItem>().ToListAsync();
            foreach (var p in profiles ?? [])
            {
                if (p.ConfigType == EConfigType.WireGuard)
                {
                    await SQLiteHelper.Instance.DeleteAsync(p);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("WireGuardServerCatalogTests", ex);
        }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
        if (rows?.Count > 0)
        {
            // Bu test kümesi tarafından eklenen satırları temizle — kendi test DB'mizde.
            foreach (var row in rows)
            {
                await WireGuardServerCatalog.RemoveAsync(row.ServerId, TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public void TryMap_GpnServerItem_DecryptsPrivateKey_AndMapsFields()
    {
        var row = new GpnServerItem
        {
            ServerId = "130.61.223.36:51820",
            Name = "Almanya",
            EndpointHost = "130.61.223.36",
            EndpointPort = 51820,
            ServerPublicKey = ServerPub,
            ClientPrivateKeyEnc = DpapiCryptor.Encrypt(ClientPriv),
            ClientAddress = "10.66.66.3/24",
            Dns = "1.1.1.1",
            Mtu = 1400,
            Keepalive = 20,
        };

        var ok = WireGuardServerCatalog.TryMap(row, out var profile);

        ok.Should().BeTrue();
        profile.ServerId.Should().Be("130.61.223.36:51820");
        profile.Name.Should().Be("Almanya");
        profile.EndpointHost.Should().Be("130.61.223.36");
        profile.ClientPrivateKey.Should().Be(ClientPriv);
        profile.Mtu.Should().Be(1400);
        profile.PersistentKeepalive.Should().Be(20);
        profile.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void TryMap_GpnServerItem_CorruptEncryptedKey_ReturnsFalse()
    {
        var row = new GpnServerItem
        {
            ServerId = "x:51820",
            EndpointHost = "1.2.3.4",
            EndpointPort = 51820,
            ServerPublicKey = ServerPub,
            ClientPrivateKeyEnc = "@@ not base64 @@",
        };

        WireGuardServerCatalog.TryMap(row, out _).Should().BeFalse();
    }

    [Fact]
    public async Task UpsertFromProfileAsync_RoundTrips_PrivateKeyThroughDpapi()
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.WireGuard,
            Remarks = "İtalya (test)",
            Address = "92.4.220.236",
            Port = 51820,
            Password = ClientPriv,
        };
        item.SetProtocolExtra(new ProtocolExtraItem { WgPublicKey = ServerPub, WgInterfaceAddress = "10.66.66.2/24" });

        await WireGuardServerCatalog.UpsertFromProfileAsync(item, TestContext.Current.CancellationToken);

        var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
        var row = rows?.FirstOrDefault(t => t.ServerId == "92.4.220.236:51820");
        row.Should().NotBeNull();
        row!.ClientPrivateKeyEnc.Should().NotBe(ClientPriv); // diskte düz metin yok
        row.ClientPrivateKeyEnc.Should().NotBeNullOrEmpty();

        WireGuardServerCatalog.TryMap(row, out var profile).Should().BeTrue();
        profile.ClientPrivateKey.Should().Be(ClientPriv);
    }

    [Fact]
    public async Task ImportConfText_ResolveConfigToCatalog_RoundTripsThroughDpapi()
    {
        // Sunucu Yönetimi ekranının yaptığı akışın birebir aynısı: kullanıcı bir
        // .conf metni yapıştırır → WireguardFmt.ResolveConfig her [Peer]'i bir
        // ProfileItem'a çözer → UpsertFromProfileAsync gpn_servers'a yazar
        // (özel anahtar DPAPI ile).
        const string conf =
            """
            [Interface]
            PrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
            Address = 10.66.66.2/24

            [Peer]
            PublicKey = 5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=
            Endpoint = 92.4.220.236:51820
            AllowedIPs = 0.0.0.0/0, ::/0
            PersistentKeepalive = 25
            """;

        var peers = WireguardFmt.ResolveConfig(conf);
        peers.Should().NotBeNull();
        peers!.Should().HaveCount(1);

        var imported = 0;
        foreach (var peer in peers)
        {
            if (peer.ConfigType != EConfigType.WireGuard)
            {
                continue;
            }
            imported += await WireGuardServerCatalog.UpsertFromProfileAsync(peer, TestContext.Current.CancellationToken);
        }
        imported.Should().Be(1);

        var row = (await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken))
            ?.FirstOrDefault(t => t.ServerId == "92.4.220.236:51820");
        row.Should().NotBeNull();
        row!.ClientPrivateKeyEnc.Should().NotBe(ClientPriv); // düz metin yok

        WireGuardServerCatalog.TryMap(row, out var profile).Should().BeTrue();
        profile.ClientPrivateKey.Should().Be(ClientPriv);
        profile.EndpointHost.Should().Be("92.4.220.236");
        profile.PersistentKeepalive.Should().Be(25);
    }

    [Fact]
    public async Task UpsertFromProfileAsync_ReImport_DoesNotDuplicateRow()
    {
        var item = new ProfileItem
        {
            ConfigType = EConfigType.WireGuard,
            Remarks = "Almanya (test)",
            Address = "130.61.223.36",
            Port = 51820,
            Password = ClientPriv,
        };
        item.SetProtocolExtra(new ProtocolExtraItem { WgPublicKey = ServerPub });

        await WireGuardServerCatalog.UpsertFromProfileAsync(item, TestContext.Current.CancellationToken);
        await WireGuardServerCatalog.UpsertFromProfileAsync(item, TestContext.Current.CancellationToken);

        var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
        rows!.Count(t => t.ServerId == "130.61.223.36:51820").Should().Be(1);
    }

    [Fact]
    public void TryMap_MapsWireGuardProfileToGpnCandidate()
    {
        var item = new ProfileItem
        {
            IndexId = "wg-germany",
            ConfigType = EConfigType.WireGuard,
            Remarks = "Almanya",
            Address = "130.61.223.36",
            Port = 51820,
            Password = ClientPriv,
        };
        item.SetProtocolExtra(new ProtocolExtraItem
        {
            WgPublicKey = ServerPub,
            WgInterfaceAddress = "10.66.66.3/24",
            WgMtu = 1400,
            WgPersistentKeepalive = 25,
        });

        var ok = WireGuardServerCatalog.TryMap(item, out var profile);

        ok.Should().BeTrue();
        profile.ServerId.Should().Be("wg-germany");
        profile.Name.Should().Be("Almanya");
        profile.EndpointHost.Should().Be("130.61.223.36");
        profile.EndpointPort.Should().Be(51820);
        profile.ServerPublicKey.Should().Be(ServerPub);
        profile.ClientPrivateKey.Should().Be(ClientPriv);
        profile.ClientAddress.Should().Be("10.66.66.3/24");
        profile.Mtu.Should().Be(1400);
        profile.PersistentKeepalive.Should().Be(25);
        profile.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void TryMap_EmptyIndexId_DerivesStableServerIdFromEndpoint()
    {
        var item = new ProfileItem
        {
            IndexId = string.Empty, // conf'tan geçen profillerde genellikle boş
            ConfigType = EConfigType.WireGuard,
            Address = "92.4.220.236",
            Port = 51820,
            Password = ClientPriv,
        };
        item.SetProtocolExtra(new ProtocolExtraItem { WgPublicKey = ServerPub });

        var ok = WireGuardServerCatalog.TryMap(item, out var profile);

        ok.Should().BeTrue();
        profile.ServerId.Should().Be("92.4.220.236:51820"); // uç noktadan türetildi
    }

    [Fact]
    public void TryMap_NonWireGuardProfile_ReturnsFalse()
    {
        var item = new ProfileItem
        {
            IndexId = "vl-1",
            ConfigType = EConfigType.VLESS,
            Address = "example.com",
            Port = 443,
            Password = "guid-like-id",
        };
        item.SetProtocolExtra(new ProtocolExtraItem());

        WireGuardServerCatalog.TryMap(item, out _).Should().BeFalse();
    }

    [Fact]
    public void TryMap_MissingPrivateKey_ReturnsFalse()
    {
        var item = new ProfileItem
        {
            IndexId = "wg-x",
            ConfigType = EConfigType.WireGuard,
            Address = "1.2.3.4",
            Port = 51820,
            Password = string.Empty,
        };
        item.SetProtocolExtra(new ProtocolExtraItem { WgPublicKey = ServerPub });

        WireGuardServerCatalog.TryMap(item, out _).Should().BeFalse();
    }

    [Fact]
    public void TryMap_MissingServerPublicKey_ReturnsFalse()
    {
        var item = new ProfileItem
        {
            IndexId = "wg-y",
            ConfigType = EConfigType.WireGuard,
            Address = "1.2.3.4",
            Port = 51820,
            Password = ClientPriv,
        };
        item.SetProtocolExtra(new ProtocolExtraItem());

        WireGuardServerCatalog.TryMap(item, out _).Should().BeFalse();
    }

    // ── Gömülü ANAHTARSIZ şablonlar + harici anahtarlar (ilk kurulum tohumlaması) ──
    // Gerçek istemci özel anahtarları SÜRÜM KONTROLÜNE YAZILMAZ — şablonlar anahtarsızdır,
    // anahtar env (GPN_*_PRIVATE_KEY / GPN_*_CONF_B64) veya ayardan (GpnSeed*PrivateKey) gelir.

    // Deterministik SAHTE test anahtarları (32 bayt doldurma) — gerçek anahtar YOK.
    private const string FakeItalyKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="; // 32 sıfır bayt
    private const string FakeGermanyKey = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE="; // 32x 0x01

    [Theory]
    [InlineData(Global.GpnSampleItalyConf, "92.4.220.236", "5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=")]
    [InlineData(Global.GpnSampleGermanyConf, "130.61.223.36", "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=")]
    public void EmbeddedSampleConf_IsKeylessTemplate(string resource, string endpointHost, string serverPub)
    {
        // Şablon yapıyı taşır (uç nokta, sunucu genel anahtarı, adres) ama İSTEMCİ
        // ÖZEL ANAHTARI İÇERMEZ — anahtar dışarıdan gelir, sürüm kontrolüne yazılmaz.
        var confText = EmbedUtils.GetEmbedText(resource);
        confText.Should().NotBeNullOrEmpty($"gömülü kaynak okunamadı: {resource}");

        confText.Should().Contain($"Endpoint = {endpointHost}:51820");
        confText.Should().Contain($"PublicKey = {serverPub}");
        confText.Should().Contain("Address = 10.66.66.2/24");
        confText.Should().NotContain("PrivateKey =", "şablon anahtarsız olmalı (PrivateKey yönergesi yok)");
        // Anahtarsız conf çözümlenemez — tohumlama her zaman önce anahtarı enjekte eder.
        WireguardFmt.ResolveConfig(confText).Should().BeNull("anahtarsız conf ResolveConfig'te reddedilir");
    }

    [Fact]
    public void InjectPrivateKey_AddsMissingLine_AndReplacesExisting()
    {
        var template = EmbedUtils.GetEmbedText(Global.GpnSampleItalyConf);

        // [Interface]'te PrivateKey yok → ekler.
        var merged = WireGuardServerCatalog.InjectPrivateKey(template, FakeItalyKey);
        merged.Should().NotBeNull();
        var peer = WireguardFmt.ResolveConfig(merged!)![0];
        peer.Password.Should().Be(FakeItalyKey);

        // Var olan PrivateKey satırını değiştirir (idempotent).
        var again = WireGuardServerCatalog.InjectPrivateKey(merged!, FakeGermanyKey);
        WireguardFmt.ResolveConfig(again!)![0].Password.Should().Be(FakeGermanyKey);
    }

    [Fact]
    public void InjectPrivateKey_InvalidInput_ReturnsNull()
    {
        WireGuardServerCatalog.InjectPrivateKey(string.Empty, FakeItalyKey).Should().BeNull();
        WireGuardServerCatalog.InjectPrivateKey("[Interface]\n", null).Should().BeNull();
        WireGuardServerCatalog.InjectPrivateKey("no sections here", FakeItalyKey).Should().BeNull();
    }

    [Fact]
    public void CollectExternalClientKeys_SettingsThenEnv_EnvWins()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.GuiItem.GpnSeedItalyPrivateKey = FakeItalyKey;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        try
        {
            // Ayar + env yok → ayar görünür.
            var keys = WireGuardServerCatalog.CollectExternalClientKeys();
            keys["92.4.220.236:51820"].Should().Be(FakeItalyKey);
            keys.Should().NotContainKey("130.61.223.36:51820");

            // Env ayarı ezer.
            Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", FakeGermanyKey);
            keys = WireGuardServerCatalog.CollectExternalClientKeys();
            keys["92.4.220.236:51820"].Should().Be(FakeGermanyKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", null);
            // Ayarları da geri al — aksi halde bu testin bıraktığı GpnSeedItalyPrivateKey
            // aynı sınıftaki "anahtar yok → tohumlama yok" testini kirletir.
            CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        }
    }

    [Fact]
    public async Task SeedFromTemplatesWithExternalKeys_EnvKey_PopulatesDpapiEncrypted()
    {
        Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", FakeItalyKey);
        Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", FakeGermanyKey);
        try
        {
            await WireGuardServerCatalog.SeedFromTemplatesWithExternalKeysAsync(
                TestContext.Current.CancellationToken);

            var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
            rows.Should().NotBeNull();

            var italy = rows!.FirstOrDefault(t => t.ServerId == "92.4.220.236:51820");
            italy.Should().NotBeNull();
            italy!.ClientPrivateKeyEnc.Should().NotBeNullOrEmpty();
            italy.ClientPrivateKeyEnc.Should().NotBe(FakeItalyKey); // diske düz metin yazılmaz

            var germany = rows.FirstOrDefault(t => t.ServerId == "130.61.223.36:51820");
            germany.Should().NotBeNull();

            // DPAPI çözümü enjekte edilen anahtarı geri verir — bağlantı anında kullanılır.
            WireGuardServerCatalog.TryMap(italy, out var italyProfile).Should().BeTrue();
            italyProfile.ClientPrivateKey.Should().Be(FakeItalyKey);
            WireGuardServerCatalog.TryMap(germany, out var germanyProfile).Should().BeTrue();
            germanyProfile.ClientPrivateKey.Should().Be(FakeGermanyKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", null);
            Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", null);
        }
    }

    [Fact]
    public async Task SeedFromTemplates_NoExternalKey_SkipsSeeding()
    {
        // Anahtar sağlanmayan şablon asla tohumlanmaz — boş katalog kalır.
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();

        await WireGuardServerCatalog.SeedFromTemplatesWithExternalKeysAsync(
            TestContext.Current.CancellationToken);

        var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
        rows.Should().BeEmpty("anahtarsız şablon tohumlanmaz");
    }

    [Fact]
    public async Task SeedFromEnvironmentConfs_FullConfB64_SeedsDirectly()
    {
        var confText = EmbedUtils.GetEmbedText(Global.GpnSampleItalyConf);
        var withKey = WireGuardServerCatalog.InjectPrivateKey(confText, FakeItalyKey);
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(withKey!));

        Environment.SetEnvironmentVariable("GPN_ITALY_CONF_B64", b64);
        try
        {
            await WireGuardServerCatalog.SeedFromEnvironmentConfsAsync(TestContext.Current.CancellationToken);

            var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
            var italy = rows!.FirstOrDefault(t => t.ServerId == "92.4.220.236:51820");
            italy.Should().NotBeNull();
            WireGuardServerCatalog.TryMap(italy!, out var profile).Should().BeTrue();
            profile.ClientPrivateKey.Should().Be(FakeItalyKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GPN_ITALY_CONF_B64", null);
        }
    }

    [Fact]
    public async Task LoadAsync_WhenTableEmpty_SeedsFromTemplatesWithExternalKeys()
    {
        // İlk kurulum senaryosu: gpn_servers boş + harici anahtarlar varsa → LoadAsync
        // gömülü anahtarsız şablonları anahtarlarla tohumlar (kullanıcı saklı profili
        // olmasa bile). Anahtar yoksa boş kalır (güvenlik: anahtarsız profil yok).
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();
        Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", FakeItalyKey);
        Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", FakeGermanyKey);
        try
        {
            var profiles = await WireGuardServerCatalog.LoadAsync(TestContext.Current.CancellationToken);

            profiles.Select(p => p.ServerId).Should().Contain("92.4.220.236:51820");
            profiles.Select(p => p.ServerId).Should().Contain("130.61.223.36:51820");
            profiles.First(p => p.ServerId == "92.4.220.236:51820").ClientPrivateKey.Should().Be(FakeItalyKey);
            profiles.First(p => p.ServerId == "130.61.223.36:51820").ClientPrivateKey.Should().Be(FakeGermanyKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", null);
            Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", null);
        }
    }

    [Fact]
    public async Task LoadAsync_WhenTableEmpty_NoExternalKey_StaysEmpty()
    {
        // Güvenlik duruşu: anahtar dışarıdan gelmezse seed boş kalır — gömülü
        // şablon anahtarsız olduğu için kullanılamaz profil üretmez.
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();

        var profiles = await WireGuardServerCatalog.LoadAsync(TestContext.Current.CancellationToken);

        profiles.Should().BeEmpty("anahtarsız tohumlama yapılmaz");
    }

    // ── Yeni kurulum deneyimi — uçtan uca ──────────────────────────────────
    // Boş veritabanında uygulama başlatılır → gpn_servers İtalya/Almanya ile dolar
    // (DPAPI'li) → GPN Bağlan'ın tükettiği aday listesi (WireGuardServerCatalog.LoadAsync
    // çıktısı — MainWindowViewModel.TryRunGpnConnectAsync ve dashboard probe bunu kullanır)
    // her iki sunucuyu anahtarlarıyla döndürür. Anahtar kaynağı: env (yeni sözleşme).

    [Fact]
    public async Task NewInstall_EmptyDb_AppStart_SeedsAndPopulatesConnectCandidates()
    {
        // Simüle edilen yeni kurulum: boş gpn_servers + kullanıcının/ortamın sağladığı
        // harici anahtarlar (env — CI/dağıtım sözleşmesi). Uygulama ilk LoadAsync'te
        // tohumlar; aynı çıktı GPN Bağlan'ın aday kaynağıdır.
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();
        Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", FakeItalyKey);
        Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", FakeGermanyKey);
        try
        {
            // ── Adım 1: "uygulama başlat" — ilk LoadAsync (dashboard probe / GPN Bağlan aday kaynağı) ──
            var candidates = await WireGuardServerCatalog.LoadAsync(TestContext.Current.CancellationToken);

            // ── Adım 2: gpn_servers İtalya/Almanya ile dolu; anahtarlar DPAPI'li ──
            var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
            rows!.Select(r => r.ServerId).Should().BeEquivalentTo(
                new[] { "92.4.220.236:51820", "130.61.223.36:51820" });
            rows.Should().OnlyContain(r =>
                !string.IsNullOrEmpty(r.ClientPrivateKeyEnc)
                && r.ClientPrivateKeyEnc != FakeItalyKey
                && r.ClientPrivateKeyEnc != FakeGermanyKey);

            // ── Adım 3: GPN Bağlan aday listesinde her iki sunucu, anahtarlar çözülür ──
            candidates.Select(c => c.ServerId).Should().BeEquivalentTo(
                new[] { "92.4.220.236:51820", "130.61.223.36:51820" });
            candidates.Should().OnlyContain(c => c.IsEnabled);

            var italy = candidates.First(c => c.ServerId == "92.4.220.236:51820");
            italy.EndpointHost.Should().Be("92.4.220.236");
            italy.EndpointPort.Should().Be(51820);
            italy.ServerPublicKey.Should().Be("5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=");
            italy.ClientPrivateKey.Should().Be(FakeItalyKey); // bağlantı anında çözülür
            italy.ClientAddress.Should().Be("10.66.66.2/24");
            italy.Mtu.Should().Be(Global.GpnRecommendedMtu);
            italy.Name.Should().NotBeNullOrEmpty();

            var germany = candidates.First(c => c.ServerId == "130.61.223.36:51820");
            germany.ServerPublicKey.Should().Be("xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=");
            germany.ClientPrivateKey.Should().Be(FakeGermanyKey);

            // ── Adım 4: yeniden başlatma idempotent — ikinci LoadAsync satır çoğaltmaz ──
            var relaunched = await WireGuardServerCatalog.LoadAsync(TestContext.Current.CancellationToken);
            relaunched.Should().HaveCount(2);
            (await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken))!
                .Should().HaveCount(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", null);
            Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", null);
        }
    }

    [Fact]
    public async Task NewInstall_EmptyDb_SettingKeys_SeedsAndPopulatesConnectCandidates()
    {
        // Aynı deneyim, anahtar kaynağı AYAR (GuiItem.GpnSeed*PrivateKey) — masaüstü
        // kullanıcının kurulum akışı. env yoksa ayar tohumlar.
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();
        var config = CoreConfigTestFactory.CreateConfig();
        config.GuiItem.GpnSeedItalyPrivateKey = FakeItalyKey;
        config.GuiItem.GpnSeedGermanyPrivateKey = FakeGermanyKey;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        try
        {
            var candidates = await WireGuardServerCatalog.LoadAsync(TestContext.Current.CancellationToken);

            candidates.Select(c => c.ServerId).Should().BeEquivalentTo(
                new[] { "92.4.220.236:51820", "130.61.223.36:51820" });
            candidates.First(c => c.ServerId == "92.4.220.236:51820").ClientPrivateKey.Should().Be(FakeItalyKey);
            candidates.First(c => c.ServerId == "130.61.223.36:51820").ClientPrivateKey.Should().Be(FakeGermanyKey);

            var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
            rows!.Should().HaveCount(2);
        }
        finally
        {
            // Ayarları geri al — sınıftaki "anahtar yok" testlerini kirletme.
            CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        }
    }

    // ── Varsayılanları geri yükle: gömülü şablon durumu + geri yükleme ────

    [Fact]
    public void GetDefaultTemplates_ParsesBothKeylessTemplates()
    {
        var templates = WireGuardServerCatalog.GetDefaultTemplates();

        templates.Should().HaveCount(2);
        var italy = templates.First(t => t.EndpointHost == "92.4.220.236");
        italy.EndpointPort.Should().Be(51820);
        italy.ServerPublicKey.Should().Be("5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=");
        italy.ClientAddress.Should().Be("10.66.66.2/24");
        italy.Mtu.Should().Be(Global.GpnRecommendedMtu);
        italy.Dns.Should().Be("1.1.1.1");
        italy.PersistentKeepalive.Should().Be(25);

        var germany = templates.First(t => t.EndpointHost == "130.61.223.36");
        germany.ServerPublicKey.Should().Be("xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=");
    }

    [Fact]
    public async Task DefaultsStatus_EmptyDb_ReportsNotSeeded()
    {
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();

        var statuses = await WireGuardServerCatalog.GetDefaultsStatusAsync(TestContext.Current.CancellationToken);

        statuses.Should().HaveCount(2);
        statuses.Should().OnlyContain(s => !s.Seeded && !s.KeyPresent && !s.UpToDate);
    }

    [Fact]
    public async Task DefaultsStatus_SeededWithExternalKeys_UpToDate()
    {
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();
        Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", FakeItalyKey);
        Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", FakeGermanyKey);
        try
        {
            await WireGuardServerCatalog.SeedFromTemplatesWithExternalKeysAsync(TestContext.Current.CancellationToken);

            var statuses = await WireGuardServerCatalog.GetDefaultsStatusAsync(TestContext.Current.CancellationToken);

            statuses.Should().HaveCount(2);
            statuses.Should().OnlyContain(s => s.Seeded && s.KeyPresent && s.UpToDate);
            statuses.Should().OnlyContain(s => s.Differences.Count == 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", null);
            Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", null);
        }
    }

    [Fact]
    public async Task DefaultsStatus_Outdated_ReportsFieldDifferences()
    {
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();
        Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", FakeItalyKey);
        try
        {
            await WireGuardServerCatalog.SeedFromTemplatesWithExternalKeysAsync(TestContext.Current.CancellationToken);

            // Kaydı boz: MTU + DNS şablondan saptır.
            var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
            var italy = rows!.First(r => r.ServerId == "92.4.220.236:51820");
            italy.Mtu = 1300;
            italy.Dns = "8.8.8.8";
            await SQLiteHelper.Instance.ReplaceAsync(italy);

            var statuses = await WireGuardServerCatalog.GetDefaultsStatusAsync(TestContext.Current.CancellationToken);

            var italyStatus = statuses.First(s => s.ServerId == "92.4.220.236:51820");
            italyStatus.Seeded.Should().BeTrue();
            italyStatus.KeyPresent.Should().BeTrue();
            italyStatus.UpToDate.Should().BeFalse();
            italyStatus.Differences.Should().Contain("Mtu");
            italyStatus.Differences.Should().Contain("Dns");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", null);
        }
    }

    [Fact]
    public async Task RestoreDefaults_AppliesTemplateFields_PreservesKeyAndName()
    {
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();
        Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", FakeItalyKey);
        try
        {
            await WireGuardServerCatalog.SeedFromTemplatesWithExternalKeysAsync(TestContext.Current.CancellationToken);

            // Boz + isim/etkinlik değiştir (geri yükleme bunları korumalı).
            var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
            var italy = rows!.First(r => r.ServerId == "92.4.220.236:51820");
            italy.Mtu = 1300;
            italy.Dns = "8.8.8.8";
            italy.Name = "Özel İtalya";
            italy.IsEnabled = false;
            await SQLiteHelper.Instance.ReplaceAsync(italy);

            var result = await WireGuardServerCatalog.RestoreDefaultsAsync(TestContext.Current.CancellationToken);

            result.RestoredCount.Should().Be(1);
            var restored = result.Statuses.First(s => s.ServerId == "92.4.220.236:51820");
            restored.UpToDate.Should().BeTrue();
            restored.Seeded.Should().BeTrue();
            restored.KeyPresent.Should().BeTrue();

            var final = (await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken))!
                .First(r => r.ServerId == "92.4.220.236:51820");
            final.Mtu.Should().Be(Global.GpnRecommendedMtu);   // şablondan geri geldi
            final.Dns.Should().Be("1.1.1.1");
            final.Name.Should().Be("Özel İtalya");      // korundu
            final.IsEnabled.Should().BeFalse();          // korundu
            WireGuardServerCatalog.TryMap(final, out var profile).Should().BeTrue();
            profile.ClientPrivateKey.Should().Be(FakeItalyKey); // DPAPI anahtarı korundu
        }
        finally
        {
            Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", null);
        }
    }

    [Fact]
    public async Task RestoreDefaults_NoRows_SkipsAndCountsZero()
    {
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();

        var result = await WireGuardServerCatalog.RestoreDefaultsAsync(TestContext.Current.CancellationToken);

        result.RestoredCount.Should().Be(0);
        result.Statuses.Should().HaveCount(2);
        result.Statuses.Should().OnlyContain(s => !s.Seeded);
    }

    [Fact]
    public async Task RestoreDefaults_Idempotent_SecondCallRestoresNothing()
    {
        await SQLiteHelper.Instance.DeleteAllAsync<GpnServerItem>();
        Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", FakeItalyKey);
        Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", FakeGermanyKey);
        try
        {
            await WireGuardServerCatalog.SeedFromTemplatesWithExternalKeysAsync(TestContext.Current.CancellationToken);

            var first = await WireGuardServerCatalog.RestoreDefaultsAsync(TestContext.Current.CancellationToken);
            var second = await WireGuardServerCatalog.RestoreDefaultsAsync(TestContext.Current.CancellationToken);

            first.RestoredCount.Should().Be(0); // seed zaten şablonla birebir
            second.RestoredCount.Should().Be(0); // idempotent
            second.Statuses.Should().OnlyContain(s => s.UpToDate);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GPN_ITALY_PRIVATE_KEY", null);
            Environment.SetEnvironmentVariable("GPN_GERMANY_PRIVATE_KEY", null);
        }
    }

    // ── Rozet rotasyonu: aynı uç nokta + farklı anahtarlar ─────────────────

    private const string GermanyPub = "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=";

    private static GpnServerProfile ServerProfile(string host, int port, string pub, string priv) => new(
        ServerId: string.Empty,
        Name: $"{host}:{port}",
        EndpointHost: host,
        EndpointPort: port,
        ServerPublicKey: pub,
        ClientPrivateKey: priv,
        ClientAddress: "10.66.66.2/24",
        Mtu: 1420,
        Dns: "1.1.1.1",
        PersistentKeepalive: 25,
        IsEnabled: true);

    [Fact]
    public async Task Upsert_SameEndpoint_NewPublicAndPrivateKey_RotatesSingleRow()
    {
        // Aynı uç noktaya farklı sunucu genel + istemci özel anahtarı gelir →
        // kayıt güncellenir, uç nokta başına tek satır kalır.
        await WireGuardServerCatalog.UpsertAsync(ServerProfile("92.4.220.236", 51820, ServerPub, FakeItalyKey), TestContext.Current.CancellationToken);
        await WireGuardServerCatalog.UpsertAsync(ServerProfile("92.4.220.236", 51820, GermanyPub, FakeGermanyKey), TestContext.Current.CancellationToken);

        var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
        var matches = rows!.Where(r => r.EndpointHost == "92.4.220.236" && r.EndpointPort == 51820).ToList();
        matches.Should().HaveCount(1);
        matches[0].ServerId.Should().Be("92.4.220.236:51820"); // kanonik ServerId

        WireGuardServerCatalog.TryMap(matches[0], out var profile).Should().BeTrue();
        profile.ServerPublicKey.Should().Be(GermanyPub);
        profile.ClientPrivateKey.Should().Be(FakeGermanyKey); // eski anahtar yok
    }

    [Fact]
    public async Task Upsert_SameEndpoint_OnlyPrivateKeyChanged_RotatesInPlace()
    {
        // Sunucu genel anahtarı aynı, yalnızca istemci özel anahtarı değişti
        // (istemci anahtar rotasyonu) — tek satır, yeni anahtar.
        await WireGuardServerCatalog.UpsertAsync(ServerProfile("130.61.223.36", 51820, ServerPub, FakeItalyKey), TestContext.Current.CancellationToken);
        await WireGuardServerCatalog.UpsertAsync(ServerProfile("130.61.223.36", 51820, ServerPub, FakeGermanyKey), TestContext.Current.CancellationToken);

        var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
        var match = rows!.Single(r => r.ServerId == "130.61.223.36:51820");
        WireGuardServerCatalog.TryMap(match, out var profile).Should().BeTrue();
        profile.ServerPublicKey.Should().Be(ServerPub);
        profile.ClientPrivateKey.Should().Be(FakeGermanyKey);
    }

    [Fact]
    public async Task Upsert_SameEndpoint_RemovesLegacyCustomServerIdRow_AndKeepsOtherEndpoints()
    {
        // Eski sürümden kalan IndexId tabanlı satır (ServerId="wg-legacy") aynı
        // uç noktaya sahip. Kanonik upsert geldiğinde eski rozet silinir; diğer
        // uç nokta (Almanya) dokunulmadan kalır.
        var legacy = new GpnServerItem
        {
            ServerId = "wg-legacy",
            Name = "Eski İtalya",
            EndpointHost = "92.4.220.236",
            EndpointPort = 51820,
            ServerPublicKey = ServerPub,
            ClientPrivateKeyEnc = DpapiCryptor.Encrypt(FakeItalyKey),
            ClientAddress = "10.66.66.2/24",
            Mtu = 1420,
            Dns = "1.1.1.1",
            Keepalive = 25,
            IsEnabled = true,
        };
        await SQLiteHelper.Instance.InsertAsync(legacy); // katalog dışı (eski DB durumu)
        await WireGuardServerCatalog.UpsertAsync(ServerProfile("130.61.223.36", 51820, GermanyPub, FakeGermanyKey), TestContext.Current.CancellationToken);

        // İtalya kanonik satırı yeni anahtarlarla gelir → eski rozet silinir.
        await WireGuardServerCatalog.UpsertAsync(ServerProfile("92.4.220.236", 51820, GermanyPub, FakeGermanyKey), TestContext.Current.CancellationToken);

        var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
        rows!.Any(r => r.ServerId == "wg-legacy").Should().BeFalse();

        var italy = rows.Single(r => r.EndpointHost == "92.4.220.236");
        italy.ServerId.Should().Be("92.4.220.236:51820");
        WireGuardServerCatalog.TryMap(italy, out var italyProfile).Should().BeTrue();
        italyProfile.ServerPublicKey.Should().Be(GermanyPub);
        italyProfile.ClientPrivateKey.Should().Be(FakeGermanyKey);

        // Almanya dokunulmadı.
        var germany = rows.Single(r => r.EndpointHost == "130.61.223.36");
        WireGuardServerCatalog.TryMap(germany, out var germanyProfile).Should().BeTrue();
        germanyProfile.ServerPublicKey.Should().Be(GermanyPub);
        germanyProfile.ClientPrivateKey.Should().Be(FakeGermanyKey);
    }

    [Fact]
    public void TryMap_DefaultsMtuAndKeepalive_WhenProtocolExtra_Partial()
    {
        var item = new ProfileItem
        {
            IndexId = "wg-it",
            ConfigType = EConfigType.WireGuard,
            Remarks = "İtalya",
            Address = "92.4.220.236",
            Port = 51820,
            Password = ClientPriv,
        };
        item.SetProtocolExtra(new ProtocolExtraItem { WgPublicKey = ServerPub, WgInterfaceAddress = "10.66.66.2/24" });

        WireGuardServerCatalog.TryMap(item, out var profile).Should().BeTrue();

        // .conf'ta MTU/PersistentKeepalive yoksa GPN varsayılanları
        // (Global.GpnRecommendedMtu/25) uygulanır.
        profile.Mtu.Should().Be(Global.GpnRecommendedMtu);
        profile.PersistentKeepalive.Should().Be(25);
    }
}