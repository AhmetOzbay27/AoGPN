using AwesomeAssertions;
using ServiceLib.Helper;
using Xunit;

namespace ServiceLib.Tests.Helper;

/// <summary>
/// SQLiteHelper yazma kapısı (write gate). 2026-09-10 oturumunda ~2,9M yazma
/// "write gate busy" ile atlandı (10:10 → 20:31 kesintisiz) ama "write timed out"
/// hiç basmadı: kilit tutan yazma 10-15 sn sürüp kapıyı bırakıyordu — tutucu
/// adlandırılmadan bu yazar bulunamazdı. Bu testler kapının atlanan yazarı ve
/// tutan yazarı nasıl raporladığını doğrular.
///
/// Tüm testler SINGLETON üzerinde çalışır ve değiştirdikleri eşikleri geri yükler:
/// sqlite-net'in bağlantı havuzu bağlantı dizesine göre statiktir; ayrı bir örnek
/// açmak ya da havuz bağlantısını kapatmak tekil örneğin sonraki yazmalarını bozar.
/// Sınıf, DB testleriyle aynı sıralı koleksiyondadır (SharedDatabase).
/// </summary>
[Collection("SharedDatabase")]
public class SqliteHelperGateTests
{
    [Fact]
    public async Task Busy_WaitingWriterSkipped_AndHolderRecorded()
    {
        var helper = SQLiteHelper.Instance;
        helper.ResetGateDiagnosticsForTest();
        helper.GateAcquireTimeout = TimeSpan.FromMilliseconds(100);
        helper.GateHoldCap = TimeSpan.FromSeconds(5);
        helper.SlowHoldReportThreshold = TimeSpan.FromSeconds(5);
        try
        {
            // Kapıyı 800 ms tutan yazar.
            var holderTask = helper.RunSerializedWriteForTest(async () =>
            {
                await Task.Delay(800);
                return 1;
            });

            // Tutucu kapıyı aldı mı — bekle.
            await SpinUntilAsync(() => helper.IsGateHeld);

            // İkinci yazar 100 ms bekler, kapı hâlâ dolu → atlanır (-1).
            var skipped = await helper.RunSerializedWriteForTest(async () => 2);

            skipped.Should().Be(-1);
            helper.BusySkipCount.Should().Be(1);

            (await holderTask).Should().Be(1);
            helper.IsGateHeld.Should().BeFalse("kapı yazar bitince serbest kalmalı");
        }
        finally
        {
            RestoreDefaults(helper);
        }
    }

    [Fact]
    public async Task SlowHold_ReportsWriter_EvenWithoutContention()
    {
        var helper = SQLiteHelper.Instance;
        helper.ResetGateDiagnosticsForTest();
        helper.SlowHoldReportThreshold = TimeSpan.FromMilliseconds(50);
        helper.GateHoldCap = TimeSpan.FromSeconds(5);
        try
        {
            var result = await helper.RunSerializedWriteForTest(async () =>
            {
                await Task.Delay(300);
                return 7;
            });

            result.Should().Be(7, "yavaş yazma tavanı aşmıyorsa tamamlanır");
            helper.SlowHoldLogCount.Should().Be(1, "yavaş tutuş contention olmasa bile raporlanır");
        }
        finally
        {
            RestoreDefaults(helper);
        }
    }

    [Fact]
    public async Task CapExceeded_WriteAbandoned_AndGateReleased()
    {
        var helper = SQLiteHelper.Instance;
        helper.ResetGateDiagnosticsForTest();
        helper.GateHoldCap = TimeSpan.FromMilliseconds(150);
        // Havuz bağlantısını kapatmadan (RecreateAsyncConnection) tavan davranışı
        // doğrulanır — aksi halde statik havuz üzerinden tekil örneğin sonraki
        // yazmaları bozulur.
        helper.RecreateConnectionOnTimeout = false;
        try
        {
            var result = await helper.RunSerializedWriteForTest(async () =>
            {
                await Task.Delay(1000);
                return 1;
            });

            result.Should().Be(-1, "tavanı aşan yazma terk edilir");
            helper.TimeoutAbandonCount.Should().Be(1);
            helper.IsGateHeld.Should().BeFalse("terk edilen yazma kapıyı serbest bırakmalı");
        }
        finally
        {
            RestoreDefaults(helper);
        }
    }

    [Fact]
    public void BuildBusyMessage_ContainsWaitingCallerAndHolder()
    {
        var message = SQLiteHelper.BuildBusyMessage(
            "ConfigHandler.SaveConfig", "StatisticsManager.UpdateAllAsync", 12.3);

        message.Should().Contain("caller: ConfigHandler.SaveConfig");
        message.Should().Contain("gate holder: StatisticsManager.UpdateAllAsync");
        message.Should().Contain("held for: 12.3s");
        message.Should().Contain("write gate busy");
    }

    private static void RestoreDefaults(SQLiteHelper helper)
    {
        helper.GateAcquireTimeout = TimeSpan.FromSeconds(10);
        helper.GateHoldCap = TimeSpan.FromSeconds(15);
        helper.SlowHoldReportThreshold = TimeSpan.FromSeconds(5);
        helper.RecreateConnectionOnTimeout = true;
    }

    private static async Task SpinUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        condition().Should().BeTrue("kapı beklenen sürede tutucuya geçmeli");
    }
}