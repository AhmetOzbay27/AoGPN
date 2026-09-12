using AwesomeAssertions;

namespace ServiceLib.Tests.Common;

/// <summary>
/// ProcessPathResolver testleri.
///
/// Asıl kilitlenen şey bir <b>algoritma sırası</b>: yol çözümü önce
/// PROCESS_QUERY_LIMITED_INFORMATION ile denenir ve limited sorgu "erişim
/// engellendi" derse pahalı MainModule yolu <b>hiç</b> denenmez. Bu davranış
/// gerçek bir hatanın kaynağını kapatır: MainModule her reddedilişinde bir
/// Win32Exception atar ve Visual Studio bunları yakalansa bile ilk şans
/// istisnası olarak yazar (günlükte tik başına yüzlerce satır).
/// </summary>
public class ProcessPathResolverTests
{
    /// <summary>Bu test sürecinin kendi exe'si — var olduğu kesin.</summary>
    private static string RealExePath => Environment.ProcessPath!;

    private static ProcessPathResolver.LimitedQueryOutcome Denied
        => new(null, ProcessPathResolver.AccessDeniedError);

    // ── Sahte süreçler (pid 0) hiç dokunulmaz ──────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    public void Resolve_NonPositivePid_ReturnsNull_WithoutTouchingEitherPath(int pid)
    {
        var limitedCalls = 0;
        var mainModuleCalls = 0;

        var result = ProcessPathResolver.Resolve(
            pid,
            _ => { limitedCalls++; return Denied; },
            _ => { mainModuleCalls++; return RealExePath; });

        result.Should().BeNull();
        limitedCalls.Should().Be(0, "sahte süreçte hiçbir sistem çağrısı yapılmamalı");
        mainModuleCalls.Should().Be(0);
    }

    // ── ANA REGRESYON KİLİDİ ───────────────────────────────────────────

    [Fact]
    public void Resolve_WhenLimitedQueryIsDenied_NeverAttemptsMainModule()
    {
        // Kısa devre kaldırılırsa bu test kırılır: MainModule denenir, her turda
        // bir Win32Exception atılır ve günlük gürültüsü geri gelir.
        var mainModuleCalls = 0;

        var result = ProcessPathResolver.Resolve(
            Environment.ProcessId,
            _ => Denied,
            _ =>
            {
                mainModuleCalls++;
                return RealExePath;
            });

        result.Should().BeNull();
        mainModuleCalls.Should().Be(
            0,
            "erişim denetimi istenen haklarda monotondur: limited'ı reddeden MainModule'ü de reddeder");
    }

    [Fact]
    public void Resolve_WhenLimitedQuerySucceeds_ReturnsItAndSkipsMainModule()
    {
        var mainModuleCalls = 0;

        var result = ProcessPathResolver.Resolve(
            Environment.ProcessId,
            _ => new ProcessPathResolver.LimitedQueryOutcome(RealExePath, 0),
            _ =>
            {
                mainModuleCalls++;
                return RealExePath;
            });

        result.Should().Be(Path.GetFullPath(RealExePath));
        mainModuleCalls.Should().Be(0, "limited sorgu başarılıysa yedek yola hiç düşülmemeli");
    }

    [Fact]
    public void Resolve_WhenLimitedQueryFailsForAnotherReason_FallsBackToMainModule()
    {
        // Erişim reddi DIŞINDAKİ bir hata yedek yolu meşru kılar; kısa devre
        // fazla geniş olursa bu test kırılır.
        var mainModuleCalls = 0;

        var result = ProcessPathResolver.Resolve(
            Environment.ProcessId,
            _ => new ProcessPathResolver.LimitedQueryOutcome(null, 6 /* ERROR_INVALID_HANDLE */),
            _ =>
            {
                mainModuleCalls++;
                return RealExePath;
            });

        result.Should().Be(Path.GetFullPath(RealExePath));
        mainModuleCalls.Should().Be(1, "erişim reddi olmayan hatada yedek yol denenmeli");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\windows\system32\notepad.dll")]      // .exe değil
    [InlineData(@"C:\bu\yol\gercekten\yok\missing.exe")]  // var olmayan dosya
    public void Resolve_ResolvedPathIsNotAnExistingExe_ReturnsNull(string? candidate)
    {
        var result = ProcessPathResolver.Resolve(
            Environment.ProcessId,
            _ => new ProcessPathResolver.LimitedQueryOutcome(candidate, 0),
            _ => candidate);

        result.Should().BeNull();
    }

    // ── Canlı süreçler üzerinde gerçek yol ─────────────────────────────

    [Fact]
    public void Resolve_OnLiveCurrentProcess_ReturnsItsOwnExecutable()
    {
        var result = ProcessPathResolver.Resolve(Environment.ProcessId);

        result.Should().NotBeNull();
        File.Exists(result).Should().BeTrue();
        result.Should().EndWith(".exe", "exe yolu döner");
        result.Should().Be(Path.GetFullPath(RealExePath));
    }

    [Fact]
    public void ResolveLimitedOnly_OnLiveCurrentProcess_ResolvesWithoutMainModule()
    {
        var result = ProcessPathResolver.ResolveLimitedOnly(Environment.ProcessId);

        result.Should().NotBeNull();
        File.Exists(result).Should().BeTrue();
    }

    [Fact]
    public void Resolve_OnPseudoProcess_ReturnsNullInsteadOfThrowing()
        => ProcessPathResolver.Resolve(0).Should().BeNull();

    [Fact]
    public void Resolve_ForEveryRunningProcess_NeverThrows()
    {
        // Korumalı, yükseltilmiş, başka mimaride ve arada kapanan süreçler dahil:
        // hiçbir pid bu metottan istisna sızdırmamalı. Sızarsa çağıranların
        // (bağlantı izleyici, süreç listesi, çekirdek temizleyici) döngülerine
        // ilk şans istisnası olarak yansır.
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var pid = process.Id;
                Action probe = () => ProcessPathResolver.Resolve(pid);
                probe.Should().NotThrow("pid {0} için yol çözümü sessiz olmalı", pid);
            }
        }
    }
}
