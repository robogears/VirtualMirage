using System.Runtime.InteropServices;
using System.Text;
using VirtualMirage.Display;
using VirtualMirage.Interop;
using Microsoft.Win32.SafeHandles;

namespace VirtualMirage.VirtualDisplay;

/// <summary>Identifiers for a created virtual display.</summary>
public readonly record struct VirtualDisplayHandle(string GdiName, LUID AdapterLuid, uint TargetId, Guid MonitorGuid)
{
    public override string ToString() => $"{GdiName} (target {TargetId}, adapter {AdapterLuid}, guid {MonitorGuid})";
}

/// <summary>
/// Owns the SUDOVDA device handle and drives create/remove of the virtual display
/// plus the watchdog keep-alive. Layer A of the Apollo-style design.
/// </summary>
public sealed class SudoVdaController : IDisposable
{
    private readonly object _gate = new();
    private SafeFileHandle? _handle;
    private WatchdogPinger? _pinger;

    public bool IsOpen
    {
        get { lock (_gate) return _handle is { IsInvalid: false, IsClosed: false }; }
    }

    /// <summary>Open the device and verify protocol compatibility. Returns false if SUDOVDA isn't present.</summary>
    public bool Open()
    {
        lock (_gate)
        {
            if (_handle is { IsInvalid: false, IsClosed: false }) return true;

            var h = SudoVdaInterop.OpenDevice();
            if (h is null || h.IsInvalid)
            {
                Log.Error("SUDOVDA device not found / could not be opened. Is the SudoMaker Virtual Display Adapter installed and started?");
                return false;
            }
            _handle = h;

            if (TryGetProtocolVersion(out var v))
            {
                Log.Info($"SUDOVDA protocol version {v.Major}.{v.Minor}.{v.Incremental} (test={v.TestBuild != 0}).");
                if (v.Major != SudoVdaInterop.ExpectedMajor)
                    Log.Warn($"SUDOVDA protocol major {v.Major} != expected {SudoVdaInterop.ExpectedMajor}; proceeding but ADD/REMOVE layout may differ.");
            }
            else
            {
                Log.Warn("Could not read SUDOVDA protocol version (continuing).");
            }
            return true;
        }
    }

    internal bool TryGetProtocolVersion(out SudoVdaInterop.PROTOCOL_VERSION version)
    {
        version = default;
        lock (_gate)
        {
            if (_handle is null) return false;
            return SudoVdaInterop.IoctlOut(_handle, SudoVdaInterop.IOCTL_GET_PROTOCOL_VERSION, out version);
        }
    }

    internal bool TryGetWatchdog(out SudoVdaInterop.WATCHDOG_OUT watchdog)
    {
        watchdog = default;
        lock (_gate)
        {
            if (_handle is null) return false;
            return SudoVdaInterop.IoctlOut(_handle, SudoVdaInterop.IOCTL_GET_WATCHDOG, out watchdog);
        }
    }

    public bool Ping()
    {
        lock (_gate)
        {
            if (_handle is null || _handle.IsInvalid) return false;
            return SudoVdaInterop.IoctlNone(_handle, SudoVdaInterop.IOCTL_PING);
        }
    }

    public bool SetRenderAdapter(LUID adapter)
    {
        lock (_gate)
        {
            if (_handle is null) return false;
            var p = new SudoVdaInterop.SET_RENDER_ADAPTER_PARAMS { AdapterLuid = adapter };
            bool ok = SudoVdaInterop.IoctlIn(_handle, SudoVdaInterop.IOCTL_SET_RENDER_ADAPTER, p);
            Log.Info($"SetRenderAdapter({adapter}) -> {ok}");
            return ok;
        }
    }

    /// <summary>
    /// Create (plug) the virtual display at the requested mode and resolve its \\.\DISPLAYn name.
    /// Starts the watchdog ping thread. <paramref name="onWatchdogFail"/> fires if pings stop succeeding.
    /// </summary>
    public VirtualDisplayHandle? CreateDisplay(uint width, uint height, uint refreshHz, Guid monitorGuid,
        string deviceName, string serialNumber, LUID? renderAdapter, Action onWatchdogFail)
    {
        lock (_gate)
        {
            if (_handle is null && !Open()) return null;
            if (_handle is null) return null;

            if (renderAdapter is { } ra) SetRenderAdapter(ra);

            var p = new SudoVdaInterop.ADD_PARAMS
            {
                Width = width,
                Height = height,
                RefreshRate = refreshHz,
                MonitorGuid = monitorGuid,
            };
            unsafe
            {
                WriteFixed(p.DeviceName, 14, deviceName);
                WriteFixed(p.SerialNumber, 14, serialNumber);
            }

            if (!SudoVdaInterop.IoctlInOut(_handle, SudoVdaInterop.IOCTL_ADD, p, out SudoVdaInterop.ADD_OUT outp))
            {
                int err = Marshal.GetLastWin32Error();
                Log.Error($"IOCTL_ADD failed (win32 {err}).");
                return null;
            }
            Log.Info($"Virtual display added: adapter={outp.AdapterLuid}, targetId={outp.TargetId}, guid={monitorGuid}.");

            // Start the watchdog keep-alive before anything else can go wrong.
            StartWatchdog(onWatchdogFail);

            // Best-effort quick resolve of the GDI name (fast path when the display auto-activates).
            // If it came up inactive (Windows remembered it off), the name stays empty here and the
            // caller's Apply step activates + resolves it. Keep this short to avoid blackout delay.
            string? gdiName = null;
            int interval = 20;
            for (int tries = 0; tries < 4; tries++)
            {
                gdiName = CcdInterop.FindGdiNameForTarget(outp.AdapterLuid, outp.TargetId);
                if (!string.IsNullOrEmpty(gdiName)) break;
                Thread.Sleep(interval);
                interval = Math.Min(interval * 2, 320);
            }

            if (string.IsNullOrEmpty(gdiName))
            {
                Log.Info("Virtual display added but came up inactive; it will be activated during Apply.");
                return new VirtualDisplayHandle(string.Empty, outp.AdapterLuid, outp.TargetId, monitorGuid);
            }

            Log.Info($"Virtual display resolved to {gdiName}.");
            return new VirtualDisplayHandle(gdiName, outp.AdapterLuid, outp.TargetId, monitorGuid);
        }
    }

    public bool RemoveDisplay(Guid monitorGuid)
    {
        lock (_gate)
        {
            StopWatchdog();
            if (_handle is null) return false;
            var p = new SudoVdaInterop.REMOVE_PARAMS { MonitorGuid = monitorGuid };
            bool ok = SudoVdaInterop.IoctlIn(_handle, SudoVdaInterop.IOCTL_REMOVE, p);
            Log.Info($"Virtual display removed (guid {monitorGuid}) -> {ok}.");
            return ok;
        }
    }

    private void StartWatchdog(Action onFail)
    {
        StopWatchdog();
        int intervalMs = 1000;
        if (TryGetWatchdog(out var wd) && wd.Timeout > 0)
        {
            intervalMs = (int)(wd.Timeout * 1000 / 3);
            Log.Info($"SUDOVDA watchdog timeout {wd.Timeout}s; pinging every {intervalMs}ms.");
        }
        else
        {
            Log.Info("SUDOVDA watchdog disabled or unreadable; pinging at 1s anyway.");
        }
        _pinger = new WatchdogPinger(Ping, intervalMs, onFail);
        _pinger.Start();
    }

    private void StopWatchdog()
    {
        _pinger?.Dispose();
        _pinger = null;
    }

    private static unsafe void WriteFixed(byte* dst, int cap, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s ?? string.Empty);
        int n = Math.Min(bytes.Length, cap - 1); // always leave a NUL terminator
        for (int i = 0; i < n; i++) dst[i] = bytes[i];
        for (int i = n; i < cap; i++) dst[i] = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopWatchdog();
            try { _handle?.Dispose(); } catch { }
            _handle = null;
        }
    }
}
