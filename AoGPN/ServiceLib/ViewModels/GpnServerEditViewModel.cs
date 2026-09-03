using System.Net;

namespace ServiceLib.ViewModels;

/// <summary>
/// gpn_servers tablosu için sunucu ekleme/düzenleme penceresi ViewModel'i.
/// Kullanıcı uç nokta + sunucu genel anahtarı + istemci özel anahtarını girer;
/// Kaydet ile <see cref="WireGuardServerCatalog.UpsertAsync(GpnServerProfile)"/>
/// çağrılır — özel anahtar DPAPI ile şifrelenerek diske yazılır (düz metin çıkmaz).
/// Düzenleme modunda mevcut satırın şifresi çözülmüş hali alana yüklenir; özel
/// anahtar alanı boş bırakılırsa mevcut (disk'teki) anahtar korunur.
/// </summary>
public class GpnServerEditViewModel : MyReactiveObject, ICloseable
{
    public event EventHandler? RequestClose;

    [Reactive]
    public string Name { get; set; } = string.Empty;

    [Reactive]
    public string EndpointHost { get; set; } = string.Empty;

    [Reactive]
    public string EndpointPort { get; set; } = string.Empty;

    [Reactive]
    public string ServerPublicKey { get; set; } = string.Empty;

    /// <summary>Yeni sunucuda zorunlu; düzenlemede boş bırakılınca mevcut anahtar korunur.</summary>
    [Reactive]
    public string ClientPrivateKey { get; set; } = string.Empty;

    [Reactive]
    public string ClientAddress { get; set; } = "10.66.66.2/24";

    [Reactive]
    public string Mtu { get; set; } = "1420";

    [Reactive]
    public string Dns { get; set; } = "1.1.1.1";

    [Reactive]
    public string Keepalive { get; set; } = "25";

    [Reactive]
    public bool IsEnabled { get; set; } = true;

    /// <summary>Pencere başlığı / buton etiketi için mod: true = yeni sunucu, false = düzenleme.</summary>
    public bool IsNew { get; }

    public string WindowTitle => IsNew ? ResUI.GpnServerAddTitle : ResUI.GpnServerEditTitle;

    /// <summary>Düzenlenen mevcut satır (null = yeni). Kayıtta özel anahtar korunması için kullanılır.</summary>
    private readonly GpnServerProfile? _existing;

    /// <summary>Yeni oluşturulan sunucu; Save sonrası çağıran (pencere) bu profili alır.</summary>
    public GpnServerProfile? SavedProfile { get; private set; }

    public ReactiveCommand<Unit, Unit> SaveCmd { get; }
    public ReactiveCommand<Unit, Unit> GenerateKeyCmd { get; }

    public GpnServerEditViewModel(GpnServerProfile? existing = null)
    {
        _existing = existing;
        IsNew = existing is null;

        if (existing is not null)
        {
            Name = existing.Name;
            EndpointHost = existing.EndpointHost;
            EndpointPort = existing.EndpointPort.ToString();
            ServerPublicKey = existing.ServerPublicKey;
            ClientAddress = existing.ClientAddress;
            Mtu = existing.Mtu > 0 ? existing.Mtu.ToString() : "1420";
            Dns = existing.Dns;
            Keepalive = existing.PersistentKeepalive > 0 ? existing.PersistentKeepalive.ToString() : "25";
            IsEnabled = existing.IsEnabled;
        }

        SaveCmd = ReactiveCommand.CreateFromTask(async () => await SaveAsync());
        GenerateKeyCmd = ReactiveCommand.Create(() =>
        {
            ClientPrivateKey = Utils.GenerateWireGuardPrivateKey();
            return Unit.Default;
        });
    }

    private async Task SaveAsync()
    {
        // ── Doğrulama ──
        var host = EndpointHost?.Trim() ?? string.Empty;
        if (host.IsNullOrEmpty() || !IPAddress.TryParse(host, out _))
        {
            NoticeManager.Instance.Enqueue(ResUI.GpnServerInvalidEndpoint);
            return;
        }

        if (!int.TryParse(EndpointPort, out var port) || port is <= 0 or >= 65536)
        {
            NoticeManager.Instance.Enqueue(ResUI.GpnServerInvalidEndpoint);
            return;
        }

        if (ServerPublicKey?.Trim().IsNullOrEmpty() != false)
        {
            NoticeManager.Instance.Enqueue(ResUI.GpnServerInvalidPublicKey);
            return;
        }

        var keepPrivateKey = false;
        var privateKey = ClientPrivateKey?.Trim() ?? string.Empty;
        if (privateKey.IsNullOrEmpty())
        {
            if (_existing is null)
            {
                NoticeManager.Instance.Enqueue(ResUI.GpnServerInvalidPrivateKey);
                return;
            }
            // Düzenleme: alan boş → diskteki (DPAPI'li) anahtar korunur.
            keepPrivateKey = true;
        }

        if (!int.TryParse(Mtu, out var mtu) || mtu <= 0)
        {
            mtu = 1420;
        }
        if (!int.TryParse(Keepalive, out var keepalive) || keepalive <= 0)
        {
            keepalive = 25;
        }
        var clientAddress = ClientAddress?.Trim().IsNotEmpty() == true ? ClientAddress.Trim() : "10.66.66.2/24";
        var dns = Dns?.Trim().IsNotEmpty() == true ? Dns.Trim() : "1.1.1.1";

        // ── Katalog yazımı ──
        try
        {
            if (keepPrivateKey && _existing is not null)
            {
                // Mevcut satırı doğrudan güncelle: private key diskte zaten DPAPI'li.
                var item = new Models.Entities.GpnServerItem
                {
                    ServerId = _existing.ServerId,
                    Name = Name?.Trim().IsNotEmpty() == true ? Name.Trim() : host,
                    EndpointHost = host,
                    EndpointPort = port,
                    ServerPublicKey = ServerPublicKey!.Trim(),
                    ClientPrivateKeyEnc = DpapiCryptor.Encrypt(_existing.ClientPrivateKey),
                    ClientAddress = clientAddress,
                    Mtu = mtu,
                    Dns = dns,
                    Keepalive = keepalive,
                    IsEnabled = IsEnabled,
                    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                var updated = await WireGuardServerCatalog.UpsertAsync(item);
                if (updated <= 0)
                {
                    NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
                    return;
                }
                SavedProfile = new GpnServerProfile(
                    _existing.ServerId, Name?.Trim().IsNotEmpty() == true ? Name.Trim() : host,
                    host, port, ServerPublicKey!.Trim(), _existing.ClientPrivateKey,
                    clientAddress, mtu, dns, keepalive, IsEnabled);
            }
            else
            {
                var profile = new GpnServerProfile(
                    // ServerId uç noktadan türetilir (katalogla aynı kural) — aynı
                    // uç nokta yeniden eklenirse satır çoğalmaz, güncellenir.
                    ServerId: string.Empty,
                    Name: Name?.Trim().IsNotEmpty() == true ? Name.Trim() : host,
                    EndpointHost: host,
                    EndpointPort: port,
                    ServerPublicKey: ServerPublicKey!.Trim(),
                    ClientPrivateKey: privateKey,
                    ClientAddress: clientAddress,
                    Mtu: mtu,
                    Dns: dns,
                    PersistentKeepalive: keepalive,
                    IsEnabled: IsEnabled);
                var added = await WireGuardServerCatalog.UpsertAsync(profile);
                if (added <= 0)
                {
                    NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
                    return;
                }
                SavedProfile = profile;
            }

            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"GpnServerEditViewModel kaydetme hatası: {ex.Message}");
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }
}
