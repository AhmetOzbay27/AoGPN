using AwesomeAssertions;
using ServiceLib.Models.Entities;
using ServiceLib.Services;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.ViewModels;

[Collection(ServiceLib.Tests.SqliteCatalogCollection.Name)]
public class GpnServerEditViewModelTests : IAsyncLifetime
{
    private const string ClientPriv = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string ServerPub = "5AXLx91KgGJb9sou5who+rpukDGtMk8sT421xPQQsys=";

    public async ValueTask InitializeAsync()
    {
        SQLiteHelper.Instance.CreateTable<GpnServerItem>();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        var rows = await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken);
        if (rows?.Count > 0)
        {
            foreach (var row in rows)
            {
                await WireGuardServerCatalog.RemoveAsync(row.ServerId, TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task Save_NewServer_WritesDpapiEncryptedRow()
    {
        var vm = new GpnServerEditViewModel();
        vm.Name = "İtalya (manuel)";
        vm.EndpointHost = "92.4.220.236";
        vm.EndpointPort = "51820";
        vm.ServerPublicKey = ServerPub;
        vm.ClientPrivateKey = ClientPriv;

        var closed = false;
        vm.RequestClose += (_, _) => closed = true;
        await vm.SaveCmd.Execute();

        closed.Should().BeTrue();
        vm.SavedProfile.Should().NotBeNull();

        var row = (await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken))
            ?.FirstOrDefault(t => t.ServerId == "92.4.220.236:51820");
        row.Should().NotBeNull();
        row!.ClientPrivateKeyEnc.Should().NotBe(ClientPriv); // diskte düz metin yok
        row.ClientPrivateKeyEnc.Should().NotBeNullOrEmpty();
        row.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Save_InvalidEndpoint_DoesNotWrite()
    {
        var vm = new GpnServerEditViewModel();
        vm.EndpointHost = "not-an-ip";
        vm.EndpointPort = "51820";
        vm.ServerPublicKey = ServerPub;
        vm.ClientPrivateKey = ClientPriv;

        var closed = false;
        vm.RequestClose += (_, _) => closed = true;
        await vm.SaveCmd.Execute();

        closed.Should().BeFalse();
        vm.SavedProfile.Should().BeNull();
    }

    [Fact]
    public async Task Save_NewServerWithoutPrivateKey_Rejected()
    {
        var vm = new GpnServerEditViewModel();
        vm.EndpointHost = "92.4.220.236";
        vm.EndpointPort = "51820";
        vm.ServerPublicKey = ServerPub;
        vm.ClientPrivateKey = string.Empty; // yeni sunucuda zorunlu

        var closed = false;
        vm.RequestClose += (_, _) => closed = true;
        await vm.SaveCmd.Execute();

        closed.Should().BeFalse();
    }

    [Fact]
    public async Task Save_EditKeepsExistingKey_WhenFieldLeftEmpty()
    {
        // Önce bir satır yaz (DPAPI'li), sonra düzenleme VM'iyle anahtar boş bırakılarak güncelle.
        var seed = new GpnServerProfile(
            ServerId: string.Empty, Name: "İtalya", EndpointHost: "92.4.220.236",
            EndpointPort: 51820, ServerPublicKey: ServerPub, ClientPrivateKey: ClientPriv,
            ClientAddress: "10.66.66.2/24", Mtu: 1420, Dns: "1.1.1.1", PersistentKeepalive: 25, IsEnabled: true);
        await WireGuardServerCatalog.UpsertAsync(seed, TestContext.Current.CancellationToken);

        var existing = (await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken))
            ?.FirstOrDefault(t => t.ServerId == "92.4.220.236:51820");
        existing.Should().NotBeNull();
        WireGuardServerCatalog.TryMap(existing!, out var profile).Should().BeTrue();

        var vm = new GpnServerEditViewModel(profile!);
        vm.ClientPrivateKey = string.Empty; // anahtar korunmalı
        vm.Name = "İtalya Güncel";
        vm.Mtu = "1400";

        var closed = false;
        vm.RequestClose += (_, _) => closed = true;
        await vm.SaveCmd.Execute();

        closed.Should().BeTrue();
        var row = (await WireGuardServerCatalog.GetItemsAsync(TestContext.Current.CancellationToken))
            ?.FirstOrDefault(t => t.ServerId == "92.4.220.236:51820");
        row.Should().NotBeNull();
        row!.Name.Should().Be("İtalya Güncel");
        row.Mtu.Should().Be(1400);
        WireGuardServerCatalog.TryMap(row, out var updated).Should().BeTrue();
        updated.ClientPrivateKey.Should().Be(ClientPriv); // aynı anahtar korundu
    }

    [Fact]
    public void GenerateKey_ProducesBase64_32ByteKey()
    {
        var key = ServiceLib.Common.Utils.GenerateWireGuardPrivateKey();
        key.Should().NotBeNullOrEmpty();
        var bytes = Convert.FromBase64String(key);
        bytes.Length.Should().Be(32);
    }
}
