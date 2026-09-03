namespace ServiceLib.Models.Dto;

/// <summary>
/// Safe, renderer-facing description of a running executable. It contains no
/// command line, environment, user token, or network data.
/// </summary>
public sealed record ProcessCatalogItem(
    int Pid,
    string ProcessName,
    string DisplayName,
    string ExePath,
    bool IsElevatedProcess);
