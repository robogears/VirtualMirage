using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VirtualMirage.Detection;

/// <summary>
/// Low-level Virtual Desktop session signals. None require admin.
///   - streamer process / shared-memory existence  => the Streamer is running (gate)
///   - LAN peer on the VD streaming ports           => a headset is connected (primary, non-invasive)
///   - VirtualDesktop.BodyStateEvent pulse          => live frames flowing (alt; auto-reset, mildly invasive)
/// The "right" combination is locked per-machine via DetectionDiagnostics.
/// </summary>
public static class VdSignals
{
    public const string StreamerProcessName = "VirtualDesktop.Streamer";
    public const string BodyStateMmf = "VirtualDesktop.BodyState";
    public const string BodyStateEvent = "VirtualDesktop.BodyStateEvent";

    public static readonly int[] DefaultPorts = { 38810, 38820, 38830, 38840 };

    public static bool IsStreamerRunning()
    {
        try { return Process.GetProcessesByName(StreamerProcessName).Length > 0; }
        catch { return false; }
    }

    /// <summary>The streamer publishes this shared memory; its existence ≈ streamer running (v1.30+).</summary>
    public static bool MmfExists()
    {
        foreach (var name in new[] { BodyStateMmf, "Global\\" + BodyStateMmf, "Local\\" + BodyStateMmf })
        {
            try { using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read); return true; }
            catch { /* not present in this namespace */ }
        }
        return false;
    }

    /// <summary>Open the body-state event and wait briefly for a pulse (auto-reset). True if it pulsed in time.</summary>
    public static bool TryEventPulse(int timeoutMs)
    {
        foreach (var name in new[] { BodyStateEvent, "Global\\" + BodyStateEvent, "Local\\" + BodyStateEvent })
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(name);
                return ev.WaitOne(timeoutMs);
            }
            catch { /* not present in this namespace */ }
        }
        return false;
    }

    public sealed record LanPeer(int LocalPort, string Remote);

    /// <summary>Established TCP connections on the VD ports whose remote is a private/LAN address (the headset).</summary>
    public static List<LanPeer> GetVdLanPeers(int[] ports)
    {
        var result = new List<LanPeer>();
        try
        {
            foreach (var c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
            {
                if (c.State != TcpState.Established) continue;
                if (Array.IndexOf(ports, c.LocalEndPoint.Port) < 0) continue;
                if (!IsPrivate(c.RemoteEndPoint.Address)) continue; // excludes the public cloud relay
                result.Add(new LanPeer(c.LocalEndPoint.Port, c.RemoteEndPoint.ToString()));
            }
        }
        catch (Exception ex) { Log.Error("GetVdLanPeers failed", ex); }
        return result;
    }

    public static bool HasLanPeer(int[] ports) => GetVdLanPeers(ports).Count > 0;

    public static bool IsPrivate(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return false;
        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            if (b[0] == 10) return true;                          // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;          // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;          // 169.254.0.0/16 link-local
            return false;
        }
        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal) return true;
            var b = addr.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;               // fc00::/7 unique-local
            return false;
        }
        return false;
    }
}
