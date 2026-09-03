namespace ServiceLib.Models.Entities;

[Serializable]
public class ProfileExItem
{
    [PrimaryKey]
    public string IndexId { get; set; }

    public int Delay { get; set; }
    public decimal Speed { get; set; }
    public int Sort { get; set; }
    public string? Message { get; set; }
    public string? IpInfo { get; set; }

    /// <summary>User-flagged favorite node (dashboard star).</summary>
    public bool IsFav { get; set; }

    /// <summary>Unix milliseconds of the last time this profile was selected as the active node.</summary>
    public long LastUsed { get; set; }
}
