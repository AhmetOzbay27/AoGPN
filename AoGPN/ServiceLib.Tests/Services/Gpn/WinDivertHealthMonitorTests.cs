using System.IO;
using AwesomeAssertions;
using ServiceLib.Services.Gpn;
using Xunit;

namespace ServiceLib.Tests.Services.Gpn;

/// <summary>
/// <see cref="WinDivertHealthMonitor"/> — durum makinesi, dosya varlığı, cihaz
/// probe'u, yönetici/yetki ayrımı ve SCM kurulum akışı. Goodwired'lar tamamen
/// sahtedir (gerçek WinDivert.dll/sürücü gerekmez — WinDivertDriverSmokeTests'e
/// bakın); AppEvents yayını global kanala gider ama testler Snapshot'a bakar.
/// </summary>
public class WinDivertHealthMonitorTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aogpn-windivert-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Touch(string path) => File.WriteAllText(path, "dummy");

    private static WinDivertHealthMonitor Create(
        string dllPath,
        string sysPath,
        Func<(bool Exists, int Error)>? probe = null,
        Func<string, int>? install = null,
        bool elevated = false)
        => new(dllPath, sysPath, probe, install, elevated);

    [Fact]
    public void Check_DllMissing_ReportsDllMissingWithFilePath()
    {
        var dir = TempDir();
        var monitor = Create(Path.Combine(dir, "WinDivert.dll"), Path.Combine(dir, "WinDivert64.sys"));

        var health = monitor.Check();

        health.State.Should().Be(WinDivertHealthState.DllMissing);
        health.Message.Should().Contain("WinDivert.dll");
        health.NativeError.Should().Be(2);
        monitor.Snapshot.Should().Be(health);
    }

    [Fact]
    public void Check_DriverFileMissing_ReportsDriverFileMissing()
    {
        var dir = TempDir();
        Touch(Path.Combine(dir, "WinDivert.dll"));
        var monitor = Create(Path.Combine(dir, "WinDivert.dll"), Path.Combine(dir, "WinDivert64.sys"));

        var health = monitor.Check();

        health.State.Should().Be(WinDivertHealthState.DriverFileMissing);
        health.Message.Should().Contain("WinDivert64.sys");
    }

    [Fact]
    public void Check_ProbeOk_ReportsReady()
    {
        var dir = TempDir();
        var dll = Path.Combine(dir, "WinDivert.dll");
        var sys = Path.Combine(dir, "WinDivert64.sys");
        Touch(dll);
        Touch(sys);

        var monitor = Create(dll, sys, probe: () => (true, 0));
        var health = monitor.Check();

        health.State.Should().Be(WinDivertHealthState.Ready);
        health.Message.Should().BeNull();
    }

    [Fact]
    public void Check_DeviceAbsent_NotElevated_ReportsNeedsAdmin_NoInstallAttempt()
    {
        var dir = TempDir();
        var dll = Path.Combine(dir, "WinDivert.dll");
        var sys = Path.Combine(dir, "WinDivert64.sys");
        Touch(dll);
        Touch(sys);
        var installCalls = 0;

        var monitor = Create(
            dll, sys,
            probe: () => (false, 2),
            install: _ => { installCalls++; return 0; },
            elevated: false);

        var health = monitor.Check(attemptInstall: true);

        health.State.Should().Be(WinDivertHealthState.NeedsAdmin);
        installCalls.Should().Be(0, "yönetici yetkisi yokken kurulum denenmemeli");
        health.Message.Should().Contain("yönetici");
    }

    [Fact]
    public void Check_DeviceAbsent_Elevated_AttemptInstall_InstallsAndProbesAgain()
    {
        var dir = TempDir();
        var dll = Path.Combine(dir, "WinDivert.dll");
        var sys = Path.Combine(dir, "WinDivert64.sys");
        Touch(dll);
        Touch(sys);
        var probes = 0;
        var installedPath = "";

        var monitor = Create(
            dll, sys,
            probe: () => (++probes == 1) ? (false, 2) : (true, 0),
            install: p => { installedPath = p; return 0; },
            elevated: true);

        var health = monitor.Check(attemptInstall: true);

        health.State.Should().Be(WinDivertHealthState.Ready);
        probes.Should().Be(2, "kurulum öncesi ve sonrası probe beklenir");
        installedPath.Should().Be(Path.GetFullPath(sys), "kurucu sürücü dosyasının tam yolunu almalı");
    }

    [Fact]
    public void Check_Elevated_AttemptInstallFalse_DoesNotInstall()
    {
        var dir = TempDir();
        var dll = Path.Combine(dir, "WinDivert.dll");
        var sys = Path.Combine(dir, "WinDivert64.sys");
        Touch(dll);
        Touch(sys);
        var installCalls = 0;

        var monitor = Create(
            dll, sys,
            probe: () => (false, 2),
            install: _ => { installCalls++; return 0; },
            elevated: true);

        var health = monitor.Check(attemptInstall: false);

        health.State.Should().Be(WinDivertHealthState.NeedsAdmin);
        installCalls.Should().Be(0);
    }

    [Fact]
    public void Check_InstallFails_SignatureError_ReportsInstallFailedWithHint()
    {
        var dir = TempDir();
        var dll = Path.Combine(dir, "WinDivert.dll");
        var sys = Path.Combine(dir, "WinDivert64.sys");
        Touch(dll);
        Touch(sys);

        var monitor = Create(
            dll, sys,
            probe: () => (false, 2),
            install: _ => 577,
            elevated: true);

        var health = monitor.Check(attemptInstall: true);

        health.State.Should().Be(WinDivertHealthState.InstallFailed);
        health.NativeError.Should().Be(577);
        health.Message.Should().Contain("577");
        health.Message.Should().Contain("imza");
    }

    [Fact]
    public void Check_ProbeError_BfeDisabled_ReportsInstallFailedWithHint()
    {
        var dir = TempDir();
        var dll = Path.Combine(dir, "WinDivert.dll");
        var sys = Path.Combine(dir, "WinDivert64.sys");
        Touch(dll);
        Touch(sys);

        var monitor = Create(dll, sys, probe: () => (false, 1753), elevated: true);
        var health = monitor.Check(attemptInstall: true);

        health.State.Should().Be(WinDivertHealthState.InstallFailed);
        health.NativeError.Should().Be(1753);
        health.Message.Should().Contain("Filtreleme");
    }

    [Fact]
    public void Check_ProbeAccessDenied_ReportsNeedsAdmin()
    {
        var dir = TempDir();
        var dll = Path.Combine(dir, "WinDivert.dll");
        var sys = Path.Combine(dir, "WinDivert64.sys");
        Touch(dll);
        Touch(sys);

        var monitor = Create(dll, sys, probe: () => (false, 5), elevated: true);
        var health = monitor.Check(attemptInstall: true);

        health.State.Should().Be(WinDivertHealthState.NeedsAdmin);
        health.NativeError.Should().Be(5);
    }

    [Fact]
    public void Check_InstallStartsButDeviceStillClosed_ReportsInstallFailed()
    {
        var dir = TempDir();
        var dll = Path.Combine(dir, "WinDivert.dll");
        var sys = Path.Combine(dir, "WinDivert64.sys");
        Touch(dll);
        Touch(sys);

        var monitor = Create(
            dll, sys,
            probe: () => (false, 2),
            install: _ => 0,
            elevated: true);

        var health = monitor.Check(attemptInstall: true);

        health.State.Should().Be(WinDivertHealthState.InstallFailed);
        health.Message.Should().Contain("kuruldu ama");
    }
}