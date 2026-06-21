using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualMirage;

public sealed class ResolutionConfig
{
    public int Width { get; set; } = 3840;
    public int Height { get; set; } = 2160;
    public int RefreshHz { get; set; } = 120;

    public override string ToString() => $"{Width}x{Height}@{RefreshHz}";
}

public sealed class DetectionConfig
{
    /// <summary>auto | event | ports. The streamer-process gate always applies.</summary>
    public string Mode { get; set; } = "auto";
    public int DebounceConnectMs { get; set; } = 1500;
    public int DebounceDisconnectMs { get; set; } = 3000;
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>VD streamer TCP/UDP ports used by the LAN-peer fallback signal.</summary>
    public int[] StreamerPorts { get; set; } = { 38810, 38820, 38830, 38840, 38850 };
}

/// <summary>User configuration, persisted to %AppData%\VirtualMirage\config.json.</summary>
public sealed class Config
{
    public bool AutomationEnabled { get; set; } = true;

    public ResolutionConfig Display { get; set; } = new();

    /// <summary>Stable identity for the virtual monitor so Windows remembers its layout/scaling.</summary>
    public Guid MonitorGuid { get; set; } = Guid.Empty;

    /// <summary>Passed to SUDOVDA (CHAR[14] => max 13 chars, truncated at the interop boundary).</summary>
    public string DeviceName { get; set; } = "VirtualMirage";
    public string SerialNumber { get; set; } = "AVRVD-0001";

    public bool SetPrimary { get; set; } = true;
    public bool DisableOtherMonitors { get; set; } = true;

    /// <summary>GPU LUID to render the virtual display on (null = SUDOVDA default). Stored as Int64(LUID).</summary>
    public long? RenderAdapterLuid { get; set; }
    public string? RenderAdapterName { get; set; }

    public DetectionConfig Detection { get; set; } = new();

    public bool SkipIfApolloActive { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool UpdateCheckOnLaunch { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(Paths.ConfigPath), JsonOpts);
                if (cfg is not null)
                {
                    bool changed = cfg.EnsureDefaults();
                    if (changed) cfg.Save();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load config; falling back to defaults", ex);
        }

        var fresh = new Config();
        fresh.EnsureDefaults();
        fresh.Save();
        return fresh;
    }

    /// <returns>true if a default had to be filled in (caller should persist).</returns>
    public bool EnsureDefaults()
    {
        bool changed = false;
        if (MonitorGuid == Guid.Empty) { MonitorGuid = Guid.NewGuid(); changed = true; }
        if (string.IsNullOrWhiteSpace(DeviceName)) { DeviceName = "VirtualMirage"; changed = true; }
        if (string.IsNullOrWhiteSpace(SerialNumber)) { SerialNumber = "AVRVD-0001"; changed = true; }
        Display ??= new ResolutionConfig();
        Detection ??= new DetectionConfig();
        return changed;
    }

    public void Save()
    {
        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(Paths.ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save config", ex);
        }
    }
}
