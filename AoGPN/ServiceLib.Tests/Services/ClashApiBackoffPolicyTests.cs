using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// ClashApiBackoffPolicy — mihomo denetleyicisi erişilemediğinde deneme aralığının
/// kademeli açıldığını, üst sınırda durduğunu ve ilk başarıda anında sıfırlandığını
/// doğrular. Zaman dışarıdan verilir; testler gerçek bekleme yapmaz.
/// </summary>
public sealed class ClashApiBackoffPolicyTests
{
    private static readonly DateTime T0 = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FreshPolicy_AllowsAttemptImmediately()
    {
        var policy = new ClashApiBackoffPolicy(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));

        policy.ShouldAttemptAt(T0).Should().BeTrue();
        policy.IsOpenAt(T0).Should().BeFalse();
        policy.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void SingleFailure_OpensGateOnlyUntilMinBackoff()
    {
        var policy = new ClashApiBackoffPolicy(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));

        policy.RecordFailureAt(T0);

        policy.ConsecutiveFailures.Should().Be(1);
        policy.IsOpenAt(T0).Should().BeTrue();
        policy.RemainingCooldownAt(T0).Should().Be(TimeSpan.FromMilliseconds(500));
        policy.ShouldAttemptAt(T0.AddMilliseconds(499)).Should().BeFalse();
        policy.ShouldAttemptAt(T0.AddMilliseconds(500)).Should().BeTrue();
    }

    [Fact]
    public void ConsecutiveFailures_DoubleTheCooldownUpToTheCap()
    {
        var policy = new ClashApiBackoffPolicy(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));

        policy.RecordFailureAt(T0);
        policy.RemainingCooldownAt(T0).Should().Be(TimeSpan.FromMilliseconds(500));

        policy.RecordFailureAt(T0);
        policy.RemainingCooldownAt(T0).Should().Be(TimeSpan.FromSeconds(1));

        // Üst sınır: 2 sn'de kalır (4 sn'ye taşmaz).
        policy.RecordFailureAt(T0);
        policy.RemainingCooldownAt(T0).Should().Be(TimeSpan.FromSeconds(2));
        policy.RecordFailureAt(T0);
        policy.RemainingCooldownAt(T0).Should().Be(TimeSpan.FromSeconds(2));
        policy.ConsecutiveFailures.Should().Be(4);

        // Sınırın hemen ardından yeniden denenebilir.
        policy.ShouldAttemptAt(T0.AddSeconds(2)).Should().BeTrue();
    }

    [Fact]
    public void Success_ResetsBackoffAndCounter()
    {
        var policy = new ClashApiBackoffPolicy(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        policy.RecordFailureAt(T0);
        policy.RecordFailureAt(T0);

        policy.RecordSuccess();

        policy.ConsecutiveFailures.Should().Be(0);
        policy.IsOpenAt(T0).Should().BeFalse();
        policy.ShouldAttemptAt(T0).Should().BeTrue();

        // Geri çekilme merdiveni başa döner: sonraki hata yine en kısa aralıkla ertelenir.
        policy.RecordFailureAt(T0);
        policy.RemainingCooldownAt(T0).Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void InvalidBounds_DoNotBreakTheLadder()
    {
        // max < min verilirse üst sınır en az min'e çekilir (sonsuz/kademesiz kalmasın).
        var policy = new ClashApiBackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));

        policy.RecordFailureAt(T0);
        policy.RemainingCooldownAt(T0).Should().Be(TimeSpan.FromSeconds(2));
        policy.RecordFailureAt(T0);
        policy.RemainingCooldownAt(T0).Should().Be(TimeSpan.FromSeconds(2));
    }
}
