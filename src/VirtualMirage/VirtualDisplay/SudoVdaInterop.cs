using System.Runtime.InteropServices;
using VirtualMirage.Interop;
using Microsoft.Win32.SafeHandles;

namespace VirtualMirage.VirtualDisplay;

/// <summary>
/// P/Invoke surface for the SUDOVDA driver (SudoMaker Virtual Display Adapter).
/// Definitions mirror SudoVDA/Common/Include/sudovda-ioctl.h verbatim
/// (interface GUID, IOCTL codes, struct layouts).
/// </summary>
internal static class SudoVdaInterop
{
    public static readonly Guid InterfaceGuid = new("e5bcc234-1e0c-418a-a0d4-ef8b7501414d");
    public const string HardwareId = "root\\sudomaker\\sudovda";

    // CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, fn, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    private const uint FILE_DEVICE_UNKNOWN = 0x22;
    private static uint Ctl(uint fn) => (FILE_DEVICE_UNKNOWN << 16) | (fn << 2);

    public static readonly uint IOCTL_ADD                  = Ctl(0x800); // 0x222000
    public static readonly uint IOCTL_REMOVE               = Ctl(0x801); // 0x222004
    public static readonly uint IOCTL_SET_RENDER_ADAPTER   = Ctl(0x802); // 0x222008
    public static readonly uint IOCTL_GET_WATCHDOG         = Ctl(0x803); // 0x22200C
    public static readonly uint IOCTL_PING                 = Ctl(0x888); // 0x222220
    public static readonly uint IOCTL_GET_PROTOCOL_VERSION = Ctl(0x8FF); // 0x2223FC

    // Protocol version this build was written against (sudovda-ioctl.h: {0,2,1,true}).
    public const byte ExpectedMajor = 0;
    public const byte ExpectedMinor = 2;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public unsafe struct ADD_PARAMS
    {
        public uint Width;
        public uint Height;
        public uint RefreshRate;       // integer Hz
        public Guid MonitorGuid;
        public fixed byte DeviceName[14];
        public fixed byte SerialNumber[14];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ADD_OUT { public LUID AdapterLuid; public uint TargetId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct REMOVE_PARAMS { public Guid MonitorGuid; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SET_RENDER_ADAPTER_PARAMS { public LUID AdapterLuid; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WATCHDOG_OUT { public uint Timeout; public uint Countdown; } // seconds

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PROTOCOL_VERSION { public byte Major; public byte Minor; public byte Incremental; public byte TestBuild; }

    // ---- DeviceIoControl helpers (structs are blittable / unmanaged) ----

    public static unsafe bool IoctlInOut<TIn, TOut>(SafeFileHandle h, uint code, TIn input, out TOut output)
        where TIn : unmanaged where TOut : unmanaged
    {
        TOut local = default;
        bool ok = Native.DeviceIoControl(h, code, (IntPtr)(&input), (uint)sizeof(TIn),
            (IntPtr)(&local), (uint)sizeof(TOut), out _, IntPtr.Zero);
        output = local;
        return ok;
    }

    public static unsafe bool IoctlIn<TIn>(SafeFileHandle h, uint code, TIn input) where TIn : unmanaged
        => Native.DeviceIoControl(h, code, (IntPtr)(&input), (uint)sizeof(TIn), IntPtr.Zero, 0, out _, IntPtr.Zero);

    public static unsafe bool IoctlOut<TOut>(SafeFileHandle h, uint code, out TOut output) where TOut : unmanaged
    {
        TOut local = default;
        bool ok = Native.DeviceIoControl(h, code, IntPtr.Zero, 0, (IntPtr)(&local), (uint)sizeof(TOut), out _, IntPtr.Zero);
        output = local;
        return ok;
    }

    public static bool IoctlNone(SafeFileHandle h, uint code)
        => Native.DeviceIoControl(h, code, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    // ---- SetupAPI device-interface enumeration ----

    public const uint DIGCF_PRESENT = 0x2;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(in Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        in Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    /// <summary>Locate and open the SUDOVDA device interface. Returns null if not found/openable.</summary>
    public static SafeFileHandle? OpenDevice()
    {
        Guid g = InterfaceGuid;
        IntPtr devInfo = SetupDiGetClassDevs(in g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == Native.INVALID_HANDLE_VALUE) return null;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
            };

            for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, in g, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifData, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                if (reqSize == 0) continue;

                IntPtr detail = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86. Path begins at +4.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifData, detail, reqSize, out _, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringUni(detail + 4);
                        if (!string.IsNullOrEmpty(path))
                        {
                            var handle = Native.CreateFileW(path,
                                Native.GENERIC_READ | Native.GENERIC_WRITE,
                                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                            if (!handle.IsInvalid) return handle;
                            handle.Dispose();
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(devInfo); }
    }
}
