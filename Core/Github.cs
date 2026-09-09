using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace v1per_wpf;

/// <summary>GitHub API + download helpers (mirrors the v1per.py logic).</summary>
public static class Github
{
    private static readonly HttpClient Http = new();

    static Github()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("V1Per/1.2");
        Http.Timeout = TimeSpan.FromSeconds(180);
    }

    public const string KsuReleasesPage = "https://github.com/KernelSU-Next/KernelSU-Next/releases";
    public const string KsuReleasesUrl = "https://api.github.com/repos/KernelSU-Next/KernelSU-Next/releases/latest";
    public const string FolkReleasesPage = "https://github.com/LyraVoid/FolkPatch/releases";
    public const string FolkReleasesUrl = "https://api.github.com/repos/LyraVoid/FolkPatch/releases/latest";
    public const string KpReleasesUrl = "https://api.github.com/repos/bmax121/KernelPatch/releases/latest";

    public static async Task<JsonElement?> FetchLatest(string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Failed to fetch release: {ex.Message}");
            return null;
        }
    }

    /// <summary>First .apk asset (skipping "spoofed"), preferring any given substrings.</summary>
    public static JsonElement? PickApk(JsonElement release, params string[] prefer)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var n))
                continue;
            string name = n.GetString() ?? string.Empty;
            if (!name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Contains("spoofed", StringComparison.OrdinalIgnoreCase))
                continue;

            string lower = name.ToLowerInvariant();
            if (prefer.Length > 0 && prefer.Any(p => lower.Contains(p)))
                return asset;
            fallback ??= asset;
        }
        return fallback;
    }

    /// <summary>Finds an asset by exact name.</summary>
    public static JsonElement? PickAsset(JsonElement release, string exactName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var n))
                continue;
            if ((n.GetString() ?? string.Empty) == exactName)
                return asset;
        }
        return null;
    }

    public static string Name(JsonElement asset) =>
        asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;

    public static string Url(JsonElement asset) =>
        asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;

    public static string DownloadsHome()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? AppContext.BaseDirectory;
        string dir = Path.Combine(baseDir, "V1Per", "downloads");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Downloads a file (skipping existing ones) with a rough % progress.</summary>
    public static async Task<string?> DownloadAsync(string url, string filename)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        string dest = Path.Combine(DownloadsHome(), filename);
        if (File.Exists(dest) && new FileInfo(dest).Length > 0)
        {
            V1Per.Ui.Ok($"Already downloaded: {filename}");
            return dest;
        }

        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            await using var fs = File.Create(dest);
            await using var stream = await resp.Content.ReadAsStreamAsync();
            var buffer = new byte[81920];
            while (true)
            {
                int n = await stream.ReadAsync(buffer);
                if (n == 0)
                    break;
                await fs.WriteAsync(buffer.AsMemory(0, n));
            }
            return dest;
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Download failed: {ex.Message}");
            try
            {
                File.Delete(dest);
            }
            catch (Exception)
            {
                // ignore
            }
            return null;
        }
    }
}