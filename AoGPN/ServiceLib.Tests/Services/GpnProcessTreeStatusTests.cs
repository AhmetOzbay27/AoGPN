using AwesomeAssertions;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

/// <summary>
/// NativeProcessTreeSource FATAL / Degraded / Healthy durum makinesi testleri.
/// Gerçek Toolhelp32'e dokunmaz: <see cref="NativeProcessTreeSource"/>'ın
/// korumalı sanal seam'leri (TrySnapshotNative / CollectFallbackNative) alt
/// sınıfta sahte senaryo üretir — GpnCaptureTests'teki StubResolver deseniyle aynı.
/// </summary>
public class GpnProcessTreeStatusTests
{
    [Fact]
    public void Enumerate_Healthy_ToolhelpOk_FullTree()
    {
        var source = new StubSource(
            toolhelpOk: true,
            nativeEntries: [new NativeProcessTreeSource.NativeEntry(100, 0, "Game.exe"), new NativeProcessTreeSource.NativeEntry(200, 100, "Game.exe")]);

        var result = source.Enumerate(TestContext.Current.CancellationToken).ToArray();

        source.Status.Should().Be(ProcessTreeStatus.Healthy);
        result.Should().HaveCount(2);
        result[1].Should().Be(new ProcessInfo(200, 100, "Game.exe")); // parent korunur
    }

    [Fact]
    public void Enumerate_Degraded_ToolhelpFails_FallbackUsed()
    {
        var source = new StubSource(
            toolhelpOk: false,
            fallback: [new ProcessInfo(100, 0, "Game.exe"), new ProcessInfo(300, 0, "lol.exe")]);

        var result = source.Enumerate(TestContext.Current.CancellationToken).ToArray();

        source.Status.Should().Be(ProcessTreeStatus.Degraded);
        result.Should().HaveCount(2);
        result[0].Should().Be(new ProcessInfo(100, 0, "Game.exe")); // parent bilinmez (0)
    }

    [Fact]
    public void Enumerate_Fatal_WhenFallbackThrows()
    {
        var source = new StubSource(
            toolhelpOk: false,
            fallbackThrows: new InvalidOperationException("process tree access denied"));

        var result = source.Enumerate(TestContext.Current.CancellationToken).ToArray();

        source.Status.Should().Be(ProcessTreeStatus.Fatal);
        result.Should().BeEmpty("ağaç erişilemez — hiçbir karar güvenilir değil");
    }

    [Fact]
    public void Enumerate_Fatal_WhenBothEmpty()
    {
        var source = new StubSource(toolhelpOk: false, fallback: []);

        var result = source.Enumerate(TestContext.Current.CancellationToken).ToArray();

        source.Status.Should().Be(ProcessTreeStatus.Fatal, "sıfır süreç = erişim kısıtı");
        result.Should().BeEmpty();
    }

    // ── GpnTargetResolver — LastSourceStatus yüzeylemesi ─────────────────

    [Fact]
    public void Resolver_FatalSource_ReturnsNull_AndSurfacesFatal()
    {
        var resolver = new GpnTargetResolver(
            ["Game.exe"],
            new FixedStatusSource(ProcessTreeStatus.Fatal, [new ProcessInfo(100, 0, "Game.exe")]));

        var snap = resolver.Resolve(TestContext.Current.CancellationToken);

        snap.Should().BeNull();
        resolver.LastSourceStatus.Should().Be(ProcessTreeStatus.Fatal);
    }

    [Fact]
    public void Resolver_HealthySource_Resolves_AndSurfacesHealthy()
    {
        var resolver = new GpnTargetResolver(
            ["Game.exe"],
            new FixedStatusSource(ProcessTreeStatus.Healthy, [new ProcessInfo(100, 0, "Game.exe")]));

        var snap = resolver.Resolve(TestContext.Current.CancellationToken);

        snap.Should().NotBeNull();
        snap!.Pids.Should().Equal(100u);
        resolver.LastSourceStatus.Should().Be(ProcessTreeStatus.Healthy);
    }

    [Fact]
    public void Resolver_EnumerateThrows_MarksFatal()
    {
        var resolver = new GpnTargetResolver(
            ["Game.exe"],
            new ThrowingSource());

        var snap = resolver.Resolve(TestContext.Current.CancellationToken);

        snap.Should().BeNull();
        resolver.LastSourceStatus.Should().Be(ProcessTreeStatus.Fatal);
    }

    // ── Test donanımı ────────────────────────────────────────────────────

    private sealed class StubSource : NativeProcessTreeSource
    {
        private readonly bool _toolhelpOk;
        private readonly NativeProcessTreeSource.NativeEntry[] _nativeEntries;
        private readonly ProcessInfo[] _fallback;
        private readonly Exception? _fallbackThrows;

        public StubSource(
            bool toolhelpOk,
            NativeProcessTreeSource.NativeEntry[]? nativeEntries = null,
            ProcessInfo[]? fallback = null,
            Exception? fallbackThrows = null)
        {
            _toolhelpOk = toolhelpOk;
            _nativeEntries = nativeEntries ?? [];
            _fallback = fallback ?? [];
            _fallbackThrows = fallbackThrows;
        }

        protected override bool TrySnapshotNative(out List<NativeProcessTreeSource.NativeEntry> entries)
        {
            entries = _nativeEntries.ToList();
            return _toolhelpOk && entries.Count > 0;
        }

        protected override IEnumerable<ProcessInfo> CollectFallbackNative(CancellationToken cancellationToken)
        {
            if (_fallbackThrows is not null)
            {
                throw _fallbackThrows;
            }
            return _fallback;
        }
    }

    private sealed class FixedStatusSource : IProcessTreeSource
    {
        private readonly ProcessTreeStatus _status;
        private readonly ProcessInfo[] _processes;

        public FixedStatusSource(ProcessTreeStatus status, ProcessInfo[] processes)
        {
            _status = status;
            _processes = processes;
        }

        public ProcessTreeStatus Status => _status;

        public IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default)
            => _processes;
    }

    private sealed class ThrowingSource : IProcessTreeSource
    {
        public ProcessTreeStatus Status => ProcessTreeStatus.Healthy;

        public IEnumerable<ProcessInfo> Enumerate(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }
}