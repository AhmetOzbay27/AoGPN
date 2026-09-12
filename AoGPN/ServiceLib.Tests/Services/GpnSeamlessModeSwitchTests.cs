using AwesomeAssertions;
using ServiceLib.Services.CoreConfig.Mihomo;
using ServiceLib.Services.Gpn;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// Bağlıyken mod değişimi — kesintisiz tünel entegrasyon testi. Gerçek üretim zinciri
/// uçtan uca sınanır: yönetilen kurallar (<see cref="ManualRoutingRules.BuildManagedRules"/>)
/// → gerçek superset config üretimi (<see cref="CoreConfigHandler.GenerateClientConfig"/>,
/// GPN-MODE/GPN-CHECK/ao-&lt;i&gt; grupları) → oturum kaydı (<see cref="GpnSoftSession.Begin"/>)
/// → canlı mihomo seçimlerine restart'sız delta PUT (<see cref="GpnSoftPolicyApplier"/>).
///
/// Doğrulanan sözleşme: kullanıcı bağlıyken mod pill'ini çevirdiğinde (GPN↔VPN↔Off)
/// WG tünel teli (GPN-Nodes seçimi) asla değişmez — bağlantı kesintisiz sürer; yalnızca
/// mod/rota seçim gruplarına delta PUT atılır. Giriş listesi değiştiyse (yapısal
/// değişiklik) uygulayıcı canlı uygulamayı reddeder ve hiçbir kısmi PUT atmaz — çağıran
/// restart'lı fallback'e düşer, oturum bozulmaz.
/// </summary>
[Collection("SharedDatabase")]
public class GpnSeamlessModeSwitchTests : IDisposable
{
    private const string ServerId = "de";

    private static Config CreateConfig()
    {
        return new Config
        {
            CoreBasicItem = new CoreBasicItem { Loglevel = "warning" },
            TunModeItem = new TunModeItem { EnableTun = true, IcmpRouting = "default" },
            RoutingBasicItem = new RoutingBasicItem
            {
                DomainStrategy = Global.AsIs,
                RoutingIndexId = string.Empty,
            },
            GuiItem = new GUIItem { EnableStatistics = false },
            UiItem = new UIItem
            {
                CurrentLanguage = "en",
                CurrentFontFamily = "sans",
                MainColumnItem = [],
                WindowSizeItem = [],
            },
            ConstItem = new ConstItem(),
            SimpleDNSItem = new SimpleDNSItem
            {
                BootstrapDNS = Global.DomainPureIPDNSAddress.FirstOrDefault(),
                ServeStale = false,
                ParallelQuery = false,
                Strategy4Freedom = Global.AsIs,
                Strategy4Proxy = Global.AsIs,
                Strategy4ProxyDial = Global.AsIs,
            },
            KcpItem = new KcpItem(),
            GrpcItem = new GrpcItem(),
            HysteriaItem = new HysteriaItem(),
            Mux4RayItem = new Mux4RayItem(),
            Mux4SboxItem = new Mux4SboxItem(),
            Inbound = [new InItem
            {
                Protocol = nameof(EInboundProtocol.socks),
                LocalPort = 10808,
                UdpEnabled = true,
                SniffingEnabled = true,
            }],
        };
    }

    private static void BindConfig(Config config)
    {
        var field = typeof(AppManager).GetField(
            "_config", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(AppManager.Instance, config);
    }

    private static SplitTunnelAppItem App(string value, string action)
    {
        return new SplitTunnelAppItem
        {
            EntryType = "app",
            Value = value,
            Port = "",
            Action = action,
        };
    }

    private static GpnServerProfile Server() => new(
        ServerId: ServerId,
        Name: "Almanya",
        EndpointHost: "130.61.223.36",
        EndpointPort: 51820,
        ServerPublicKey: "xQZLxeDqYrCcM7oDYbFxDszWnCk4SzwYYXWsrib8S3A=",
        ClientPrivateKey: "ICsMC9b6W0uzw7NXNlWMgSqQu1W8ZkNvOKt9vlIzyFw=",
        ClientAddress: "10.66.66.2/24",
        Mtu: 1420,
        PersistentKeepalive: 25);

    /// <summary>
    /// Üretimdeki GpnCoreLauncher yoluyla aynı: aday listesi + superset politikasıyla
    /// gerçek config üretimi. Başarı da üretimdeki gibi <see cref="GpnSoftSession.Begin"/>
    /// tetiklenir (config oturum parmak izini taşır).
    /// </summary>
    private static async Task<string> GenerateSupersetYamlAsync(
        Config config,
        List<RulesItem> managedRules,
        GpnSoftRoutingPolicy policy)
    {
        var node = GpnCoreLauncher.BuildWireGuardProfile(Server());
        node.Remarks = "test-node";

        var context = new CoreConfigContext
        {
            Node = node,
            RunCoreType = ECoreType.mihomo,
            AppConfig = config,
            RoutingItem = new RoutingItem
            {
                Id = Utils.GetGuid(false),
                Remarks = "gpn-managed-routing",
                RuleSet = JsonUtils.Serialize(managedRules, false),
                DomainStrategy = Global.AsIs,
            },
            RawDnsItem = null,
            SimpleDnsItem = config.SimpleDNSItem,
            AllProxiesMap = new Dictionary<string, ProfileItem> { [node.IndexId] = node },
            FullConfigTemplate = null,
            IsTunEnabled = true,
            ProtectDomainList = [],
            GpnSoftPolicy = policy,
            GpnCandidates = [Server()],
        };

        var result = await CoreConfigHandler.GenerateClientConfig(context, null);
        result.Success.Should().BeTrue("mihomo superset config üretimi başarılı olmalı: " + result.Msg);
        var yaml = result.Data?.ToString();
        yaml.Should().NotBeNullOrEmpty();
        return yaml!;
    }

    /// <summary>YAML'deki bir select grubunun üye sırasını okur (blok stil).</summary>
    private static List<string> GroupMembers(string yaml, string groupName)
    {
        var members = new List<string>();
        var started = false;
        var inMembers = false;
        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0)
            {
                continue;
            }
            if (!started)
            {
                started = t == $"name: {groupName}"
                    || t == $"- name: {groupName}";
                continue;
            }
            if (t.StartsWith("- name:", StringComparison.Ordinal))
            {
                return members; // sonraki grup/outbound başladı
            }
            if (inMembers)
            {
                if (t.StartsWith("- ", StringComparison.Ordinal))
                {
                    members.Add(t[2..].Trim());
                }
                else
                {
                    inMembers = false; // grubun sonraki anahtarı (ör. type)
                }
            }
            else if (t == "proxies:")
            {
                inMembers = true;
            }
        }
        return members;
    }

    /// <summary>
    /// Canlı mihomo oturumu simülasyonu: gruplar üretilen YAML'den (gerçek config)
    /// çözülür; açılış seçimleri üreticinin ilk-üye varsayılanıdır ("config açılışta
    /// doğru modda başlar"). PUT'lar gerçek mihomo gibi seçimi uygular ve loglanır.
    /// </summary>
    private sealed class LiveMihomoSession
    {
        private readonly Dictionary<string, ClashProxies.ProxiesItem> _groups;
        private readonly List<(string Group, string Target)> _puts = new();

        public LiveMihomoSession(string yaml, IReadOnlyList<string> groupNames)
        {
            _groups = new Dictionary<string, ClashProxies.ProxiesItem>();
            foreach (var name in groupNames)
            {
                var members = GroupMembers(yaml, name);
                members.Should().NotBeEmpty($"üretilen YAML'de {name} grubu olmalı");
                _groups[name] = new ClashProxies.ProxiesItem
                {
                    type = "Selector",
                    now = members[0],
                    all = members,
                };
            }
        }

        public string Now(string group) => _groups[group].now;

        public bool WasPut(string group) => _puts.Any(p => p.Group == group);

        public IReadOnlyList<(string Group, string Target)> Puts => _puts;

        public Dictionary<string, string> Snapshot()
            => _groups.ToDictionary(g => g.Key, g => g.Value.now);

        public void ResetPuts() => _puts.Clear();

        public Task<ClashProxies?> FetchAsync()
            => Task.FromResult<ClashProxies?>(new ClashProxies { proxies = _groups });

        public Func<string, string, Task> SetSelection() => (group, target) =>
        {
            _puts.Add((group, target));
            if (_groups.TryGetValue(group, out var item))
            {
                item.now = target; // gerçek mihomo gibi seçimi uygula
            }
            return Task.CompletedTask;
        };
    }

    public void Dispose() => GpnSoftSession.End();

    /// <summary>
    /// Bağlıyken mod değişimi (Manuel → Global VPN → Manuel → Off): tünel teli
    /// (GPN-Nodes → wg-de) hiçbir adımda değişmez; mod/rota seçimleri canlı superset
    /// oturumuna restart'sız delta PUT ile uygulanır. Oturum kimliği boyunca aynıdır.
    /// </summary>
    [Fact]
    public async Task ModeChangeWhileConnected_AppliesLiveDelta_TunnelWireNeverDropped()
    {
        var config = CreateConfig();
        BindConfig(config);

        var apps = new[]
        {
            App("chrome.exe", "vpn"),
            App("edge.exe", "direct"),
            App("notepad.exe", "block"),
        };

        // ── Faz 1: Manuel (beyaz liste) modda bağlan — gerçek üretim zinciri ──
        var policyManual = GpnSoftRouting.BuildPolicy(
            GameTriggerModes.Manual, invertManualRouting: false, apps, config);
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: false);
        var yaml = await GenerateSupersetYamlAsync(config, managed, policyManual);

        yaml.Should().Contain("name: GPN-MODE");
        yaml.Should().Contain("name: GPN-CHECK");
        yaml.Should().Contain("name: ao-0");
        yaml.Should().Contain("name: ao-2");
        yaml.Should().Contain("PROCESS-NAME,chrome.exe,ao-0");
        yaml.Should().Contain("MATCH,GPN-MODE");

        // Config üretimi gerçek oturum kaydını Begin etti — yumuşak uygulayıcı
        // (üretimde GpnSoftSession.Fingerprint okur) aynı parmak iziyle çalışır.
        GpnSoftSession.IsActive.Should().BeTrue("superset config üretimi oturumu Begin etmeli");
        var fingerprint = GpnSoftSession.Fingerprint!;
        fingerprint.Should().Equal(policyManual.EntryKeys);

        // Canlı mihomo: gruplar üretilen config'ten; açılış seçimi = ilk üye.
        var session = new LiveMihomoSession(yaml, new[]
        {
            GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ModeGroupName,
            GpnSoftRouting.CheckGroupName,
            "ao-0", "ao-1", "ao-2",
        });
        session.Now(GpnMihomoConfigService.NodesGroupName).Should().Be("wg-de", "tünel teli: WG bağlantısı seçili");
        session.Now(GpnSoftRouting.ModeGroupName).Should().Be(GpnSoftRouting.ClashDirect, "beyaz liste yakalayıcısı DIRECT başlar");
        session.Now(GpnSoftRouting.CheckGroupName).Should().Be(GpnMihomoConfigService.NodesGroupName, "bağlıyken IP doğrulaması tünelden çıkar");
        session.Now("ao-0").Should().Be(GpnMihomoConfigService.NodesGroupName, "chrome → tünel");
        session.Now("ao-1").Should().Be(GpnSoftRouting.ClashDirect, "edge → direct");
        session.Now("ao-2").Should().Be(GpnSoftRouting.ClashReject, "notepad → engelli");

        // ── Faz 2: bağlıyken mod değişimi → Global VPN ──
        // Aynı giriş listesi → yapısal değişiklik yok → restart'sız delta PUT.
        var policyVpn = GpnSoftRouting.BuildPolicy(
            GameTriggerModes.Vpn, invertManualRouting: false, apps, config);
        GpnSoftRouting.IsStructuralEntryChange(fingerprint, policyVpn).Should().BeFalse(
            "mod değişimi giriş listesini değiştirmez — kesintisiz uygulanabilir");

        var applied = await GpnSoftPolicyApplier.TryApplyCoreAsync(
            policyVpn, fingerprint, session.FetchAsync, session.SetSelection(), TestContext.Current.CancellationToken);
        applied.Should().BeTrue("canlı superset oturumuna delta uygulanmalı — çekirdek yeniden başlatılmaz");

        // Kesintisizlik 1: WG tünel teli hiç PUT almadı — bağlantı yerinde.
        session.Now(GpnMihomoConfigService.NodesGroupName).Should().Be("wg-de");
        session.WasPut(GpnMihomoConfigService.NodesGroupName).Should().BeFalse(
            "GPN-Nodes seçimine asla PUT atılmaz — tünel kesintisiz sürer");
        // Kesintisizlik 2: oturum kimliği aynı — aynı superset oturumu yaşıyor.
        GpnSoftSession.Fingerprint.Should().Equal(fingerprint, "mod değişimi oturumu sonlandırmaz");
        // Mod gerçekten flip oldu: yakalayıcı + tüm girişler modun gölgesinde tünelde.
        session.Now(GpnSoftRouting.ModeGroupName).Should().Be(GpnMihomoConfigService.NodesGroupName);
        session.Now("ao-0").Should().Be(GpnMihomoConfigService.NodesGroupName);
        session.Now("ao-1").Should().Be(GpnMihomoConfigService.NodesGroupName);
        session.Now("ao-2").Should().Be(GpnMihomoConfigService.NodesGroupName);
        // Delta tam olarak değişen gruplar: GPN-MODE + ao-1 + ao-2 (GPN-CHECK ve
        // ao-0 zaten tüneldeydi → dokunulmadı).
        session.Puts.Should().Equal(
            (GpnSoftRouting.ModeGroupName, GpnMihomoConfigService.NodesGroupName),
            ("ao-1", GpnMihomoConfigService.NodesGroupName),
            ("ao-2", GpnMihomoConfigService.NodesGroupName));

        // ── Faz 3: bağlıyken geri → Manuel: bölünmüş tünelleme yerinde restore ──
        session.ResetPuts();
        var appliedBack = await GpnSoftPolicyApplier.TryApplyCoreAsync(
            policyManual, fingerprint, session.FetchAsync, session.SetSelection(), TestContext.Current.CancellationToken);
        appliedBack.Should().BeTrue();
        session.Now(GpnSoftRouting.ModeGroupName).Should().Be(GpnSoftRouting.ClashDirect);
        session.Now("ao-0").Should().Be(GpnMihomoConfigService.NodesGroupName);
        session.Now("ao-1").Should().Be(GpnSoftRouting.ClashDirect);
        session.Now("ao-2").Should().Be(GpnSoftRouting.ClashReject);
        session.Now(GpnMihomoConfigService.NodesGroupName).Should().Be("wg-de");
        session.WasPut(GpnMihomoConfigService.NodesGroupName).Should().BeFalse();

        // ── Faz 4: bağlıyken Off: trafik DIRECT'e döner, IP doğrulaması da DIRECT'e
        // (ISP baz çizgisi ölçümü tünel IP'siyle kirlenmez). Tünel teli yine korunur. ──
        session.ResetPuts();
        var policyOff = GpnSoftRouting.BuildPolicy(
            GameTriggerModes.Off, invertManualRouting: false, apps, config);
        var appliedOff = await GpnSoftPolicyApplier.TryApplyCoreAsync(
            policyOff, fingerprint, session.FetchAsync, session.SetSelection(), TestContext.Current.CancellationToken);
        appliedOff.Should().BeTrue();
        session.Now(GpnSoftRouting.ModeGroupName).Should().Be(GpnSoftRouting.ClashDirect);
        session.Now(GpnSoftRouting.CheckGroupName).Should().Be(GpnSoftRouting.ClashDirect, "Off'ta IP doğrulaması DIRECT'e döner");
        session.Now("ao-0").Should().Be(GpnSoftRouting.ClashDirect);
        session.Now(GpnMihomoConfigService.NodesGroupName).Should().Be("wg-de");
        session.WasPut(GpnMihomoConfigService.NodesGroupName).Should().BeFalse();
    }

    /// <summary>
    /// Bağlıyken giriş listesi değişirse (yeni uygulama eklendi) yumuşak uygulama
    /// REDDEDİLİR ve hiçbir kısmi PUT atılmaz — canlı oturum dokunulmadan önceki
    /// rotalarda kalır; çağıran restart'lı fallback'e düşer. Oturum asla yarı uygulanmış
    /// bir durumda bırakılmaz.
    /// </summary>
    [Fact]
    public async Task StructuralEntryChangeWhileConnected_RefusesLiveApply_LeavesSessionUntouched()
    {
        var config = CreateConfig();
        BindConfig(config);

        var apps = new[]
        {
            App("chrome.exe", "vpn"),
            App("edge.exe", "direct"),
        };
        var policyManual = GpnSoftRouting.BuildPolicy(
            GameTriggerModes.Manual, invertManualRouting: false, apps, config);
        var managed = ManualRoutingRules.BuildManagedRules(
            GameTriggerModes.Manual, apps, invertManual: false);
        var yaml = await GenerateSupersetYamlAsync(config, managed, policyManual);
        var fingerprint = GpnSoftSession.Fingerprint!;

        var session = new LiveMihomoSession(yaml, new[]
        {
            GpnMihomoConfigService.NodesGroupName,
            GpnSoftRouting.ModeGroupName,
            GpnSoftRouting.CheckGroupName,
            "ao-0", "ao-1",
        });
        var before = session.Snapshot();

        // Kullanıcı bağlıyken yeni bir uygulama ekledi → parmak izi değişir.
        var extended = apps.Append(App("cs2.exe", "vpn")).ToArray();
        var desired = GpnSoftRouting.BuildPolicy(
            GameTriggerModes.Vpn, invertManualRouting: false, extended, config);
        GpnSoftRouting.IsStructuralEntryChange(fingerprint, desired).Should().BeTrue(
            "giriş eklenmesi yapısal değişikliktir — restart'lı fallback gerektirir");

        var applied = await GpnSoftPolicyApplier.TryApplyCoreAsync(
            desired, fingerprint, session.FetchAsync, session.SetSelection(), TestContext.Current.CancellationToken);
        applied.Should().BeFalse("yapısal değişiklikte canlı uygulama reddedilir");

        // Hiçbir kısmi PUT atılmadı — canlı oturum önceki rotalarda yaşıyor.
        session.Puts.Should().BeEmpty("reddedilen uygulama hiçbir gruba dokunmamalı");
        session.Snapshot().Should().Equal(before);
        session.Now(GpnMihomoConfigService.NodesGroupName).Should().Be("wg-de");
    }
}