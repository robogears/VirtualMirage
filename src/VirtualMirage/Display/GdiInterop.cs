using System.Runtime.InteropServices;
using System.Text;

namespace VirtualMirage.Display;

/// <summary>
/// Legacy GDI display APIs (DEVMODE / EnumDisplaySettings / EnumDisplayDevices /
/// ChangeDisplaySettingsEx). Used for the simple "set mode" and "set primary"
/// operations (where the legacy API is much simpler than CCD) and for monitor
/// enumeration / diagnostics.
/// </summary>
internal static class GdiInterop
{
    public const int ENUM_CURRENT_SETTINGS = -1;

    // ChangeDisplaySettingsEx flags
    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint CDS_TEST = 0x00000002;
    public const uint CDS_FULLSCREEN = 0x00000004;
    public const uint CDS_GLOBAL = 0x00000008;
    public const uint CDS_SET_PRIMARY = 0x00000010;
    public const uint CDS_NORESET = 0x10000000;
    public const uint CDS_RESET = 0x40000000;

    // DEVMODE.dmFields
    public const uint DM_BITSPERPEL = 0x00040000;
    public const uint DM_PELSWIDTH = 0x00080000;
    public const uint DM_PELSHEIGHT = 0x00100000;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint DM_POSITION = 0x00000020;

    // ChangeDisplaySettingsEx return codes
    public const int DISP_CHANGE_SUCCESSFUL = 0;
    public const int DISP_CHANGE_RESTART = 1;
    public const int DISP_CHANGE_FAILED = -1;
    public const int DISP_CHANGE_BADMODE = -2;
    public const int DISP_CHANGE_NOTUPDATED = -3;
    public const int DISP_CHANGE_BADFLAGS = -4;
    public const int DISP_CHANGE_BADPARAM = -5;
    public const int DISP_CHANGE_BADDUALVIEW = -6;

    // DISPLAY_DEVICE.StateFlags
    public const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;
    public const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
    public const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        public int dmPositionX;   // POINTL dmPosition (display union)
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, ref DEVMODE lpDevMode,
        IntPtr hwnd, uint dwflags, IntPtr lParam);

    // Overload for the "commit" call: ChangeDisplaySettingsEx(NULL, NULL, ...).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, IntPtr lpDevMode,
        IntPtr hwnd, uint dwflags, IntPtr lParam);

    public static DEVMODE NewDevMode()
    {
        var dm = new DEVMODE { dmDeviceName = string.Empty, dmFormName = string.Empty };
        dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        return dm;
    }

    /// <summary>Read a display's current mode (seeds private driver fields too).</summary>
    public static bool TryGetCurrentMode(string deviceName, out DEVMODE mode)
    {
        mode = NewDevMode();
        return EnumDisplaySettingsW(deviceName, ENUM_CURRENT_SETTINGS, ref mode);
    }

    /// <summary>Human-readable dump of all GDI display devices and their current modes (for logs/diagnostics).</summary>
    public static string DescribeAll()
    {
        var sb = new StringBuilder();
        var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            bool active = (dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0;
            bool primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            string mode = "(inactive)";
            if (active && TryGetCurrentMode(dd.DeviceName, out var dm))
                mode = $"{dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency} @({dm.dmPositionX},{dm.dmPositionY})";

            sb.Append($"  {dd.DeviceName} [{(active ? "active" : "off")}{(primary ? ",PRIMARY" : "")}] {dd.DeviceString.Trim()} -> {mode}\n");
            dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>True if any ACTIVE display's adapter description contains <paramref name="substr"/> (e.g. "SudoMaker").</summary>
    public static bool HasActiveDisplayMatching(string substr)
    {
        var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0 &&
                dd.DeviceString.IndexOf(substr, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return false;
    }

    public static int ActiveMonitorCount()
    {
        int n = 0;
        var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0) n++;
            dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        }
        return n;
    }
}
