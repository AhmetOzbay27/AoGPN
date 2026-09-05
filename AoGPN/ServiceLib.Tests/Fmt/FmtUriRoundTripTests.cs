using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Handler.Fmt;
using ServiceLib.Models;
using Xunit;

namespace ServiceLib.Tests.Fmt;

/// <summary>
/// Abonelik/düğüm içe aktarma yolunun girdi kapısı: GERÇEK paylaşım URI'leri
/// (trojan / hy2 / tuic / naive / anytls / socks5 / vless-REALITY / vmess)
/// FmtHandler.ResolveConfig ile ayrıştırılır, temel kimlik alanları doğrulanır
/// ve yeniden dışa aktarılıp tekrar ayrıştırıldığında aynı kimliğin korunduğu
/// teyit edilir (round-trip). Ayrıştırıcı regresyonu burada yakalanır — en ufak
/// bir alan kaybı kullanıcının düğüm listesini bozar.
/// </summary>
public class FmtUriRoundTripTests
{
    public static TheoryData<string, EConfigType, string, int, string, string?, string?, string?> Cases => new()
    {
        // (uri, ConfigType, Address, Port, Password, Username, StreamSecurity, Sni)
        {
            "trojan://trojan-password-123@trojan.example.com:443?security=tls&sni=trojan.example.com#Trojan%20Node",
            EConfigType.Trojan, "trojan.example.com", 443, "trojan-password-123", null, "tls", "trojan.example.com"
        },
        {
            "hy2://hy2-pass@h2.example.com:8443?insecure=1&sni=h2.example.com&alpn=h3#Hy2%20Node",
            EConfigType.Hysteria2, "h2.example.com", 8443, "hy2-pass", null, null, "h2.example.com"
        },
        {
            "tuic://tuic-uuid-1111:tuic-password@tuic.example.com:443?sni=tuic.example.com&alpn=h3#Tuic%20Node",
            EConfigType.TUIC, "tuic.example.com", 443, "tuic-password", "tuic-uuid-1111", null, "tuic.example.com"
        },
        {
            "naive+https://naive-user:naive-pass@naive.example.com:443#Naive%20Node",
            EConfigType.Naive, "naive.example.com", 443, "naive-pass", "naive-user", null, null
        },
        {
            "anytls://anytls-pass@anytls.example.com:443?security=tls&sni=anytls.example.com#Anytls%20Node",
            EConfigType.Anytls, "anytls.example.com", 443, "anytls-pass", null, "tls", "anytls.example.com"
        },
        {
            "socks5://socks-user:socks-pass@socks.example.com:1080#Socks5%20Node",
            EConfigType.SOCKS, "socks.example.com", 1080, "socks-pass", "socks-user", null, null
        },
        {
            "vless://REALITY_UUID_0000@203.0.113.10:443?encryption=none&security=reality&sni=www.microsoft.com&fp=chrome&pbk=REALITY_PUBLIC_KEY&sid=REALITY_SHORT_ID&spx=%2F#Reality%20Node",
            EConfigType.VLESS, "203.0.113.10", 443, "REALITY_UUID_0000", null, "reality", "www.microsoft.com"
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Resolve_UriCarriesIdentityFields(
        string uri, EConfigType configType, string address, int port, string password,
        string? username, string? security, string? sni)
    {
        var item = FmtHandler.ResolveConfig(uri, out var msg);

        item.Should().NotBeNull($"uri: {uri}, msg: {msg}");
        item!.ConfigType.Should().Be(configType);
        item.Address.Should().Be(address);
        item.Port.Should().Be(port);
        item.Password.Should().Be(password);
        if (username is not null)
        {
            item.Username.Should().Be(username);
        }
        if (security is not null)
        {
            item.StreamSecurity.Should().Be(security);
        }
        if (sni is not null)
        {
            item.Sni.Should().Be(sni);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTrip_UriReExportsAndReResolvesToSameIdentity(
        string uri, EConfigType configType, string address, int port, string password,
        string? username, string? security, string? sni)
    {
        var first = FmtHandler.ResolveConfig(uri, out _);
        first.Should().NotBeNull();
        var expectedRemarks = first!.Remarks;

        // Dışa aktar → tekrar ayrıştır: kimlik alanları korunmalı.
        var exported = FmtHandler.GetShareUri(first);
        exported.Should().NotBeNullOrWhiteSpace();

        var second = FmtHandler.ResolveConfig(exported!, out var msg2);
        second.Should().NotBeNull($"re-exported uri: {exported}, msg: {msg2}");
        second!.ConfigType.Should().Be(configType);
        second.Address.Should().Be(address);
        second.Port.Should().Be(port);
        second.Password.Should().Be(password);
        second.Remarks.Should().Be(expectedRemarks, "remarks (fragment) dışa aktarımda korunur");
        if (username is not null)
        {
            second.Username.Should().Be(username);
        }
        if (sni is not null)
        {
            second.Sni.Should().Be(sni);
        }
    }

    [Fact]
    public void Resolve_VmessBase64Json_CarriesIdentityFields()
    {
        var json = "{\"v\":\"2\",\"ps\":\"vmess node\",\"add\":\"vmess.example.com\",\"port\":\"8443\"," +
                   "\"id\":\"vmess-uuid-2222\",\"aid\":\"0\",\"scy\":\"auto\",\"net\":\"tcp\"," +
                   "\"type\":\"none\",\"host\":\"\",\"path\":\"\",\"tls\":\"\"}";
        var uri = Global.ProtocolShares[EConfigType.VMess] + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var item = FmtHandler.ResolveConfig(uri, out var msg);

        item.Should().NotBeNull(msg);
        item!.ConfigType.Should().Be(EConfigType.VMess);
        item.Address.Should().Be("vmess.example.com");
        item.Port.Should().Be(8443);
        item.Password.Should().Be("vmess-uuid-2222");
        item.Remarks.Should().Be("vmess node");
    }
}