using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VirtualMirage.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint LowPart;
    public int HighPart;

    public long ToInt64() => ((long)HighPart << 32) | LowPart;
    public static LUID FromInt64(long v) => new() { LowPart = unchecked((uint)v), HighPart = (int)(v >> 32) };
    public bool SameAs(LUID o) => LowPart == o.LowPart && HighPart == o.HighPart;
    public override string ToString() => $"{HighPart:X8}:{LowPart:X8}";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINTL { public int x; public int y; }

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int left; public int top; public int right; public int bottom; }

/// <summary>Shared kernel32 primitives used across the interop layers.</summary>
public static class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
