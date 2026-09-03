namespace ServiceLib.Models.Configs;

[Serializable]
public class Config
{
    #region property

    public string IndexId { get; set; }
    public string SubIndexId { get; set; }

    #endregion property

    #region other entities

    public CoreBasicItem CoreBasicItem { get; set; }
    public TunModeItem TunModeItem { get; set; }
    public KcpItem KcpItem { get; set; }
    public GrpcItem GrpcItem { get; set; }
    public RoutingBasicItem RoutingBasicItem { get; set; }
    public GUIItem GuiItem { get; set; }
    public MsgUIItem MsgUIItem { get; set; }
    public ConnectionSettingsItem ConnectionItem { get; set; }
    public UIItem UiItem { get; set; }
    public ConstItem ConstItem { get; set; }
    public SpeedTestItem SpeedTestItem { get; set; }
    public Mux4RayItem Mux4RayItem { get; set; }
    public Mux4SboxItem Mux4SboxItem { get; set; }
    public HysteriaItem HysteriaItem { get; set; }
    public ClashUIItem ClashUIItem { get; set; }
    public SystemProxyItem SystemProxyItem { get; set; }
    public WebDavItem WebDavItem { get; set; }
    public CheckUpdateItem CheckUpdateItem { get; set; }
    public Fragment4RayItem? Fragment4RayItem { get; set; }
    public List<InItem> Inbound { get; set; }
    public List<KeyEventItem> GlobalHotkeys { get; set; }
    public List<CoreTypeItem> CoreTypeItem { get; set; }
    public SimpleDNSItem SimpleDNSItem { get; set; }
    public HappyEyeballs4RayItem HappyEyeballs4RayItem { get; set; }

    /// <summary>WinDivert yakalama (OpenEx kuyruk/katman) ayarları — GPN paket yakalama döngüsü için.</summary>
    public GpnCaptureItem GpnCaptureItem { get; set; }

    /// <summary>Wintun adaptörü (adapter ad ön eki + halka tampon kapasitesi) ayarları — GPN tünel köprüsü için.</summary>
    public GpnWintunItem GpnWintunItem { get; set; }

    /// <summary>
    /// IndexIds of profiles moved to the AoGPN dashboard's Disabled section.
    /// Kept in the JSON config so the flag survives restarts without a schema
    /// change; native AoGPN operations leave the profiles themselves intact.
    /// </summary>
    public List<string> DisabledIndexIds { get; set; }

    /// <summary>
    /// Node pool: URLs (GitHub raw .txt lists, base64 or plain subscription
    /// endpoints) whose shared nodes can be pulled into the node list with a
    /// single "download new nodes" action. Kept in the JSON config so the pool
    /// survives restarts without touching the subscription model.
    /// </summary>
    public List<string> NodePoolLinks { get; set; } = [];

    #endregion other entities
}
