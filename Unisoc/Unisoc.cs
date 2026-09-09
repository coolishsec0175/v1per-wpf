using System.IO;
using System.Net.Http;

namespace v1per_wpf;

/// <summary>
/// Unisoc / Spreadtrum support. Drives the per-chipset unlock packages that live
/// in Unisoc/&lt;package&gt;/ (e.g. ums9230, sc9863a): spd_dump.exe + download
/// agents + the unlock_autopatch*.bat script, ported as a native C# flow.
/// </summary>
public static partial class Unisoc
{
    /// <summary>Spreadtrum/Unisoc USB vendor ID (download mode, sdboot, diag).</summary>
    public const ushort SprdVid = 0x1782;

    /// <summary>Optional FDL overrides applied to every spd_dump call (GUI Flash tab). Empty = package default.</summary>
    public static string OverrideFdl1 = "";
    public static string OverrideFdl1Addr = "";
    public static string OverrideFdl2 = "";
    public static string OverrideFdl2Addr = "";

    /// <summary>Extra args (e.g. chipset/device token) prefixed before each spd_dump run.</summary>
    public static string PrefixArgs = "";

    /// <summary>When true, erase/read/flash skip modem/NV style partitions (Keep NV toggle).</summary>
    public static bool KeepNv = false;

    /// <summary>When true, spd_dump connects via the --kick route (boot_diag -> dl_diag).</summary>
    public static bool Kick = false;

    /// <summary>Progress 0..100 raised by spd_dump while a long op runs.</summary>
    public static Action<int>? Progress;

    /// <summary>Partitions skipped for write/erase when <see cref="KeepNv"/> is on.</summary>
    public static readonly string[] NvProtected = { "nvram", "nvdata", "nvcfg", "prodnv", "modem", "miscdata", "dsp", "wcn", "lte" };

    public static bool IsNvProtected(string name)
    {
        string n = name.ToLowerInvariant();
        return NvProtected.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] SprdKeywords = { "Spreadtrum", "Unisoc", "sdboot", "ums", "sc" };

    private static readonly HttpClient Http = new();

    static Unisoc()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("V1Per/1.2");
        Http.Timeout = TimeSpan.FromSeconds(180);
    }

    public enum Mode { None, Adb, Fastboot, Download }

    /// <summary>Everything that differs between a supported chipset's unlock package.</summary>
    public sealed record UnisocPackage
    {
        /// <summary>Folder name under Unisoc/ (e.g. "ums9230").</summary>
        public required string Id { get; init; }

        /// <summary>Human friendly name shown in the UI (e.g. "UMS9230").</summary>
        public required string Name { get; init; }

        /// <summary>Payload exec address; also derives custom_exec_no_verify_&lt;addr&gt;.bin.</summary>
        public required uint ExecAddr { get; init; }

        public required string Fdl1 { get; init; }
        public required uint Fdl1Addr { get; init; }

        public required string Fdl2 { get; init; }
        public required uint Fdl2Addr { get; init; }

        public required string Cboot { get; init; }

        /// <summary>Optional pre-captured splloader used to build the unlocker; null = use the fresh dump.</summary>
        public string? SplLoaderBk { get; init; }

        /// <summary>misc image written on restore (misc-ubldone.bin vs misc-wipe.bin).</summary>
        public required string MiscDone { get; init; }

        /// <summary>True to run `chsize uboot.bin` on the fresh dump before backing it up.</summary>
        public bool ChsizeUboot { get; init; }

        /// <summary>Host-tool build family: "gen1" (ums9230/sc9863a) or "gen2" (ums512/ums9620).</summary>
        public string ToolsGen { get; init; } = "gen2";

        /// <summary>True to erase the persist partition during restore (ums9230 flow).</summary>
        public bool ErasePersist { get; init; }

        /// <summary>Partitions read as a best-effort backup before restoring the boot chain.</summary>
        public required string[] BackupPartitions { get; init; }

        /// <summary>Files copied into the scratch work dir for spd_dump (incl. auto-loaded exec payload).</summary>
        public required string[] Files { get; init; }

        public string CustomExec => $"custom_exec_no_verify_{ExecAddr:x}.bin";
    }

    /// <summary>Known chipset packages. Payload folders live in Unisoc/&lt;Id&gt;/.</summary>
    public static readonly IReadOnlyDictionary<string, UnisocPackage> Packages =
        new Dictionary<string, UnisocPackage>
        {
            ["ums9230"] = new()
            {
                Id = "ums9230",
                Name = "UMS9230 (T606/T612)",
                ExecAddr = 0x65015f08,
                Fdl1 = "fdl1-dl.bin", Fdl1Addr = 0x65000800,
                Fdl2 = "fdl2-dl.bin", Fdl2Addr = 0x9efffe00,
                Cboot = "fdl2-cboot.bin",
                SplLoaderBk = "splloader_bk.bin",
                MiscDone = "misc-ubldone.bin",
                ChsizeUboot = false,
                ToolsGen = "gen1",
                ErasePersist = true,
                BackupPartitions = new[] { "boot", "init_boot", "vendor_boot", "prodnv" },
                Files = new[]
                {
                    "custom_exec_no_verify_65015f08.bin", "fdl1-dl.bin", "fdl2-dl.bin",
                    "fdl2-cboot.bin", "splloader_bk.bin", "misc-ubldone.bin", "misc-wipe.bin",
                },
            },
            ["sc9863a"] = new()
            {
                Id = "sc9863a",
                Name = "SC9863A",
                ExecAddr = 0x4ee8,
                Fdl1 = "fdl1-dl.bin", Fdl1Addr = 0x5000,
                Fdl2 = "fdl2-dl.bin", Fdl2Addr = 0x9efffe00,
                Cboot = "fdl2-cboot.bin",
                SplLoaderBk = null,
                MiscDone = "misc-wipe.bin",
                ChsizeUboot = true,
                ToolsGen = "gen1",
                ErasePersist = false,
                BackupPartitions = new[] { "boot", "prodnv" },
                Files = new[]
                {
                    "custom_exec_no_verify_4ee8.bin", "fdl1-dl.bin", "fdl2-dl.bin",
                    "fdl2-cboot.bin", "misc-wipe.bin",
                },
            },
            ["ums512"] = new()
            {
                Id = "ums512",
                Name = "UMS512 (T610/T700)",
                ExecAddr = 0x3ee8,
                Fdl1 = "fdl1-dl.bin", Fdl1Addr = 0x5500,
                Fdl2 = "fdl2-dl.bin", Fdl2Addr = 0x9efffe00,
                Cboot = "fdl2-cboot.bin",
                SplLoaderBk = null,
                MiscDone = "misc-wipe.bin",
                ChsizeUboot = false,
                ErasePersist = false,
                BackupPartitions = new[] { "boot", "prodnv" },
                Files = new[]
                {
                    "custom_exec_no_verify_3ee8.bin", "fdl1-dl.bin", "fdl2-dl.bin",
                    "fdl2-cboot.bin", "misc-wipe.bin",
                },
            },
            ["ums9620"] = new()
            {
                Id = "ums9620",
                Name = "UMS9620",
                ExecAddr = 0x65012f48,
                Fdl1 = "fdl1-dl.bin", Fdl1Addr = 0x65000800,
                Fdl2 = "fdl2-dl.bin", Fdl2Addr = 0x9efffe00,
                Cboot = "fdl2-cboot.bin",
                SplLoaderBk = null,
                MiscDone = "misc-wipe.bin",
                ChsizeUboot = false,
                ErasePersist = false,
                BackupPartitions = new[] { "boot", "prodnv" },
                Files = new[]
                {
                    "custom_exec_no_verify_65012f48.bin", "fdl1-dl.bin", "fdl2-dl.bin",
                    "fdl2-cboot.bin", "misc-wipe.bin",
                },
            },
        };

    public static async Task Run(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "root";
        string[] rest = args.Skip(1).ToArray();

        switch (sub)
        {
            case "detect":
            case "state":
            case "devices":
                Detect();
                break;

            case "unlock":
                await Unlock(rest);
                break;

            case "dump":
                await Dump(rest);
                break;

            case "flash":
                await FlashPartition(rest);
                break;

            case "erase":
                await ErasePartition(rest);
                break;

            case "parts":
            case "gpt":
            case "identify":
                await Parts(rest);
                break;

            case "read":
                await ReadParts(rest);
                break;

            case "erasefrp":
            case "frp":
                await EraseFrp(rest);
                break;

            case "root":
                await RootFlow(rest);
                break;

            case "packages":
            case "list":
                ListPackages();
                break;

            case "help":
            default:
                if (sub != "help")
                    V1Per.Ui.Err($"Unknown unisoc subcommand '{sub}'.");
                Help();
                break;
        }
    }

    private static void Help()
    {
        V1Per.Ui.Rule("unisoc subcommands");
        V1Per.Ui.Muted("  unisoc packages              — list installed chipset packages");
        V1Per.Ui.Muted("  unisoc detect                — show device state (adb/fastboot/download)");
        V1Per.Ui.Muted("  unisoc parts [chipset]         — pull the on-device GPT partition table (download mode)");
        V1Per.Ui.Muted("  unisoc unlock [chipset|device]  — unlock bootloader via download mode (CVE-2022-38694)");
        V1Per.Ui.Muted("  unisoc dump [chipset|device]    — full partition backup via download mode (spd_dump r all)");
        V1Per.Ui.Muted("  unisoc read <chipset> <part...> — read selected partitions to Downloads (r <part>)");
        V1Per.Ui.Muted("  unisoc flash <chipset|device> <part> <img> — write a partition image through download mode");
        V1Per.Ui.Muted("  unisoc erase <chipset|device> <part>      — erase a partition through download mode");
        V1Per.Ui.Muted("  unisoc erasefrp [chipset]       — erase the FRP partition in download mode");
        V1Per.Ui.Muted("  unisoc root [chipset|device] [boot.img]   — full flow: unlock → patch → flash");
        V1Per.Ui.Muted("  chipsets: ums9230 · sc9863a · ums512 · ums9620 (or a device alias like c21y / hot40i)");
    }

    /// <summary>Prints the installed packages and their payload folders.</summary>
    private static void ListPackages()
    {
        V1Per.Ui.Rule("unisoc packages");
        foreach (var pkg in Packages.Values)
        {
            string? dir = PackageDir(pkg);
            string status = dir is null
                ? "[red]missing folder[/]"
                : $"{Path.GetFileName(dir)}";
            V1Per.Ui.MarkupLine($"[#{V1Per.Ui.LightBlue.ToHex()}]{pkg.Id,-10}[/] {pkg.Name,-22} {status}");
        }
    }

    // ── package discovery ─────────────────────────────────────────

    /// <summary>Finds the Unisoc root folder (walking up from the app dir).</summary>
    private static string? UnisocRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Unisoc");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Returns the payload folder for a package, or null if it is not installed.</summary>
    private static string? PackageDir(UnisocPackage pkg)
    {
        string? root = UnisocRoot();
        if (root is null)
            return null;
        string candidate = Path.Combine(root, pkg.Id);
        return Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Resolves the spd_dump.exe to run: the shared Unisoc/spd_dump.exe by
    /// default, or the package-local one when present (e.g. sc9863a needs its
    /// own older build).
    /// </summary>
    private static string? ResolveSpdDump(string pkgDir)
    {
        string? root = UnisocRoot();
        if (root is not null && File.Exists(Path.Combine(root, "spd_dump.exe")))
            return Path.Combine(root, "spd_dump.exe");
        string local = Path.Combine(pkgDir, "spd_dump.exe");
        return File.Exists(local) ? local : null;
    }

    /// <summary>First installed package (prefers ums9230), used as the default chipset.</summary>
    private static UnisocPackage DefaultPackage()
    {
        if (PackageDir(Packages["ums9230"]) is not null)
            return Packages["ums9230"];
        foreach (var pkg in Packages.Values)
        {
            if (PackageDir(pkg) is not null)
                return pkg;
        }
        return Packages["ums9230"];
    }

    /// <summary>
    /// Splits an optional leading chipset/device id off the argument list.
    /// Accepts either a package id (ums9230, sc9863a, ...) or a device alias
    /// (c11, c21y, hot40i, ...). Returns the package, the device name (null for
    /// a bare package id), and the remaining args.
    /// </summary>
    private static (UnisocPackage Pkg, string? Device, string[] Args) ResolvePackage(string[] args)
    {
        if (args.Length > 0)
        {
            string key = args[0].ToLowerInvariant();
            if (Packages.TryGetValue(key, out var pkg))
                return (pkg, null, args.Skip(1).ToArray());
            if (DeviceAliases.TryGetValue(key, out string? id) && Packages.TryGetValue(id, out pkg))
                return (pkg, key, args.Skip(1).ToArray());
        }
        return (DefaultPackage(), null, args);
    }

    /// <summary>Device-specific payload folder: Unisoc/&lt;soc&gt;/&lt;device&gt;/ (mirrors SFD).</summary>
    private static string? DeviceDir(UnisocPackage pkg, string? device)
    {
        if (string.IsNullOrEmpty(device))
            return null;
        string? root = UnisocRoot();
        if (root is null)
            return null;
        string candidate = Path.Combine(root, pkg.Id, device);
        return File.Exists(Path.Combine(candidate, "fdl1-dl.bin")) ? candidate : null;
    }

    /// <summary>Device name -> package id (mirrors the SFD device database).</summary>
    public static readonly IReadOnlyDictionary<string, string> DeviceAliases =
        new Dictionary<string, string>
        {
            // ums9230 family
            ["hot40i"] = "ums9230", ["hot30i"] = "ums9230", ["smart8"] = "ums9230",
            ["smart10"] = "ums9230", ["hot12pro"] = "ums9230",
            ["s25"] = "ums9230", ["s23"] = "ums9230", ["s25ultra"] = "ums9230",
            ["city100"] = "ums9230", ["p40plus"] = "ums9230", ["p55nfc"] = "ums9230",
            ["p65"] = "ums9230", ["s23plus"] = "ums9230", ["vision3plus"] = "ums9230",
            ["vision5plus"] = "ums9230", ["sketsa_3"] = "ums9230",
            ["c31"] = "ums9230", ["c33"] = "ums9230", ["c35"] = "ums9230",
            ["c51"] = "ums9230", ["c53"] = "ums9230", ["c61"] = "ums9230",
            ["c71"] = "ums9230", ["narzo50i_prime"] = "ums9230", ["note50"] = "ums9230",
            ["note60"] = "ums9230", ["note60x"] = "ums9230",
            ["kl4"] = "ums9230", ["spark_8c"] = "ums9230", ["spark_10c"] = "ums9230",
            ["km4"] = "ums9230", ["kn3"] = "ums9230", ["a5"] = "ums9230",
            ["y19s"] = "ums9230", ["a35"] = "ums9230", ["c32"] = "ums9230",
            // sc9863a family
            ["a70"] = "sc9863a", ["a60s"] = "sc9863a", ["a50"] = "sc9863a",
            ["a80"] = "sc9863a", ["a90"] = "sc9863a", ["vision3"] = "sc9863a",
            ["smart7"] = "sc9863a", ["c11"] = "sc9863a", ["narzo50i"] = "sc9863a",
            ["blade_a31"] = "sc9863a", ["blade_a5"] = "sc9863a", ["blade_a51"] = "sc9863a",
            ["blade_a52"] = "sc9863a", ["blade_a7"] = "sc9863a", ["blade_v2020"] = "sc9863a",
            // ums512 family
            ["hot12play_nfc"] = "ums512", ["c21y"] = "ums512", ["c25y"] = "ums512",
            ["tab_vx_lite_1013"] = "ums512",
            // ums9620 family
            ["nubia_neo_gt_3"] = "ums9620", ["nubia_neo_2"] = "ums9620",
        };

    /// <summary>Resolves the package folder, reporting an error when it is not installed.</summary>
    private static string? ResolvePackageDir(UnisocPackage pkg)
    {
        string? dir = PackageDir(pkg);
        if (dir is null)
        {
            V1Per.Ui.Err($"Package '{pkg.Id}' not installed.");
            V1Per.Ui.Muted($"Expected: <app>/Unisoc/{pkg.Id}/ with {pkg.Fdl1} etc. (spd_dump.exe is shared in Unisoc/)");
            return null;
        }
        V1Per.Ui.Ok($"Using package: {pkg.Name} ({Path.GetFileName(dir)})");
        return dir;
    }

    // ── state detection ──────────────────────────────────────────

    private static (Mode, string?) Detect()
    {
        V1Per.Ui.Rule("Unisoc device state");

        string adb = V1Per.ProcessRunner.Run("adb", new[] { "devices" }, 8_000);
        foreach (string line in adb.Split('\n').Skip(1))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == "device")
            {
                V1Per.Ui.Ok($"ADB device: {parts[0]}");
                return (Mode.Adb, parts[0]);
            }
        }

        string fb = V1Per.ProcessRunner.Run("fastboot", new[] { "devices" }, 8_000);
        foreach (string line in fb.Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0 || t.Contains("waiting", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                V1Per.Ui.Ok($"Fastboot device: {parts[0]}");
                return (Mode.Fastboot, parts[0]);
            }
        }

        var ports = V1Per.ComPorts.Enumerate()
            .Where(p => p.Vid == SprdVid || SprdKeywords.Any(k => (p.Description + " ").Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (ports.Count > 0)
        {
            foreach (var p in ports)
                V1Per.Ui.Ok($"Download mode port: {p.Device} ({p.Description})");
            V1Per.Ui.Info("Device is in Spreadtrum download mode.");
            return (Mode.Download, null);
        }

        V1Per.Ui.Err("No Unisoc device detected.");
        V1Per.Ui.Muted("Options: ADB online (USB debugging) · fastboot · download mode (VID 1782)");
        V1Per.Ui.Muted("To enter download mode: power off, hold Vol+ (or Vol-) and plug in USB.");
        return (Mode.None, null);
    }
}