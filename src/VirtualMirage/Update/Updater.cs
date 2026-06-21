using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace VirtualMirage.Update;

public enum UpdateStatus { UpToDate, Available, Error }

public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string? Version = null,
    string? DownloadUrl = null,
    string? ReleaseUrl = null,
    string? Message = null);

/// <summary>
/// In-app self-updater (adapted from the global updater.md Electron pattern to .NET):
/// poll GitHub Releases, compare versions, stream-download the single-file win-x64 build,
/// and swap-and-relaunch via a detached .cmd (the running .exe can't replace itself).
/// </summary>
public static class Updater
{
    // NOTE: change these if the repo is renamed/moved.
    public const string Owner = "robogears";
    public const string Repo = "VirtualMirage";

    /// <summary>Substring identifying the installer asset in a release (the setup we run to update).</summary>
    public const string AssetSubstring = "Setup.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) // follows GitHub->S3 redirects
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"VirtualMirage/{CurrentVersion()}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>Numeric semver-ish comparison (strips leading v; '0.1.10' &gt; '0.1.2').</summary>
    public static bool IsNewer(string remote, string current)
    {
        int[] r = Parse(remote), c = Parse(current);
        int n = Math.Max(r.Length, c.Length);
        for (int i = 0; i < n; i++)
        {
            int a = i < r.Length ? r[i] : 0, b = i < c.Length ? c[i] : 0;
            if (a != b) return a > b;
        }
        return false;
    }

    private static int[] Parse(string v) =>
        v.TrimStart('v', 'V').Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();

    public static bool CanSelfInstall() => OperatingSystem.IsWindows();

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateStatus.Error, Message: $"GitHub HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(tag)) return new UpdateCheckResult(UpdateStatus.Error, Message: "No tag_name in release");

            string current = CurrentVersion();
            if (!IsNewer(tag, current)) return new UpdateCheckResult(UpdateStatus.UpToDate, Version: current);

            string? releaseUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            string? downloadUrl = releaseUrl; // fallback: open the release page
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    if (name is not null && name.Contains(AssetSubstring, StringComparison.OrdinalIgnoreCase) &&
                        a.TryGetProperty("browser_download_url", out var bu))
                    {
                        downloadUrl = bu.GetString();
                        break;
                    }
                }
            }

            Log.Info($"Update available: {tag} (current {current}).");
            return new UpdateCheckResult(UpdateStatus.Available, tag, downloadUrl, releaseUrl);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return new UpdateCheckResult(UpdateStatus.Error, Message: ex.Message);
        }
    }

    /// <summary>Stream the asset to a temp file, reporting (downloaded, total) bytes. Returns the path or null.</summary>
    public static async Task<string?> DownloadAsync(string url,
        IProgress<(long downloaded, long total)>? progress, CancellationToken ct = default)
    {
        string dest = Path.Combine(Path.GetTempPath(), $"VirtualMirage-update-{DateTime.Now:yyyyMMddHHmmss}.exe");
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);

            var buf = new byte[1 << 16];
            long got = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                got += read;
                progress?.Report((got, total));
            }
            Log.Info($"Downloaded update to {dest} ({got / 1024 / 1024} MB).");
            return dest;
        }
        catch (Exception ex)
        {
            Log.Error("Update download failed", ex);
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Launch the downloaded installer silently to apply the update. The per-machine setup auto-elevates
    /// (UAC), closes the running app (CloseApplications), installs, and relaunches it de-elevated. The
    /// caller exits the app on success. Returns false if the installer couldn't be launched (e.g. the
    /// user declined the UAC prompt) so the caller can leave the "Restart to apply" action available.
    /// </summary>
    public static bool ApplyUpdate(string setupPath)
    {
        try
        {
            Log.Info($"Launching installer to apply update: {setupPath}");
            Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true, // lets the setup's admin manifest raise the UAC prompt
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to launch the update installer (UAC declined?).", ex);
            return false;
        }
    }
}
