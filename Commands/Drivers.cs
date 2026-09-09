using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace v1per_wpf;

public sealed class DriverEntry
{
    public string Category = "";
    public string Tag = "";
    public string Name = "";
    public string Description = "";
    public string Url = "";
    public bool Recommended;
}

/// <summary>
/// Driver downloader: reads the TTSD driver index, lets the user arrow-select
/// a driver and streams the archive straight into the Downloads folder.
/// </summary>
public static class Drivers
{
    private const string SourceUrl = "https://raw.githubusercontent.com/CIPHERCHAN/TTSD-ProductData/main/drivers.json";
    private static readonly HttpClient Http = new();

    static Drivers()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("V1Per/1.2");
        Http.Timeout = TimeSpan.FromSeconds(60);
    }

    public static async Task Run(string[] args)
    {
        V1Per.Ui.Rule("Driver downloads");

        string? filter = args.Length > 0 ? args[0].Trim().ToUpperInvariant() : null;
        if (!string.IsNullOrEmpty(filter))
            V1Per.Ui.Info($"Filtering by tag: {filter}");

        V1Per.Ui.Info("Fetching driver list...");
        var list = await FetchAsync();
        if (list is null || list.Count == 0)
        {
            V1Per.Ui.Err("Failed to load driver list. Check your internet.");
            return;
        }

        if (!string.IsNullOrEmpty(filter))
            list = list.Where(d => d.Tag.Equals(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0)
        {
            V1Per.Ui.Err("No drivers match that tag.");
            return;
        }

        var labels = list.Select(d =>
            $"[{d.Tag}] {d.Name}" + (d.Recommended ? " (recommended)" : "")).ToList();

        V1Per.Ui.Muted("Pick a driver to download.");
        int idx = await Dialogs.PickAsync($"Select a driver to download ({list.Count} total)", labels);
        if (idx < 0)
        {
            V1Per.Ui.Muted("Cancelled.");
            return;
        }

        var chosen = list[idx];
        V1Per.Ui.Rule("Selected driver");
        V1Per.Ui.Info($"Name: {chosen.Name}");
        if (chosen.Description.Length > 0)
            V1Per.Ui.Muted(chosen.Description);

        string downloads = DownloadsFolder();
        Directory.CreateDirectory(downloads);
        string fileName = FileNameFromUrl(chosen.Url);
        string dest = Path.Combine(downloads, fileName);

        if (File.Exists(dest) && new FileInfo(dest).Length > 0)
        {
            V1Per.Ui.Ok($"Already in Downloads: {dest}");
            return;
        }

        V1Per.Ui.Info($"Downloading {fileName} → {dest}");
        bool ok = await DownloadAsync(chosen.Url, dest);
        if (!ok)
        {
            V1Per.Ui.Err("Download failed.");
            return;
        }
        V1Per.Ui.Ok($"Saved: {dest}");
    }

    private static async Task<List<DriverEntry>?> FetchAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(SourceUrl));
            if (!doc.RootElement.TryGetProperty("drivers", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<DriverEntry>();
            foreach (var item in arr.EnumerateArray())
            {
                var d = new DriverEntry
                {
                    Category = Str(item, "category"),
                    Tag = Str(item, "tag"),
                    Name = Str(item, "name"),
                    Description = Str(item, "description"),
                    Url = Str(item, "url"),
                    Recommended = item.TryGetProperty("recommended", out var r) && r.ValueKind == JsonValueKind.True,
                };
                if (d.Name.Length > 0 && d.Url.Length > 0)
                    list.Add(d);
            }
            return list;
        }
        catch (Exception ex)
        {
            V1Per.Ui.Muted(ex.Message);
            return null;
        }
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string FileNameFromUrl(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrEmpty(name) ? $"driver_{DateTime.Now:yyyyMMdd_HHmmss}.zip" : name;
        }
        catch (Exception)
        {
            return $"driver_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
        }
    }

    /// <summary>Streams a file showing rough percentage progress.</summary>
    private static async Task<bool> DownloadAsync(string url, string dest)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;

            await using var fs = File.Create(dest);
            await using var stream = await resp.Content.ReadAsStreamAsync();
            var buffer = new byte[81920];
            long read = 0;
            DateTime lastPrint = DateTime.MinValue;
            while (true)
            {
                int n = await stream.ReadAsync(buffer);
                if (n == 0)
                    break;
                await fs.WriteAsync(buffer.AsMemory(0, n));
                read += n;
                if (total > 0 && (DateTime.UtcNow - lastPrint).TotalMilliseconds >= 250)
                {
                    lastPrint = DateTime.UtcNow;
                    V1Per.Ui.MarkupLine($"[dim]{read * 100 / total,3}% ({read / 1048576.0:F1} / {total / 1048576.0:F1} MB)[/]");
                }
            }
            return fs.Length > 0;
        }
        catch (Exception ex)
        {
            V1Per.Ui.Muted(ex.Message);
            try
            {
                File.Delete(dest);
            }
            catch (Exception)
            {
                // ignore
            }
            return false;
        }
    }

    private static string DownloadsFolder()
    {
        if (TryKnownFolder("374DE290-123F-4565-9164-39C4925E467B", out string? path) && path is { Length: > 0 })
            return path;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private static bool TryKnownFolder(string folderId, out string? path)
    {
        path = null;
        try
        {
            IntPtr pszPath = IntPtr.Zero;
            int hr = SHGetKnownFolderPath(new Guid(folderId), 0, IntPtr.Zero, out pszPath);
            if (hr != 0)
                return false;
            path = Marshal.PtrToStringUni(pszPath);
            Marshal.FreeCoTaskMem(pszPath);
            return path is { Length: > 0 };
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr pszPath);
}