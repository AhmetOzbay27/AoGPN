using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// CoreBinaryPreflight — açılışta "mihomo/xray yerinde mi?" kontrolü (Faz 3).
///
/// Neden test var: bu kontrol yanlış pozitif üretirse kullanıcı bağlanmadan boş yere
/// uyarılır; yanlış negatif üretirse kusur yine bağlanma anında ortaya çıkar ve
/// kontrolün varlığı anlamsız kalır. Dosya varlığı gerçek bir geçici dizin üzerinden
/// sınanır — yalnızca yol çözümlemesi değil, "var sayılan şey gerçekten var mı"
/// kısmı da kapsanır.
/// </summary>
public class CoreBinaryPreflightTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("aogpn-preflight-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Geçici dizin temizliği test sonucunu etkilemez.
        }
    }

    private static CoreInfo Core(ECoreType type, params string[] exes)
        => new() { CoreType = type, CoreExes = exes.Length == 0 ? null : [.. exes] };

    /// <summary>Geçici dizinde belirtilen ikilileri oluşturur; yol çözümleyicisi döndürür.</summary>
    private Func<string, string, string> Resolver(params string[] existingExeNames)
    {
        foreach (var name in existingExeNames)
        {
            File.WriteAllText(Path.Combine(_dir, Utils.GetExeName(name)), "");
        }

        // Alt klasör önemsiz: yol çözümlemesi enjekte edilir, test yalnızca
        // "bu dosya var mı" sonucunu kontrol eder.
        return (exe, _) => Path.Combine(_dir, exe);
    }

    [Fact]
    public void AllRequiredCoresPresent_YieldsEmptyList()
    {
        var infos = new[] { Core(ECoreType.mihomo, "mihomo"), Core(ECoreType.Xray, "xray") };

        CoreBinaryPreflight.FindMissingCoreBinaries(infos, Resolver("mihomo", "xray"))
            .Should().BeEmpty();
    }

    [Fact]
    public void MissingCore_IsReportedByName()
    {
        var infos = new[] { Core(ECoreType.mihomo, "mihomo"), Core(ECoreType.Xray, "xray") };

        CoreBinaryPreflight.FindMissingCoreBinaries(infos, Resolver("mihomo"))
            .Should().BeEquivalentTo(["Xray"]);
    }

    [Fact]
    public void NonRequiredCore_IsIgnored()
    {
        // v2fly zorunlu değil: eksikliği kullanıcıyı uyarmamalı.
        var infos = new[] { Core(ECoreType.mihomo, "mihomo"), Core(ECoreType.v2fly, "v2fly") };

        CoreBinaryPreflight.FindMissingCoreBinaries(infos, Resolver("mihomo"))
            .Should().BeEmpty();
    }

    [Fact]
    public void CoreWithoutKnownExecutables_IsNotReportedMissing()
    {
        // Bilinen ikili adı yoksa (yalnızca indirme kaydı) "eksik" denemez — aksi
        // halde kurulu olmasına rağmen yanlış uyarı çıkardı.
        var infos = new[] { Core(ECoreType.mihomo) };

        CoreBinaryPreflight.FindMissingCoreBinaries(infos, Resolver())
            .Should().BeEmpty();
    }

    [Fact]
    public void AnyOneOfTheCandidateExecutablesIsEnough()
    {
        // Platforma göre farklı adlar: biri varsa çekirdek kurulu sayılır.
        var infos = new[] { Core(ECoreType.Xray, "xray", "v2ray") };

        CoreBinaryPreflight.FindMissingCoreBinaries(infos, Resolver("v2ray"))
            .Should().BeEmpty();
    }

    [Fact]
    public void NullInput_IsTreatedAsEmpty()
    {
        CoreBinaryPreflight.FindMissingCoreBinaries(null, Resolver()).Should().BeEmpty();
    }

    [Fact]
    public void NullResolver_Throws()
    {
        var act = () => CoreBinaryPreflight.FindMissingCoreBinaries(null, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CustomRequiredSet_OverridesDefaults()
    {
        var infos = new[] { Core(ECoreType.mihomo, "mihomo") };

        CoreBinaryPreflight.FindMissingCoreBinaries(infos, Resolver(), required: [ECoreType.mihomo])
            .Should().BeEquivalentTo(["mihomo"]);
    }

    [Fact]
    public void DefaultRequiredCores_AreMihomoAndXray()
        => CoreBinaryPreflight.RequiredCores.Should().BeEquivalentTo([ECoreType.mihomo, ECoreType.Xray]);
}
