using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2SweepProbe;

/// <summary>
/// Boot-diagnostics mode for the packaged dashboard: loads the REAL output
/// index.html (the exact document the app serves via the aogpn.local virtual
/// host) in a real WebView2 and captures:
///   - every renderer→host postMessage through the NATIVE WebView2 channel
///     (core.WebMessageReceived) — the page sees the real window.chrome.webview,
///     exactly like the packaged app, so postToHost behaves identically,
///   - every console message / JS exception via the DevTools protocol,
///   - a final state report (module registration, boot posts, body state).
///
/// This exists because the jsdom sandbox masks real-browser failures: it stubs
/// timers, sets __aogpnPerfDisabled, and serves files from the repo tree. The
/// packaged app runs the real timers/probe and serves from the output folder,
/// so a module that dies only there (e.g. a missing file or a browser-only API)
/// shows up here and nowhere else.
///
/// Usage: dotnet run --project AoGPN/WebView2SweepProbe -- --jscheck [--html &lt;output index.html&gt;]
/// </summary>
internal sealed class JsCheckForm : Form
{
    private const string HostName = "aogpn.local";

    private readonly string _htmlPath;
    private readonly bool _hiddenBoot;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly List<string> _console = [];
    private readonly List<string> _posts = [];

    public JsCheckForm(string htmlPath, bool hiddenBoot)
    {
        _htmlPath = htmlPath;
        _hiddenBoot = hiddenBoot;
        Text = "AoGPN dashboard JS boot check" + (hiddenBoot ? " (hidden boot)" : "");
        ClientSize = new Size(1000, 700);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(60, 60);
        Controls.Add(_webView);
        _webView.DefaultBackgroundColor = Color.FromArgb(0x0B, 0x0F, 0x19);
        if (hiddenBoot)
        {
            // Mirror the app exactly: the WebView2 control stays Visibility=Hidden
            // while the dashboard boots; the reveal flips it to Visible afterwards.
            _webView.Visible = false;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var contentRoot = Path.GetDirectoryName(_htmlPath)!;
            Console.WriteLine($"Loading {_htmlPath}");
            Console.WriteLine($"Content root  : {contentRoot}");

            var envOptions = new CoreWebView2EnvironmentOptions(
                "--disable-backgrounding-occluded-windows "
                + "--disable-renderer-backgrounding "
                + "--disable-background-timer-throttling");
            var userDataFolder = Path.Combine(Path.GetTempPath(), "AoGPN-JsCheck-WV2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions);
            await _webView.EnsureCoreWebView2Async(environment);
            var core = _webView.CoreWebView2!;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;

            // Hook the NATIVE message channel: the page's postToHost uses the real
            // window.chrome.webview, so every host-bound message lands here.
            core.WebMessageReceived += (_, args) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                    _posts.Add(doc.RootElement.TryGetProperty("action", out var a)
                        ? a.GetString() ?? args.WebMessageAsJson
                        : args.WebMessageAsJson);
                }
                catch
                {
                    _posts.Add(args.WebMessageAsJson);
                }
            };

            // DevTools capture: console errors, exceptions, CSP violations.
            var consoleReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
            consoleReceiver.DevToolsProtocolEventReceived += (_, args) =>
                _console.Add("consoleAPICalled: " + args.ParameterObjectAsJson);
            var logReceiver = core.GetDevToolsProtocolEventReceiver("Log.entryAdded");
            logReceiver.DevToolsProtocolEventReceived += (_, args) =>
                _console.Add("entryAdded: " + args.ParameterObjectAsJson);
            var exceptionReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
            exceptionReceiver.DevToolsProtocolEventReceived += (_, args) =>
                _console.Add("exceptionThrown: " + args.ParameterObjectAsJson);
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
                await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
            }
            catch { /* older runtimes */ }

            // window.onerror + unhandledrejection markers (page-side capture).
            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                window.__aogpnCheck = { errors: [], rejections: [], injected: true };
                window.addEventListener('error', function (ev) {
                  window.__aogpnCheck.errors.push((ev.message || '') + ' @ ' + (ev.filename || '') + ':' + (ev.lineno || 0));
                });
                window.addEventListener('unhandledrejection', function (ev) {
                  window.__aogpnCheck.rejections.push(String(ev.reason));
                });
                """);

            core.SetVirtualHostNameToFolderMapping(
                HostName, contentRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate($"https://{HostName}/{Path.GetFileName(_htmlPath)}");

            // Give the page time to boot: navigation + async chains (themes.json,
            // Dil/*.json, skins.json) + the boot-time renders.
            await Task.Delay(8_000);

            if (_hiddenBoot)
            {
                // "Reveal": same as the app's RevealStartupWindow — the control
                // becomes visible only now, after the seeds have been pushed.
                _webView.Visible = true;
                await Task.Delay(1_000);
            }

            var report = await core.ExecuteScriptAsync("""
                (() => {
                  const ck = window.__aogpnCheck || { errors: [], rejections: [], injected: false };
                  const mods = window.aogpn || {};
                  const skin = document.body ? document.body.getAttribute('data-skin') : null;
                  const appFrame = document.getElementById('appFrame');
                  const skinHost = document.getElementById('skinHost');
                  const cs = (el) => el ? getComputedStyle(el).display : 'missing';
                  return {
                    readyState: document.readyState,
                    injected: !!(ck.injected),
                    aogpnKeys: Object.keys(mods),
                    hasApplySettings: typeof window.applySettings === 'function',
                    hasUpdateTelemetry: typeof window.updateTelemetry === 'function',
                    hasSkinBridge: typeof window.skinBridge === 'object',
                    hasOpenSkinManager: typeof window.openSkinManager === 'function',
                    hasApplySkin: typeof window.applySkin === 'function',
                    dataSkin: skin,
                    dataTheme: document.body ? document.body.getAttribute('data-theme') : null,
                    appFrameDisplay: cs(appFrame),
                    skinHostDisplay: cs(skinHost),
                    bodyClasses: document.body ? document.body.className : '',
                    purpleElements: (() => {
                      const found = [];
                      const want = (c) => {
                        const m = c.match(/rgba?\\((\\d+),\\s*(\\d+),\\s*(\\d+)/);
                        if (!m) return false;
                        const r = +m[1], g = +m[2], b = +m[3];
                        return Math.abs(r - 165) < 10 && Math.abs(g - 76) < 10 && Math.abs(b - 208) < 10;
                      };
                      const check = (tag, el, which) => {
                        if (!el) return;
                        const s = getComputedStyle(el, which);
                        if (want(s.backgroundColor) || want(s.borderTopColor) || want(s.boxShadow)) {
                          const r = el.getBoundingClientRect();
                          found.push({ tag, which: which || 'self', bg: s.backgroundColor, bTop: s.borderTopColor, shadow: (s.boxShadow || '').slice(0, 80), rect: Math.round(r.top) + ',' + Math.round(r.height) + 'x' + Math.round(r.width) });
                        }
                      };
                      document.querySelectorAll('*').forEach((el) => {
                        const cs = getComputedStyle(el);
                        if (want(cs.backgroundColor) || want(cs.borderTopColor) || want(cs.boxShadow) || want(cs.backgroundImage)) {
                          const r = el.getBoundingClientRect();
                          if (r.width > 50 && r.height > 2 && r.top < 60) {
                            found.push({ tag: el.tagName, id: el.id || '', cls: (el.className || '').toString().slice(0, 60), bg: cs.backgroundColor, bTop: cs.borderTopColor, rect: Math.round(r.top) + ',' + Math.round(r.height) + 'x' + Math.round(r.width) });
                          }
                        }
                        check('pseudo-before', el, '::before');
                        check('pseudo-after', el, '::after');
                      });
                      return found.slice(0, 15);
                    })(),
                    fontStatus: (document.fonts && document.fonts.status) || 'n/a',
                    fontsLoaded: Array.from((document.fonts && document.fonts) || [])
                      .filter(function (f) { return f.status !== 'unloaded'; })
                      .map(function (f) { return f.family.replace(/['\"]/g, '') + ' ' + f.weight + ':' + f.status; })
                      .slice(0, 40),
                    pageErrors: ck.errors.slice(0, 20),
                    pageRejections: ck.rejections.slice(0, 20)
                  };
                })()
                """);
            Console.WriteLine("=== JS CHECK REPORT ===");
            Console.WriteLine(JsonSerializer.Serialize(
                JsonDocument.Parse(report).RootElement,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"=== NATIVE POSTS ({_posts.Count}) ===");
            foreach (var p in _posts)
            {
                Console.WriteLine("  " + p);
            }

            Console.WriteLine("=== DEVTOOLS CONSOLE/EXCEPTIONS ===");
            foreach (var c in _console)
            {
                Console.WriteLine("  " + c);
            }

            Console.WriteLine("=== END JS CHECK REPORT ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: {ex}");
        }
        finally
        {
            Close();
        }
    }
}