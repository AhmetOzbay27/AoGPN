namespace ServiceLib.Models.Dto;

public class TrafficMonitorItem
{
    public string Type { get; set; } = "";
    public string AppName { get; set; } = "";
    public string AppCountText { get; set; } = "";
    public string ExePath { get; set; } = "";
    public int ConnectionCount { get; set; }
    public long Download { get; set; }
    public long Upload { get; set; }

    /// <summary>
    /// Live destination addresses of the app's connections (from Mihomo
    /// <c>GET /connections</c> metadata), comma-separated, UDP entries first.
    /// Empty when the core reports no live connections.
    /// </summary>
    public string ActiveIps { get; set; } = "";
    public string DownloadText => Utils.HumanFy(Download);
    public string UploadText => Utils.HumanFy(Upload);
}
