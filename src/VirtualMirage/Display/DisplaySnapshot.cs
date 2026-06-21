using System.Runtime.InteropServices;
using System.Text.Json;

namespace VirtualMirage.Display;

/// <summary>Exact per-physical-monitor state, captured so refresh/position/primary restore faithfully.</summary>
public sealed class MonitorState
{
    public string GdiName { get; set; } = "";
    public string DevicePath { get; set; } = "";   // stable id (CCD monitorDevicePath)
    public string FriendlyName { get; set; } = "";
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint RefreshHz { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
    public uint BitsPerPel { get; set; }
    public bool IsPrimary { get; set; }

    public override string ToString() =>
        $"{GdiName} {Width}x{Height}@{RefreshHz} @({PosX},{PosY}){(IsPrimary ? " PRIMARY" : "")} [{FriendlyName}]";
}

/// <summary>
/// Serializable snapshot of the active display topology. The raw CCD path/mode
/// arrays are stored as base64 (they're blittable) so they can be re-applied
/// verbatim for an exact revert, plus a per-physical-monitor mode list (the
/// reliable path for restoring refresh/position/primary) and identifiers.
/// </summary>
public sealed class DisplaySnapshot
{
    public DateTime CapturedUtc { get; set; }
    public Guid VirtualMonitorGuid { get; set; }
    public uint QueryExtraFlags { get; set; }
    public uint NumPaths { get; set; }
    public uint NumModes { get; set; }
    public string PathsB64 { get; set; } = "";
    public string ModesB64 { get; set; } = "";
    public string? PrimaryGdiName { get; set; }
    public List<string> ActiveGdiNames { get; set; } = new();

    /// <summary>Exact state of each active PHYSICAL monitor (virtual displays excluded).</summary>
    public List<MonitorState> Monitors { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    internal static DisplaySnapshot Build(uint extraFlags,
        CcdInterop.DISPLAYCONFIG_PATH_INFO[] paths, CcdInterop.DISPLAYCONFIG_MODE_INFO[] modes,
        Guid virtualGuid, string? primaryGdi, List<string> activeGdi)
        => new()
        {
            CapturedUtc = DateTime.UtcNow,
            VirtualMonitorGuid = virtualGuid,
            QueryExtraFlags = extraFlags,
            NumPaths = (uint)paths.Length,
            NumModes = (uint)modes.Length,
            PathsB64 = Convert.ToBase64String(MemoryMarshal.AsBytes(paths.AsSpan()).ToArray()),
            ModesB64 = Convert.ToBase64String(MemoryMarshal.AsBytes(modes.AsSpan()).ToArray()),
            PrimaryGdiName = primaryGdi,
            ActiveGdiNames = activeGdi,
        };

    internal CcdInterop.DISPLAYCONFIG_PATH_INFO[] GetPaths()
        => MemoryMarshal.Cast<byte, CcdInterop.DISPLAYCONFIG_PATH_INFO>(Convert.FromBase64String(PathsB64).AsSpan()).ToArray();

    internal CcdInterop.DISPLAYCONFIG_MODE_INFO[] GetModes()
        => MemoryMarshal.Cast<byte, CcdInterop.DISPLAYCONFIG_MODE_INFO>(Convert.FromBase64String(ModesB64).AsSpan()).ToArray();

    public void SaveToDisk()
    {
        try
        {
            VirtualMirage.Paths.EnsureCreated();
            File.WriteAllText(VirtualMirage.Paths.StatePath, JsonSerializer.Serialize(this, JsonOpts));
            Log.Info($"Persisted session state ({NumPaths} paths) to {VirtualMirage.Paths.StatePath}.");
        }
        catch (Exception ex) { Log.Error("Failed to persist display snapshot", ex); }
    }

    public static DisplaySnapshot? LoadFromDisk()
    {
        try
        {
            return File.Exists(VirtualMirage.Paths.StatePath)
                ? JsonSerializer.Deserialize<DisplaySnapshot>(File.ReadAllText(VirtualMirage.Paths.StatePath))
                : null;
        }
        catch (Exception ex) { Log.Error("Failed to load display snapshot", ex); return null; }
    }

    public static void DeleteFromDisk()
    {
        try { if (File.Exists(VirtualMirage.Paths.StatePath)) File.Delete(VirtualMirage.Paths.StatePath); }
        catch (Exception ex) { Log.Error("Failed to delete display snapshot", ex); }
    }
}
