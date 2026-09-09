using System.IO;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace v1per_wpf;

public sealed class ScatterPartition
{
    public string Name = "";
    public string FileName = "";
    public string StartAddr = "";
    public string PartitionSize = "";
    public string Region = "";
    public string Storage = "";
    public string Type = "";
    public int RegionIndex;
    public bool IsDownloadable = true;
    public bool IsBackup;
    public string? ResolvedPath;

    public string SizeHuman()
    {
        if (!long.TryParse(PartitionSize, System.Globalization.NumberStyles.HexNumber, null, out long size) || size == 0)
            return "N/A";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double v = size;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:F1} {units[i]}";
    }

    public bool Unsafe => Scatter.UnsafeFastboot.Contains(Name.ToLowerInvariant());
}

/// <summary>MTK scatter-file parsing and fastboot flashing (v1per.py port).</summary>
public static class Scatter
{
    public static readonly HashSet<string> UnsafeFastboot = new()
    {
        "preloader", "preloader_a", "preloader_b", "pgpt", "sgpt", "nvram",
        "nvdata", "nvcfg", "protect1", "protect2", "seccfg", "lk", "lk_a", "lk_b",
    };

    public static List<ScatterPartition> Parse(string scatterPath)
    {
        var result = new List<ScatterPartition>();
        if (!File.Exists(scatterPath))
        {
            V1Per.Ui.Err($"File not found: {scatterPath}");
            return result;
        }

        string content;
        try
        {
            content = File.ReadAllText(scatterPath);
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Failed to read scatter file: {ex.Message}");
            return result;
        }

        ScatterPartition? current = null;
        foreach (string raw in content.Split('\n'))
        {
            string ls = raw.Trim();

            var nameMatch = Regex.Match(ls, @"^[-*]\s+partition_name\s*:\s*(\S+)", RegexOptions.IgnoreCase);
            if (nameMatch.Success)
            {
                if (current is { Name.Length: > 0 })
                    result.Add(current);
                current = new ScatterPartition { Name = nameMatch.Groups[1].Value };
                continue;
            }

            var idxMatch = Regex.Match(ls, @"^[-*]\s+partition_index\s*:\s*\S+", RegexOptions.IgnoreCase);
            if (idxMatch.Success)
            {
                if (current is { Name.Length: > 0 })
                    result.Add(current);
                current = new ScatterPartition();
                continue;
            }

            if (current is null)
                continue;

            var fields = new (string Field, Action<string> Set)[]
            {
                ("partition_name", v => current.Name = v),
                ("file_name", v => current.FileName = v),
                ("linear_start_addr", v => current.StartAddr = v),
                ("partition_size", v => current.PartitionSize = v),
                ("region", v => current.Region = v),
                ("storage", v => current.Storage = v),
                ("type", v => current.Type = v),
                ("region_index", v => current.RegionIndex = int.TryParse(v, out int ri) ? ri : 0),
            };
            foreach (var (field, set) in fields)
            {
                var m = Regex.Match(ls, $@"^[-*]?\s*{field}\s*:\s*(.+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    set(m.Groups[1].Value.Trim());
                    break;
                }
            }
        }

        if (current is { Name.Length: > 0 })
            result.Add(current);

        foreach (var part in result)
        {
            if (part.Name.StartsWith("_"))
            {
                part.IsBackup = true;
                part.IsDownloadable = false;
            }
            if (part.FileName.Equals("NONE", StringComparison.OrdinalIgnoreCase) || part.FileName.Length == 0)
                part.IsDownloadable = false;
        }
        return result;
    }

    public static string? FindImage(string scatterDir, string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        string exact = Path.Combine(scatterDir, fileName);
        if (File.Exists(exact))
            return exact;

        foreach (string f in Directory.EnumerateFiles(scatterDir))
        {
            if (Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return f;
        }
        foreach (string f in Directory.EnumerateFiles(scatterDir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return f;
        }
        return null;
    }

    public static void Print(List<ScatterPartition> partitions)
    {
        var table = new Table
        {
            Border = TableBorder.Rounded,
            BorderStyle = new Style(V1Per.Ui.PinkRed),
            Title = new TableTitle("Scatter partitions", new Style(V1Per.Ui.LightBlue)),
        };
        table.AddColumn(new TableColumn(new Markup("[bold]#[/]")).RightAligned());
        table.AddColumn(new TableColumn(new Markup("[bold]Partition[/]")));
        table.AddColumn(new TableColumn(new Markup("[bold]File[/]")));
        table.AddColumn(new TableColumn(new Markup("[bold]Size[/]")).RightAligned());
        table.AddColumn(new TableColumn(new Markup("[bold]Note[/]")));

        int downloadable = 0;
        for (int i = 0; i < partitions.Count; i++)
        {
            var part = partitions[i];
            string note = "";
            string style = "white";
            if (part.IsBackup)
            {
                note = "backup";
                style = "dim";
            }
            else if (!part.IsDownloadable)
            {
                note = "no image";
                style = "dim";
            }
            else if (part.Unsafe)
            {
                note = "unsafe via fastboot";
                style = "yellow";
            }
            else
            {
                downloadable++;
            }
            table.AddRow(
                (i + 1).ToString(),
                $"[{style}]{part.Name}[/]",
                part.FileName.Length == 0 ? "N/A" : part.FileName,
                part.SizeHuman(),
                note);
        }
        V1Per.Ui.Write(table);
        V1Per.Ui.Muted($"Total {partitions.Count} · {downloadable} safe-to-list with an image name");
    }

    public static List<int> ParseSelection(string raw, int count)
    {
        var indices = new SortedSet<int>();
        foreach (string part in raw.Split(','))
        {
            string p = part.Trim();
            if (p.Length == 0)
                continue;
            if (p.Contains('-'))
            {
                var range = p.Split('-');
                if (int.TryParse(range[0].Trim(), out int start) && int.TryParse(range[1].Trim(), out int end))
                {
                    for (int i = start; i <= end; i++)
                        indices.Add(i);
                }
            }
            else if (int.TryParse(p, out int idx))
            {
                indices.Add(idx);
            }
        }

        var good = new List<int>();
        foreach (int i in indices)
        {
            if (i >= 1 && i <= count)
                good.Add(i);
            else
                V1Per.Ui.Warn($"Index {i} out of range, skipped.");
        }
        return good;
    }

    public static bool Flash(ScatterPartition part, string imagePath, string? serial)
    {
        V1Per.Ui.Info($"Flashing {part.Name} ← {Path.GetFileName(imagePath)} ({part.SizeHuman()})");
        string result = V1Per.ProcessRunner.Fastboot(serial, new[] { "flash", part.Name, imagePath }, 180_000);
        if (FlashLooksOk(result))
        {
            V1Per.Ui.Ok($"{part.Name}: flash finished");
            return true;
        }
        V1Per.Ui.Err($"{part.Name}: flash may have failed");
        if (result.Length > 0)
        {
            foreach (string line in result.Split('\n').Take(8))
                V1Per.Ui.Muted(line);
        }
        else
        {
            V1Per.Ui.Muted("No response from device.");
        }
        return false;
    }

    private static bool FlashLooksOk(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return false;
        string lower = output.ToLowerInvariant();
        if (lower.Contains("failed") || lower.Contains("error:") || lower.Contains("unknown partition"))
            return false;
        return lower.Contains("okay") || lower.Contains("finished") || lower.Contains("sending");
    }

    /// <summary>Interactive scatter-flashing flow (v1per.py cmd_scatter port).</summary>
    public static async Task Run(string[] args)
    {
        V1Per.Ui.Rule("Scatter flasher");

        string scatterPath = args.Length > 0
            ? string.Join(" ", args).Trim('"', '\'')
            : await Terminal.PromptAsync("Scatter file path: ");
        if (!File.Exists(scatterPath))
        {
            V1Per.Ui.Err($"File not found: {scatterPath}");
            return;
        }

        string scatterDir = Path.GetDirectoryName(Path.GetFullPath(scatterPath)) ?? string.Empty;
        V1Per.Ui.Info("Parsing scatter file...");
        V1Per.Ui.Muted(scatterPath);

        var partitions = Parse(scatterPath);
        if (partitions.Count == 0)
        {
            V1Per.Ui.Err("No partitions found. Use a real MTK Android_scatter.txt.");
            return;
        }
        V1Per.Ui.Ok($"Found {partitions.Count} partitions.");
        Print(partitions);

        V1Per.Ui.Info($"Looking for images in {scatterDir}");
        var missing = new List<ScatterPartition>();
        int found = 0;
        foreach (var part in partitions)
        {
            if (!part.IsDownloadable)
                continue;
            part.ResolvedPath = FindImage(scatterDir, part.FileName);
            if (part.ResolvedPath is not null)
                found++;
            else
                missing.Add(part);
        }
        if (found > 0)
            V1Per.Ui.Ok($"Found {found} image file(s).");
        if (missing.Count > 0)
        {
            V1Per.Ui.Warn($"Missing {missing.Count} image file(s):");
            foreach (var part in missing)
                V1Per.Ui.Muted($"{part.Name} → {part.FileName}");
        }

        var downloadable = partitions.Where(p => p.IsDownloadable && p.ResolvedPath is not null).ToList();
        if (downloadable.Count == 0)
        {
            V1Per.Ui.Err("No flashable partitions with images on disk.");
            return;
        }

        V1Per.Ui.Info("Checking for a fastboot device...");
        var fb = FastbootDevices();
        string? serial = fb.Count > 0 ? fb[0] : null;
        if (serial is null)
        {
            V1Per.Ui.Err("No fastboot device found.");
            V1Per.Ui.Muted("Boot the phone into fastboot first.");
            if (!await Terminal.ConfirmAsync("Continue anyway?"))
                return;
        }

        var picker = new Table
        {
            Border = TableBorder.Rounded,
            BorderStyle = new Style(V1Per.Ui.PinkRed),
            Title = new TableTitle("Select partitions to flash", new Style(V1Per.Ui.LightBlue)),
        };
        picker.AddColumn(new TableColumn(new Markup("[bold]#[/]")).RightAligned());
        picker.AddColumn(new TableColumn(new Markup("[bold]Partition[/]")));
        picker.AddColumn(new TableColumn(new Markup("[bold]File[/]")));
        picker.AddColumn(new TableColumn(new Markup("[bold]Size[/]")).RightAligned());
        picker.AddColumn(new TableColumn(new Markup("[bold]Flag[/]")));
        for (int i = 0; i < downloadable.Count; i++)
        {
            var part = downloadable[i];
            picker.AddRow(
                (i + 1).ToString(),
                part.Name,
                part.FileName,
                part.SizeHuman(),
                part.Unsafe ? "[yellow]unsafe[/]" : "");
        }
        V1Per.Ui.Write(picker);
        V1Per.Ui.Muted("Type numbers or ranges. Example: 1,3,5 or 2-4");
        V1Per.Ui.Muted("Do not type 'all'. Preloader / NVRAM style partitions are marked unsafe.");

        string selection = (await Terminal.PromptAsync("Select: ")).Trim().ToLowerInvariant();
        if (selection.Length == 0 || selection == "all")
        {
            V1Per.Ui.Err("Refusing a blank / 'all' selection. Pick specific partitions.");
            return;
        }
        var chosenIdx = ParseSelection(selection, downloadable.Count);
        var selected = chosenIdx.Select(i => downloadable[i - 1]).ToList();
        if (selected.Count == 0)
        {
            V1Per.Ui.Err("No partitions selected.");
            return;
        }

        var unsafePicks = selected.Where(p => p.Unsafe).ToList();
        if (unsafePicks.Count > 0)
        {
            V1Per.Ui.Warn("These partitions are easy to brick with via fastboot:");
            foreach (var part in unsafePicks)
                V1Per.Ui.Muted(part.Name);
            if (!await Terminal.ConfirmAsync("Really include them?"))
                selected = selected.Where(p => !p.Unsafe).ToList();
            if (selected.Count == 0)
            {
                V1Per.Ui.Err("Nothing left to flash.");
                return;
            }
        }

        var summary = new Table
        {
            Border = TableBorder.Rounded,
            BorderStyle = new Style(V1Per.Ui.Yellow),
            Title = new TableTitle("Flash summary", new Style(V1Per.Ui.Yellow)),
        };
        summary.AddColumn(new TableColumn(new Markup("[bold]Partition[/]")));
        summary.AddColumn(new TableColumn(new Markup("[bold]File[/]")));
        summary.AddColumn(new TableColumn(new Markup("[bold]Size[/]")).RightAligned());
        foreach (var part in selected)
            summary.AddRow(part.Name, part.FileName, part.SizeHuman());
        V1Per.Ui.Write(summary);

        V1Per.Ui.Warn("Do not unplug the phone while flashing.");
        V1Per.Ui.Warn("The bootloader must already be unlocked.");
        if (!await Terminal.ConfirmAsync("Proceed?"))
        {
            V1Per.Ui.Muted("Cancelled.");
            return;
        }

        int success = 0, fail = 0;
        for (int i = 0; i < selected.Count; i++)
        {
            var part = selected[i];
            V1Per.Ui.Muted($"[[{i + 1}/{selected.Count}]]");
            if (Flash(part, part.ResolvedPath!, serial))
                success++;
            else
            {
                fail++;
                if (i < selected.Count - 1 && !await Terminal.ConfirmAsync("Continue after failure?"))
                {
                    V1Per.Ui.Err("Aborted.");
                    return;
                }
            }
        }

        V1Per.Ui.Rule("Flash complete");
        V1Per.Ui.Ok($"Successful: {success}");
        if (fail > 0)
        {
            V1Per.Ui.Err($"Failed: {fail}");
            V1Per.Ui.Warn("Device left in fastboot.");
            return;
        }
        if (await Terminal.ConfirmAsync("Reboot now?"))
        {
            V1Per.Ui.Info("Rebooting...");
            V1Per.ProcessRunner.Fastboot(serial, new[] { "reboot" }, 10_000);
            V1Per.Ui.Ok("Device rebooting.");
        }
        else
        {
            V1Per.Ui.Muted("Device still in fastboot.");
        }
    }

    private static List<string> FastbootDevices()
    {
        var result = new List<string>();
        string output = V1Per.ProcessRunner.Run("fastboot", new[] { "devices" }, 8_000);
        if (string.IsNullOrWhiteSpace(output))
            return result;
        foreach (string line in output.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.Contains("waiting", StringComparison.OrdinalIgnoreCase) || t.Contains("no permission", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
                result.Add(parts[0]);
        }
        return result;
    }
}