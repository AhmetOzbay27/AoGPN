using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AoGPN.Services;

/// <summary>
/// Owns the WebView2 document host for the packaged dashboard. The controller
/// remains responsible for interpreting actions; this class owns browser setup,
/// origin isolation, and teardown.
/// </summary>
public sealed class DashboardHost : IAsyncDisposable
{
    private readonly WebView2 _webView;
    private readonly string _contentRoot;
    private bool _disposed;

    public DashboardHost(WebView2 webView, string contentRoot)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _contentRoot = Path.GetFullPath(contentRoot ?? throw new ArgumentNullException(nameof(contentRoot)));
    }

    public bool IsReady { get; private set; }
    public CoreWebView2? CoreWebView2 => _webView.CoreWebView2;

    public event EventHandler<CoreWebView2WebMessageReceivedEventArgs>? WebMessageReceived;
    public event EventHandler<CoreWebView2NavigationCompletedEventArgs>? NavigationCompleted;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var dashboardPath = Path.Combine(_contentRoot, "index.html");
        if (!File.Exists(dashboardPath))
        {
            throw new FileNotFoundException("The packaged AoGPN dashboard was not found.", dashboardPath);
        }

        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x0B, 0x0F, 0x19);
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AoGPN",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        // The boot window is parked OFF-SCREEN (see App.OnStartup) and the WebView2
        // control is Visible the whole time, so Chromium never treats the dashboard
        // as a background page and the load is never throttled (a Hidden controller
        // stretches the load from ~0.4 s to ~2 s+ and delays first paint). The
        // switches below are the second half of that guarantee: even when the
        // parked window is reported occluded, the renderer keeps working and
        // composites its first frame before the reveal moves the window on screen.
        var environmentOptions = new CoreWebView2EnvironmentOptions(
            additionalBrowserArguments: "--disable-backgrounding-occluded-windows "
                + "--disable-renderer-backgrounding "
                + "--disable-background-timer-throttling");
        var environment = await CoreWebView2Environment.CreateAsync(
            null, userDataFolder, environmentOptions);
        cancellationToken.ThrowIfCancellationRequested();
        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 did not create CoreWebView2.");

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(
            "aogpn.local",
            _contentRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += OnNavigationCompleted;
        core.Navigate("https://aogpn.local/index.html");
    }

    public async Task ExecuteScriptAsync(string script)
    {
        if (_disposed || !IsReady || _webView.CoreWebView2 is null)
        {
            return;
        }

        await _webView.ExecuteScriptAsync(script);
    }

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!_disposed)
        {
            WebMessageReceived?.Invoke(this, args);
        }
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        IsReady = args.IsSuccess;
        if (!_disposed)
        {
            NavigationCompleted?.Invoke(this, args);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsReady = false;

        // WebView2 teardown'ı COM 0x8007139F ("group or resource not in the
        // correct state") veya "CoreWebView2 members cannot be accessed after
        // the WebView2 control is disposed" atabilir: tarayıcı süreci kapanırken
        // RPC çağrıları başarısız olur. Bu hatalar kapatma yolundan asla dışarı
        // sızmamalı — sızarsa WPF unhandled-exception çökmesi olur (exit code
        // 0xffffffff) ve tüm kapanış temizliği yarıda kalır.
        try
        {
            if (_webView.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnWebMessageReceived;
                core.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        catch (Exception ex) when (
            ex is COMException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            // WebView2 zaten yıkılmış — abonelik bırakma başarısız olabilir.
        }

        try
        {
            _webView.Dispose();
        }
        catch (Exception ex) when (
            ex is COMException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            // Tarayıcı süreci teardown sırasında kapanmış — yut.
        }

        await ValueTask.CompletedTask;
    }
}
