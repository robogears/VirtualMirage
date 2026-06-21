using System.Runtime.InteropServices;
using VirtualMirage.Interop;

namespace VirtualMirage.Display;

/// <summary>
/// Windows CCD ("Connecting and Configuring Displays") P/Invoke surface and structs.
/// All structs are kept blittable (BOOL fields modeled as uint) so the path/mode
/// arrays can be round-tripped verbatim for snapshot/restore.
/// </summary>
internal static class CcdInterop
{
    // QueryDisplayConfig flags
    public const uint QDC_ALL_PATHS = 0x1;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    public const uint QDC_DATABASE_CURRENT = 0x4;
    public const uint QDC_VIRTUAL_MODE_AWARE = 0x10;
    public const uint QDC_VIRTUAL_REFRESH_RATE_AWARE = 0x40;

    // SetDisplayConfig flags
    public const uint SDC_TOPOLOGY_SUPPLIED = 0x10;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    public const uint SDC_VALIDATE = 0x40;
    public const uint SDC_APPLY = 0x80;
    public const uint SDC_NO_OPTIMIZATION = 0x100;
    public const uint SDC_SAVE_TO_DATABASE = 0x200;
    public const uint SDC_ALLOW_CHANGES = 0x400;
    public const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x800;
    public const uint SDC_FORCE_MODE_ENUMERATION = 0x1000;
    public const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000;
    public const uint SDC_VIRTUAL_MODE_AWARE = 0x8000;
    public const uint SDC_VIRTUAL_REFRESH_RATE_AWARE = 0x20000;
    // "Restore last known good for currently connected monitors"
    public const uint SDC_TOPOLOGY_INTERNAL = 0x1;
    public const uint SDC_TOPOLOGY_CLONE = 0x2;
    public const uint SDC_TOPOLOGY_EXTEND = 0x4;
    public const uint SDC_TOPOLOGY_EXTERNAL = 0x8;
    public const uint SDC_USE_DATABASE_CURRENT =
        SDC_TOPOLOGY_INTERNAL | SDC_TOPOLOGY_CLONE | SDC_TOPOLOGY_EXTEND | SDC_TOPOLOGY_EXTERNAL;

    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff;

    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE = 3;

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public enum DEVICE_INFO_TYPE : uint
    {
        GET_SOURCE_NAME = 1,
        GET_TARGET_NAME = 2,
        GET_TARGET_PREFERRED_MODE = 3,
        GET_ADAPTER_NAME = 4,
        GET_ADVANCED_COLOR_INFO = 9,
        SET_ADVANCED_COLOR_STATE = 10,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2DREGION { public uint cx; public uint cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx; // union with {cloneGroupId:16, sourceModeInfoIdx:16} when virtual-mode-aware
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx; // union with {desktopModeInfoIdx:16, targetModeInfoIdx:16} when virtual-mode-aware
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public uint targetAvailable;  // BOOL
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandardAndExtra; // AdditionalSignalInfo packed bitfield
        public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_MODE { public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
    {
        public POINTL PathSourceSize;
        public RECT DesktopImageRegion;
        public RECT DesktopImageClip;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        [FieldOffset(0)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags,
        out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags,
        ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(
        uint numPathArrayElements, [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements, [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        uint flags);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    /// <summary>Query paths+modes for the given QDC flags (retries on topology change between sizing and query).</summary>
    public static bool Query(uint flags, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
    {
        paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            int err = GetDisplayConfigBufferSizes(flags, out uint np, out uint nm);
            if (err != ERROR_SUCCESS) { Log.Warn($"GetDisplayConfigBufferSizes err={err}"); return false; }

            var p = new DISPLAYCONFIG_PATH_INFO[np];
            var m = new DISPLAYCONFIG_MODE_INFO[nm];
            err = QueryDisplayConfig(flags, ref np, p, ref nm, m, IntPtr.Zero);
            if (err == ERROR_SUCCESS)
            {
                Array.Resize(ref p, (int)np);
                Array.Resize(ref m, (int)nm);
                paths = p; modes = m;
                return true;
            }
            if (err != ERROR_INSUFFICIENT_BUFFER) { Log.Warn($"QueryDisplayConfig err={err}"); return false; }
            // else: topology changed between sizing and query — retry
        }
        return false;
    }

    public static bool QueryActive(uint extraFlags, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
        => Query(QDC_ONLY_ACTIVE_PATHS | extraFlags, out paths, out modes);

    /// <summary>All paths including currently-inactive ones (needed to find/activate a freshly hot-added display).</summary>
    public static bool QueryAll(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
        => Query(QDC_ALL_PATHS, out paths, out modes);

    /// <summary>Extend the desktop across all connected displays (activates a hot-added one the OS left off).</summary>
    public static bool ExtendAllDisplays()
    {
        int r = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
        Log.Info($"ExtendAllDisplays -> {r}");
        return r == ERROR_SUCCESS;
    }

    /// <summary>Resolve a target (adapterId, targetId) to its GDI source name (\\.\DISPLAYn), or null.</summary>
    public static string? FindGdiNameForTarget(LUID adapterId, uint targetId)
    {
        if (!QueryActive(0, out var paths, out _)) return null;

        foreach (var path in paths)
        {
            if (!path.targetInfo.adapterId.SameAs(adapterId) || path.targetInfo.id != targetId) continue;

            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            src.header.type = (uint)DEVICE_INFO_TYPE.GET_SOURCE_NAME;
            src.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            src.header.adapterId = path.sourceInfo.adapterId;
            src.header.id = path.sourceInfo.id;

            if (DisplayConfigGetDeviceInfo(ref src) == ERROR_SUCCESS)
                return src.viewGdiDeviceName;
        }
        return null;
    }

    /// <summary>Resolve a source (adapterId, sourceId) to its GDI name (\\.\DISPLAYn), or null.</summary>
    public static string? GetSourceGdiName(LUID adapterId, uint sourceId)
    {
        var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        src.header.type = (uint)DEVICE_INFO_TYPE.GET_SOURCE_NAME;
        src.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
        src.header.adapterId = adapterId;
        src.header.id = sourceId;
        return DisplayConfigGetDeviceInfo(ref src) == ERROR_SUCCESS ? src.viewGdiDeviceName : null;
    }

    /// <summary>Best-effort EDID friendly name for a target (used to recognize the virtual display).</summary>
    public static string? GetTargetFriendlyName(LUID adapterId, uint targetId)
    {
        var t = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        t.header.type = (uint)DEVICE_INFO_TYPE.GET_TARGET_NAME;
        t.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        t.header.adapterId = adapterId;
        t.header.id = targetId;
        return DisplayConfigGetDeviceInfo(ref t) == ERROR_SUCCESS ? t.monitorFriendlyDeviceName : null;
    }

    /// <summary>Stable monitor device path + friendly name for a target.</summary>
    public static (string devicePath, string friendly) GetTargetDeviceName(LUID adapterId, uint targetId)
    {
        var t = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        t.header.type = (uint)DEVICE_INFO_TYPE.GET_TARGET_NAME;
        t.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        t.header.adapterId = adapterId;
        t.header.id = targetId;
        return DisplayConfigGetDeviceInfo(ref t) == ERROR_SUCCESS
            ? (t.monitorDevicePath ?? "", t.monitorFriendlyDeviceName ?? "")
            : ("", "");
    }

    /// <summary>Map every active source GDI name to its target's (devicePath, friendlyName).</summary>
    public static Dictionary<string, (string devicePath, string friendly)> MapActiveSourceToTarget()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (!QueryActive(0, out var paths, out _)) return map;
        foreach (var p in paths)
        {
            string? gdi = GetSourceGdiName(p.sourceInfo.adapterId, p.sourceInfo.id);
            if (string.IsNullOrEmpty(gdi)) continue;
            map[gdi] = GetTargetDeviceName(p.targetInfo.adapterId, p.targetInfo.id);
        }
        return map;
    }

    /// <summary>Find the current active source GDI name for a stable monitor device path, or null.</summary>
    public static string? FindSourceForDevicePath(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return null;
        foreach (var kv in MapActiveSourceToTarget())
            if (string.Equals(kv.Value.devicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return null;
    }
}
