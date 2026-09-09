using System.IO;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace v1per_wpf;

/// <summary>Download-mode dump / partition flash + the full root state machine.</summary>
public static partial class Unisoc
{
    private static async Task Dump(string[] args)
    {
        var (pkg, device, _) = ResolvePackage(args);

        V1Per.Ui.Rule($"Unisoc full dump ({pkg.Name})");
        if (!EnsureDownload(pkg.Name))
            return;

        string? pkgDir = ResolvePackageDir(pkg);
        if (pkgDir is null)
            return;
        string? deviceDir = DeviceDir(pkg, device);
        string work = PrepareWork(pkgDir, deviceDir, pkg);

        string dumpDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "v1per_unisoc_dump");
        Directory.CreateDirectory(dumpDir);

        var tokens = Base(true, pkg);
        tokens.AddRange(new[] { "path", dumpDir, "r", "all", "reset" });
        V1Per.Ui.Info($"Dumping all partitions to {dumpDir}");
        await RunSpdDumpTokens(pkgDir, work, tokens);
        V1Per.Ui.Ok("Dump finished.");
    }

    private static async Task FlashPartition(string[] args)
    {
        var (pkg, device, rest) = ResolvePackage(args);
        if (rest.Length < 2)
        {
            V1Per.Ui.Err("Usage: unisoc flash <chipset|device> <partition> <image>");
            return;
        }
        string partition = rest[0];
        string image = string.Join(" ", rest.Skip(1)).Trim('"', '\'');
        if (!File.Exists(image))
        {
            V1Per.Ui.Err($"Image not found: {image}");
            return;
        }

        V1Per.Ui.Rule($"Unisoc download-mode flash ({pkg.Name})");
        if (!EnsureDownload(pkg.Name))
            return;

        string? pkgDir = ResolvePackageDir(pkg);
        if (pkgDir is null)
            return;
        string? deviceDir = DeviceDir(pkg, device);
        string work = PrepareWork(pkgDir, deviceDir, pkg);

        V1Per.Ui.Warn($"Flashing {partition} ← {Path.GetFileName(image)}");
        if (!await Terminal.ConfirmAsync("Proceed?"))
        {
            V1Per.Ui.Muted("Cancelled.");
            return;
        }

        var tokens = Base(true, pkg);
        tokens.AddRange(new[] { "w", partition, image, "reset" });
        await RunSpdDumpTokens(pkgDir, work, tokens);
        V1Per.Ui.Ok($"Write to {partition} finished.");
    }

    private static async Task ErasePartition(string[] args)
    {
        var (pkg, device, rest) = ResolvePackage(args);
        if (rest.Length < 1)
        {
            V1Per.Ui.Err("Usage: unisoc erase <chipset|device> <partition>");
            return;
        }
        string partition = rest[0];

        V1Per.Ui.Rule($"Unisoc download-mode erase ({pkg.Name})");
        if (!EnsureDownload(pkg.Name))
            return;

        string? pkgDir = ResolvePackageDir(pkg);
        if (pkgDir is null)
            return;
        string? deviceDir = DeviceDir(pkg, device);
        string work = PrepareWork(pkgDir, deviceDir, pkg);

        V1Per.Ui.Warn($"Erasing {partition}");
        if (!await Terminal.ConfirmAsync("Proceed?"))
        {
            V1Per.Ui.Muted("Cancelled.");
            return;
        }

        var tokens = Base(true, pkg);
        tokens.AddRange(new[] { "e", partition, "reset" });
        await RunSpdDumpTokens(pkgDir, work, tokens);
        V1Per.Ui.Ok($"Erase of {partition} finished.");
    }

    private static async Task Parts(string[] args)
    {
        var (pkg, device, _) = ResolvePackage(args);

        V1Per.Ui.Rule($"Unisoc partition table ({pkg.Name})");
        if (!EnsureDownload(pkg.Name))
            return;

        string? pkgDir = ResolvePackageDir(pkg);
        if (pkgDir is null)
            return;
        string? deviceDir = DeviceDir(pkg, device);
        string work = PrepareWork(pkgDir, deviceDir, pkg);

        string listFile = Path.Combine(work, "partition_list.txt");
        var tokens = Base(true, pkg);
        tokens.AddRange(new[] { "path", work, "partition_list", listFile, "p", "reset" });
        await RunSpdDumpTokens(pkgDir, work, tokens);

        if (!File.Exists(listFile))
        {
            V1Per.Ui.Warn("No partition list file was produced; the loader may not support partition_list.");
            return;
        }

        var parts = ParsePartitionList(File.ReadAllLines(listFile));
        if (parts.Count == 0)
        {
            V1Per.Ui.Warn("Could not parse any partitions from the list.");
            return;
        }
        V1Per.Ui.Ok($"Found {parts.Count} partitions.");
        var table = new Spectre.Console.Table()
            .Border(Spectre.Console.TableBorder.Rounded)
            .BorderStyle(new Style(V1Per.Ui.PinkRed))
            .Title(new TableTitle($"Partitions ({pkg.Name})", new Style(V1Per.Ui.LightBlue)));
        table.AddColumn(new TableColumn(new Markup("[bold]Name[/]")));
        table.AddColumn(new TableColumn(new Markup("[bold]Size[/]")).RightAligned());
        foreach (var (name, size) in parts)
            table.AddRow(name, size);
        V1Per.Ui.Write(table);
    }

    private static List<(string Name, string Size)> ParsePartitionList(string[] lines)
    {
        var result = new List<(string, string)>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//"))
                continue;
            var m = Regex.Match(line, @"^([a-zA-Z0-9_]+)\s*(?:0x[0-9a-fA-F]+)?", RegexOptions.IgnoreCase);
            if (!m.Success)
                continue;
            string name = m.Groups[1].Value.ToLowerInvariant();
            if (name is "name" or "partition" or "start" or "size" or "type" or "sector" or "length")
                continue;
            var sizeHex = Regex.Match(line, @"0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
            string size = sizeHex.Success && ulong.TryParse(sizeHex.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out ulong v)
                ? Human(v)
                : "";
            result.Add((name, size));
        }
        return result.Distinct().ToList();
    }

    private static string Human(ulong size)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double v = size;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:F1} {units[i]}";
    }

    private static async Task ReadParts(string[] args)
    {
        var (pkg, device, rest) = ResolvePackage(args);
        if (rest.Length == 0)
        {
            V1Per.Ui.Err("Usage: unisoc read <chipset> <partition> [partition...]");
            return;
        }
        var targets = rest.Select(p => p.ToLowerInvariant()).Distinct().ToList();

        V1Per.Ui.Rule($"Unisoc read partitions ({pkg.Name})");
        if (!EnsureDownload(pkg.Name))
            return;

        string? pkgDir = ResolvePackageDir(pkg);
        if (pkgDir is null)
            return;
        string? deviceDir = DeviceDir(pkg, device);
        string work = PrepareWork(pkgDir, deviceDir, pkg);

        string dumpDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "v1per_unisoc_dump");
        Directory.CreateDirectory(dumpDir);

        var tokens = Base(true, pkg);
        tokens.AddRange(new[] { "path", dumpDir });
        foreach (string part in targets)
            tokens.AddRange(new[] { "r", part });
        tokens.Add("reset");

        V1Per.Ui.Info($"Reading {string.Join(", ", targets)} to {dumpDir}");
        await RunSpdDumpTokens(pkgDir, work, tokens);
        V1Per.Ui.Ok("Read finished.");
    }

    private static async Task EraseFrp(string[] args)
    {
        var (pkg, device, _) = ResolvePackage(args);

        V1Per.Ui.Rule($"Unisoc erase FRP ({pkg.Name})");
        if (!EnsureDownload(pkg.Name))
            return;

        string? pkgDir = ResolvePackageDir(pkg);
        if (pkgDir is null)
            return;
        string? deviceDir = DeviceDir(pkg, device);
        string work = PrepareWork(pkgDir, deviceDir, pkg);

        V1Per.Ui.Warn("Erasing the FRP partition. Device will wipe Google FRP.");
        if (!await Terminal.ConfirmAsync("Proceed?"))
        {
            V1Per.Ui.Muted("Cancelled.");
            return;
        }

        var tokens = Base(true, pkg);
        tokens.AddRange(new[] { "e", "frp", "reset" });
        await RunSpdDumpTokens(pkgDir, work, tokens);
        V1Per.Ui.Ok("FRP erase finished.");
    }

    private static async Task RootFlow(string[] args)
    {
        var (pkg, device, rest) = ResolvePackage(args);

        V1Per.Ui.Rule($"Unisoc root ({pkg.Name})");
        var (mode, serial) = Detect();

        if (mode == Mode.Download)
        {
            V1Per.Ui.Warn("Device is in download mode (bootloader locked).");
            if (await Terminal.ConfirmAsync("Unlock the bootloader first?"))
                await Unlock(new[] { device ?? pkg.Id });
            V1Per.Ui.Info("Waiting for the device to boot into Android (ADB)...");
            serial = await WaitForAdbAsync(120);
            if (serial is null)
            {
                V1Per.Ui.Err("No ADB device appeared.");
                return;
            }
        }

        if (serial is null)
        {
            V1Per.Ui.Err("Root needs an ADB online device to patch the boot image.");
            V1Per.Ui.Muted("Enable USB debugging and reconnect.");
            return;
        }

        string bootImg = rest.Length > 0
            ? string.Join(" ", rest).Trim('"', '\'')
            : await Terminal.PromptAsync("Enter boot.img path: ");
        if (bootImg.Length == 0 || !File.Exists(bootImg))
        {
            V1Per.Ui.Err($"File not found: {bootImg}");
            return;
        }

        string partition = Path.GetFileName(bootImg).ToLowerInvariant().Contains("init_boot") ? "init_boot" : "boot";

        string? patched = await Root.PatchOnly(serial, bootImg);
        if (patched is null)
        {
            V1Per.Ui.Err("Patch failed.");
            return;
        }
        V1Per.Ui.Ok($"Patched: {patched}");

        var (m2, _) = Detect();
        if (m2 == Mode.Fastboot || await Terminal.ConfirmAsync("Flash via fastboot (device will reboot to bootloader)?"))
        {
            await Root.FlashPatched(serial, partition, patched);
        }
        else
        {
            V1Per.Ui.Info("Reboot the phone into download mode, then run:");
            V1Per.Ui.MarkupLine($"[#{V1Per.Ui.LightBlue.ToHex()}]unisoc flash {pkg.Id} {partition} {patched}[/]");
        }
    }

    private static async Task<string?> WaitForAdbAsync(int seconds)
    {
        for (int i = 0; i < seconds; i += 2)
        {
            await Task.Delay(2000);
            string output = V1Per.ProcessRunner.Run("adb", new[] { "devices" }, 8_000);
            foreach (string line in output.Split('\n').Skip(1))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1] == "device")
                    return parts[0];
            }
            if (i % 10 == 0)
                V1Per.Ui.Muted($"waiting for ADB device... {i}s");
        }
        return null;
    }
}
