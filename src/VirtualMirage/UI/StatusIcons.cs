using System.Drawing;

namespace VirtualMirage.UI;

public enum AppStatus { Idle, Disabled, Connected, Active, Error }

/// <summary>Tray icons: the VirtualMirage logo badged with a status dot (color = state).</summary>
public static class StatusIcons
{
    private static readonly Dictionary<AppStatus, Icon> _cache = Build();

    public static Icon For(AppStatus s) => _cache.TryGetValue(s, out var i) ? i : _cache[AppStatus.Idle];

    private static Dictionary<AppStatus, Icon> Build() => new()
    {
        [AppStatus.Idle]      = IconArt.RenderStatusIcon(32, Color.FromArgb(150, 150, 150)),
        [AppStatus.Disabled]  = IconArt.RenderStatusIcon(32, Color.FromArgb(90, 90, 90)),
        [AppStatus.Connected] = IconArt.RenderStatusIcon(32, Color.FromArgb(60, 140, 235)),
        [AppStatus.Active]    = IconArt.RenderStatusIcon(32, Color.FromArgb(55, 200, 90)),
        [AppStatus.Error]     = IconArt.RenderStatusIcon(32, Color.FromArgb(225, 70, 60)),
    };
}
