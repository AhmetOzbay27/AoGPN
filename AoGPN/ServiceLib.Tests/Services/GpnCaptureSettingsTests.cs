using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ServiceLib.DI;
using ServiceLib.Models.Configs;
using ServiceLib.Services;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// AppManager.Instance._config'i yansıma ile DEĞİŞTİREN testler (BindAppManagerConfig)
/// xUnit paralel koleksiyonları altında birbirine karışır — bu sınıfın Bind→DI-çözüm
/// penceresi başka bir sınıfın rebind'iyle yarışır. Sıralı koleksiyon, bu yarışı kökten
/// kaldırır: paralel koleksiyonlarla asla aynı anda çalışmaz.
/// </summary>
[CollectionDefinition("GpnSharedAppConfigSerial", DisableParallelization = true)]
public sealed class GpnSharedAppConfigSerialCollection
{
}

/// <summary>
/// GpnCaptureItem ayar bloğunun WinDivertOpenParams'a dönüşümü, AppManager
/// config'inden okunması ve DI kaydı testleri — gerçek sürücü gerekmez.
/// </summary>
[Collection("GpnSharedAppConfigSerial")]
public class GpnCaptureSettingsTests
{
    // ── GpnCaptureSettingsMapper (pure) ───────────────────────────────────

    [Fact]
    public void ToOpenParams_Defaults_FillOutboundNetworkSlot()
    {
        var settings = new GpnCaptureItem(); // varsayılanlar: Layer=0, Direction=1, QueueLen=8192, QueueTime=1000

        var ps = GpnCaptureSettingsMapper.ToOpenParams(settings);

        ps.Version.Should().Be(WinDivertNative.OpenParamsVersion0);
        ps.CurrentLayer.Should().Be(1, "kuyruk alanları yalnızca current layer için");
        ps.CurrentDirection.Should().Be(1, "yalnızca current direction için");
        // slot = layer(0) * 2 + direction(1) = 1
        ps.QueueLen1.Should().Be(8192);
        ps.QueueTime1.Should().Be(1000);
        ps.QueueSize1.Should().Be(0, "QueueSize varsayılan devre dışı → sınırsız");
        // diğer slotlar dokunulmaz
        ps.QueueLen0.Should().Be(0);
        ps.QueueTime3.Should().Be(0);
    }

    [Fact]
    public void ToOpenParams_CustomLayerDirection_WritesCorrectSlot()
    {
        var settings = new GpnCaptureItem
        {
            Layer = 1,           // NETWORK_FORWARD
            Direction = 0,       // inbound
            QueueLen = 4096,
            QueueTime = 500,
            QueueSize = 1048576,
            EnableQueueSize = true,
        };

        var ps = GpnCaptureSettingsMapper.ToOpenParams(settings);

        // slot = 1*2 + 0 = 2
        ps.QueueLen2.Should().Be(4096);
        ps.QueueTime2.Should().Be(500);
        ps.QueueSize2.Should().Be(1048576);
        ps.QueueLen1.Should().Be(0, "farklı slot etkilenmez");
    }

    [Fact]
    public void ToOpenFlags_OnlyEnabledQueues_SetFlagBits()
    {
        var defaults = new GpnCaptureItem();
        var flags = GpnCaptureSettingsMapper.ToOpenFlags(defaults);

        (flags & WinDivertNative.FlagQueueLength).Should().NotBe(0ul, "QueueLen varsayılan açık");
        (flags & WinDivertNative.FlagQueueTime).Should().NotBe(0ul, "QueueTime varsayılan açık");
        (flags & WinDivertNative.FlagQueueSize).Should().Be(0ul, "QueueSize varsayılan kapalı");

        var withSize = new GpnCaptureItem { EnableQueueSize = true };
        (GpnCaptureSettingsMapper.ToOpenFlags(withSize) & WinDivertNative.FlagQueueSize)
            .Should().NotBe(0ul);
    }

    [Fact]
    public void ToCaptureOptions_BuildsOpenParamsAndExtraFlags()
    {
        var settings = new GpnCaptureItem { EnableQueueSize = true, QueueSize = 2097152 };

        var options = GpnCaptureSettingsMapper.ToCaptureOptions(settings);

        options.SniffFirst.Should().BeTrue(); // sniff-önce varsayılanı korunur
        options.OpenParams.QueueSize1.Should().Be(2097152);
        (options.ExtraFlags & WinDivertNative.FlagQueueSize).Should().NotBe(0ul);
        (options.ExtraFlags & WinDivertNative.FlagQueueLength).Should().NotBe(0ul);
    }

    [Fact]
    public void NormalizeSlot_ClampsOutOfRange()
    {
        GpnCaptureSettingsMapper.NormalizeSlot(5, -2).Should().Be(2);   // layer→1, direction→0 → slot 2
        GpnCaptureSettingsMapper.NormalizeSlot(0, 0).Should().Be(0);
        GpnCaptureSettingsMapper.NormalizeSlot(1, 1).Should().Be(3);
    }

    // ── GpnCaptureSettingsProvider (AppManager config) ────────────────────

    [Fact]
    public void Provider_ReadsUserSettings_FromAppManagerConfig()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.GpnCaptureItem = new GpnCaptureItem
        {
            EnableQueueSize = true,
            QueueLen = 16384,
            QueueTime = 250,
            QueueSize = 4194304,
            Layer = 1,
            Direction = 1,
        };
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var provider = new GpnCaptureSettingsProvider();

        provider.Current.Should().BeSameAs(config.GpnCaptureItem);
        var options = provider.CaptureOptions;
        // slot = 1*2 + 1 = 3
        options.OpenParams.QueueLen3.Should().Be(16384);
        options.OpenParams.QueueTime3.Should().Be(250);
        options.OpenParams.QueueSize3.Should().Be(4194304);
        (options.ExtraFlags & WinDivertNative.FlagQueueSize).Should().NotBe(0ul);
    }

    [Fact]
    public void Provider_ConfigMissingItem_FallsBackToDefaults()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.GpnCaptureItem = null!;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var provider = new GpnCaptureSettingsProvider();

        provider.Current.Should().NotBeNull();
        provider.CaptureOptions.OpenParams.QueueLen1.Should().Be(8192); // varsayılan
    }

    // ── DI kayıtları ─────────────────────────────────────────────────────

    [Fact]
    public void Di_ResolvesCaptureSettingsProvider_AndOptionsFromConfig()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.GpnCaptureItem = new GpnCaptureItem { QueueLen = 32768, QueueTime = 750 };
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var services = new ServiceCollection();
        services.AddAoGpnGpnServices();
        using var provider = services.BuildServiceProvider();

        var settingsProvider = provider.GetRequiredService<IGpnCaptureSettingsProvider>();
        settingsProvider.Should().BeOfType<GpnCaptureSettingsProvider>();

        var options = provider.GetRequiredService<GpnCaptureOptions>();
        options.Should().NotBeNull();
        options.OpenParams.QueueLen1.Should().Be(32768, "ayarlar DI üzerinden options'a yansır");
        options.OpenParams.QueueTime1.Should().Be(750);

        // Provider singleton; options TRANSIENT — her bağlantıda canlı config'den
        // taze çözülür (singleton olsaydı set_gpn_capture_settings patch'i bir sonraki
        // açılışa asla yansımazdı).
        provider.GetRequiredService<IGpnCaptureSettingsProvider>().Should().BeSameAs(settingsProvider);
        provider.GetRequiredService<GpnCaptureOptions>().Should().NotBeSameAs(options);
        provider.GetRequiredService<GpnCaptureOptions>().OpenParams.QueueLen1.Should().Be(32768);
    }

    // ── GpnCaptureSettingsPatch (dashboard set_gpn_capture_settings yükü) ──

    [Fact]
    public void Patch_UpdatesOnlyProvidedFields_PreservesOthers()
    {
        var current = new GpnCaptureItem
        {
            QueueLen = 8192,
            QueueTime = 1000,
            QueueSize = 0,
            EnableQueueSize = false,
            Layer = 0,
            Direction = 1,
            Priority = 7,
        };

        var patched = new GpnCaptureSettingsPatch(
            QueueLen: 16384, QueueTime: null, QueueSize: null,
            EnableQueueLen: null, EnableQueueTime: null, EnableQueueSize: true,
            Layer: 1, Direction: null).Apply(current);

        patched.QueueLen.Should().Be(16384);      // gönderildi → uygulandı
        patched.QueueTime.Should().Be(1000);      // gönderilmedi → korundu
        patched.QueueSize.Should().Be(0);         // korundu
        patched.EnableQueueSize.Should().BeTrue(); // gönderildi → uygulandı
        patched.EnableQueueLen.Should().BeTrue();  // korundu
        patched.Layer.Should().Be(1);              // gönderildi → uygulandı
        patched.Direction.Should().Be(1);          // korundu
        patched.Priority.Should().Be(7);           // patch dokunmaz
    }

    [Fact]
    public void Patch_ClampsOutOfRangeValues()
    {
        var patched = new GpnCaptureSettingsPatch(
            QueueLen: 0,            // min 1
            QueueTime: 999_999_999, // max 600k ms
            QueueSize: 200_000_000, // max 64M bayt
            EnableQueueLen: null, EnableQueueTime: null, EnableQueueSize: null,
            Layer: 5, Direction: -3).Apply(new GpnCaptureItem());

        patched.QueueLen.Should().Be(1u);
        patched.QueueTime.Should().Be(600_000u);
        patched.QueueSize.Should().Be(64_000_000u);
        patched.Layer.Should().Be(1);
        patched.Direction.Should().Be(0);
    }

    [Fact]
    public void Patch_AllNull_ReturnsCurrentUnchanged()
    {
        var current = new GpnCaptureItem { QueueLen = 32768, Layer = 1, Direction = 0 };

        var patched = new GpnCaptureSettingsPatch(null, null, null, null, null, null, null, null).Apply(current);

        patched.QueueLen.Should().Be(32768);
        patched.QueueTime.Should().Be(current.QueueTime);
        patched.Layer.Should().Be(1);
        patched.Direction.Should().Be(0);
    }

    [Fact]
    public void Patch_NullCurrent_AppliesToDefaults()
    {
        var patched = new GpnCaptureSettingsPatch(
            QueueLen: 4096, QueueTime: null, QueueSize: null,
            EnableQueueLen: null, EnableQueueTime: null, EnableQueueSize: null,
            Layer: null, Direction: null).Apply(null);

        patched.QueueLen.Should().Be(4096);
        patched.QueueTime.Should().Be(1000); // varsayılan
        patched.Layer.Should().Be(0);
        patched.Direction.Should().Be(1);
    }
}