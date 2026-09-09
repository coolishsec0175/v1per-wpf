using System.IO;
using Spectre.Console;

namespace v1per_wpf;

/// <summary>
/// Bootloader unlock flow driven by a chipset package (ums9230, sc9863a, ...).
/// All package files (fdl1-dl.bin, custom_exec, splloader_bk.bin, ...) live in
/// Unisoc/&lt;chipset&gt;/; execution happens in a scratch work dir so generated
/// outputs never pollute it. Ported natively from the unlock_autopatch*.bat flow.
/// </summary>
public static partial class Unisoc
{
    /// <summary>
    /// Prepares a clean scratch dir with copies of the package's input files.
    /// When a device-specific folder exists (Unisoc/&lt;soc&gt;/&lt;device&gt;/) its
    /// exact fdl payloads override the family defaults.
    /// </summary>
    private static string PrepareWork(string pkgDir, string? deviceDir, UnisocPackage pkg)
    {
        string work = Path.Combine(Path.GetTempPath(), "v1per_unisoc");
        if (Directory.Exists(work))
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (Exception)
            {
                // ignore
            }
        }
        Directory.CreateDirectory(work);
        foreach (string name in pkg.Files)
        {
            string src = Path.Combine(pkgDir, name);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(work, name), true);
        }
        if (deviceDir is not null)
        {
            foreach (string name in new[] { pkg.Fdl1, pkg.Fdl2, pkg.Cboot })
            {
                string src = Path.Combine(deviceDir, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(work, name), true);
            }
        }
        return work;
    }

    /// <summary>Builds the spd_dump prefix; <paramref name="wait"/> adds --wait 300.</summary>
    private static List<string> Base(bool wait, UnisocPackage pkg)
    {
        var t = new List<string>();
        if (wait && Kick)
            t.Add("--kick");
        else if (wait)
            t.AddRange(new[] { "--wait", "300" });
        if (!string.IsNullOrEmpty(PrefixArgs))
            t.AddRange(PrefixArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string fdl1 = string.IsNullOrEmpty(OverrideFdl1) ? pkg.Fdl1 : OverrideFdl1;
        string fdl1Addr = string.IsNullOrEmpty(OverrideFdl1Addr) ? $"0x{pkg.Fdl1Addr:x}" : OverrideFdl1Addr;
        string fdl2 = string.IsNullOrEmpty(OverrideFdl2) ? pkg.Fdl2 : OverrideFdl2;
        string fdl2Addr = string.IsNullOrEmpty(OverrideFdl2Addr) ? $"0x{pkg.Fdl2Addr:x}" : OverrideFdl2Addr;
        t.AddRange(new[]
        {
            "exec_addr", $"0x{pkg.ExecAddr:x}",
            "fdl", fdl1, fdl1Addr,
            "fdl", fdl2, fdl2Addr,
            "exec",
        });
        return t;
    }

    /// <summary>
    /// Ensures the device is reachable in download mode. From ADB it arms the
    /// --kick route (boot_diag -> dl_diag) so the user just holds the key combo
    /// until the screen goes black. Returns true when we may proceed.
    /// </summary>
    private static bool EnsureDownload(string pkgName)
    {
        var (mode, _) = Detect();
        if (mode == Mode.Adb)
        {
            V1Per.Ui.Warn("Device is in ADB. Will auto-switch to download mode via the diag kick.");
            V1Per.Ui.Muted("When you see \"waiting for dl_diag\":");
            V1Per.Ui.Muted("  * Realme            — long-press ALL BUTTONS until the screen turns black");
            V1Per.Ui.Muted("  * Tecno/Infinix/Itel/others — long-press VOL DOWN + POWER");
            V1Per.Ui.Muted("Keep holding until the screen goes black, then release. Do NOT unplug.");
            Kick = true;
            return true;
        }
        if (mode == Mode.Download)
            return true;

        V1Per.Ui.Err("No Unisoc device in download mode (and none in ADB to kick).");
        V1Per.Ui.Muted("Plug the phone in, then enter download mode: hold VOL DOWN + POWER (Realme: ALL buttons).");
        return false;
    }

    private static async Task Unlock(string[] args)
    {
        var (pkg, device, rest) = ResolvePackage(args);

        V1Per.Ui.Rule($"Unisoc bootloader unlock ({pkg.Name}, CVE-2022-38694)");

        var (mode, _) = Detect();
        if (mode == Mode.Adb)
        {
            V1Per.Ui.Warn("Device is in ADB. Will auto-switch to download mode via the diag kick.");
            V1Per.Ui.Muted("When you see \"waiting for dl_diag\":");
            V1Per.Ui.Muted("  * Realme            — long-press ALL BUTTONS until the screen turns black");
            V1Per.Ui.Muted("  * Tecno/Infinix/Itel/others — long-press VOL DOWN + POWER");
            V1Per.Ui.Muted("Keep holding until the screen goes black, then release. Do NOT unplug.");
            Kick = true;
        }
        else if (mode != Mode.Download)
        {
            V1Per.Ui.Warn("Device is not in download mode.");
            V1Per.Ui.Muted("Enter download mode: hold VOL DOWN + POWER (Realme: ALL buttons)");
            V1Per.Ui.Muted("  from the home screen it reboots straight into download mode,");
            V1Per.Ui.Muted("  or power off first and plug in USB while holding the combo.");
            if (!await Terminal.ConfirmAsync("Continue once it's in download mode?"))
                return;
        }

        string? pkgDir = ResolvePackageDir(pkg);
        if (pkgDir is null)
        {
            Kick = false;
            return;
        }
        string? deviceDir = DeviceDir(pkg, device);
        if (deviceDir is not null)
            V1Per.Ui.Ok($"Device payload: {Path.GetFileName(deviceDir)} (exact fdl)");
        else if (device is not null)
            V1Per.Ui.Warn($"No device-specific payload for '{device}', using {pkg.Id} family defaults.");

        V1Per.Ui.Rule("Read carefully before proceeding");
        V1Per.Ui.Muted("If you see \"waiting for dl_diag connection\":");
        V1Per.Ui.Muted("  * Realme            — long-press ALL BUTTONS until the screen turns black");
        V1Per.Ui.Muted("  * Tecno/Infinix/Itel/others — long-press VOL DOWN + POWER");
        V1Per.Ui.Muted("If the device is detected, release all buttons immediately!");
        V1Per.Ui.Warn("Do not unplug the phone during unlock.");

        if (!await Terminal.ConfirmAsync("Start the unlock sequence?"))
        {
            V1Per.Ui.Muted("Cancelled.");
            Kick = false;
            return;
        }

        string work = PrepareWork(pkgDir, deviceDir, pkg);
        await RunUnlock(pkg, pkgDir, work);
        Kick = false;

        string miscFile = Path.Combine(work, "m.bin");
        if (File.Exists(miscFile))
        {
            bool unlocked = ReportUnlockState(miscFile);
            V1Per.Ui.Ok(unlocked
                ? "Bootloader appears UNLOCKED."
                : "Bootloader still appears LOCKED — try running unisoc unlock again.");
        }
        else
        {
            V1Per.Ui.Muted("No miscdata verification file was produced. Reboot the device to check.");
        }
    }

    /// <summary>Runs the full unlock sequence against the device in download mode.</summary>
    private static async Task RunUnlock(UnisocPackage pkg, string pkgDir, string work)
    {
        V1Per.Ui.Info("[1/3] Backing up and erasing splloader + uboot...");
        if (!await RunSpdDump(pkgDir, work, Base(true, pkg), "r splloader r uboot e splloader e splloader_bak reset"))
        {
            V1Per.Ui.Err("Device connection failed or timed out.");
            V1Per.Ui.Muted("If you saw \"FIND PORT FAILED\", reconnect the phone and rerun unisoc unlock.");
            return;
        }

        // Build the unlocker from the package's pre-captured splloader (if any),
        // otherwise from the splloader just dumped off the device.
        string splSource = pkg.SplLoaderBk ?? "splloader.bin";
        string unlocker = Path.Combine(work, "spl-unlock.bin");
        if (!File.Exists(unlocker))
        {
            V1Per.Ui.Info($"Generating spl-unlock.bin from {splSource}...");
            if (!await RunHelper(pkgDir, work, pkg, "gen_spl-unlock", splSource))
                return;
            if (!File.Exists(unlocker))
            {
                V1Per.Ui.Err("gen_spl-unlock did not produce spl-unlock.bin. Unlock aborted.");
                return;
            }
        }
        else
        {
            V1Per.Ui.Info("spl-unlock.bin already present, skipping generation.");
        }

        RenameIfExists(work, "splloader.bin", "u-boot-spl-16k-sign.bin");
        if (pkg.ChsizeUboot)
        {
            V1Per.Ui.Info("Sizing uboot.bin (chsize)...");
            await RunHelper(pkgDir, work, pkg, "chsize", "uboot.bin");
        }
        RenameIfExists(work, "uboot.bin", "uboot_bak.bin");

        V1Per.Ui.Info("[2/3] Flashing modified uboot...");
        if (!await RunSpdDump(pkgDir, work, Base(true, pkg), $"w uboot {pkg.Cboot} reset"))
        {
            V1Per.Ui.Err("Failed to flash modified uboot. Stopping — do NOT unplug.");
            return;
        }
        V1Per.Ui.Warn("Waiting 10s for device initialization...");
        await Task.Delay(10_000);

        V1Per.Ui.Info("Sending unlocker...");
        await RunSpdDump(pkgDir, work, new List<string> { "exec_addr", $"0x{pkg.ExecAddr:x}", "fdl", "spl-unlock.bin", $"0x{pkg.Fdl1Addr:x}" }, null);

        V1Per.Ui.Info("Checking unlock status...");
        await RunSpdDump(pkgDir, work, Base(false, pkg), "verbose 2 read_part miscdata 8192 64 m.bin reset");

        V1Per.Ui.Info($"Backing up {string.Join(" / ", pkg.BackupPartitions)}...");
        string backup = "r " + string.Join(" r ", pkg.BackupPartitions) + " reset";
        await RunSpdDump(pkgDir, work, Base(false, pkg), backup);

        V1Per.Ui.Info("[3/3] Restoring splloader and uboot...");
        string splRestore = File.Exists(Path.Combine(work, "u-boot-spl-16k-sign.bin")) ? "u-boot-spl-16k-sign.bin" : (pkg.SplLoaderBk ?? "splloader_bk.bin");
        string ubootRestore = File.Exists(Path.Combine(work, "uboot_bak.bin")) ? "uboot_bak.bin" : "uboot_bk.bin";
        string persist = pkg.ErasePersist ? " e persist" : "";
        await RunSpdDump(pkgDir, work, Base(false, pkg), $"w splloader {splRestore} w uboot {ubootRestore}{persist} w misc {pkg.MiscDone} reset");
    }

    private static void RenameIfExists(string dir, string src, string dst)
    {
        string srcPath = Path.Combine(dir, src);
        string dstPath = Path.Combine(dir, dst);
        if (File.Exists(srcPath))
        {
            File.Move(srcPath, dstPath, true);
            V1Per.Ui.Info($"renamed {src} → {dst}");
        }
        else
        {
            V1Per.Ui.Warn($"skip rename: {src} not found");
        }
    }

    /// <summary>Runs spd_dump (exe from the package dir) with cwd set to the work dir.</summary>
    private static async Task<bool> RunSpdDump(string pkgDir, string cwd, List<string> baseTokens, string? tail)
    {
        var tokens = new List<string>(baseTokens);
        if (!string.IsNullOrEmpty(tail))
            tokens.AddRange(tail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string output = await RunSpdDumpTokens(pkgDir, cwd, tokens);
        return output.Length > 0;
    }

    private static async Task<bool> RunHelper(string pkgDir, string cwd, UnisocPackage pkg, string exe, string arg)
    {
        string exeName = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";
        string? root = UnisocRoot();
        string? exePath = null;
        if (root is not null)
        {
            string shared = Path.Combine(root, "tools", pkg.ToolsGen, exeName);
            if (File.Exists(shared))
                exePath = shared;
        }
        exePath ??= Path.Combine(pkgDir, exeName);
        if (!File.Exists(exePath))
        {
            V1Per.Ui.Err($"{exe} not found (Unisoc/tools/{pkg.ToolsGen} or package).");
            return false;
        }
        V1Per.Ui.Info($"Running {Path.GetFileName(exePath)} {arg}");
        string output = await V1Per.ProcessRunner.RunLive(exePath, new[] { arg },
            l => { if (!string.IsNullOrWhiteSpace(l)) V1Per.Ui.Muted(Markup.Escape(l)); },
            120_000, cwd);
        return output.Length > 0;
    }

    /// <summary>Runs spd_dump.exe (shared root, or package-local fallback), streaming output.</summary>
    private static async Task<string> RunSpdDumpTokens(string pkgDir, string cwd, IReadOnlyList<string> tokens)
    {
        string? exe = ResolveSpdDump(pkgDir);
        if (exe is null)
        {
            V1Per.Ui.Err("spd_dump.exe not found in the Unisoc folder.");
            return string.Empty;
        }
        V1Per.Ui.Info($"spd_dump {string.Join(" ", tokens)}");
        try
        {
            return await V1Per.ProcessRunner.RunLive(exe, tokens,
                l => { if (!string.IsNullOrWhiteSpace(l)) V1Per.Ui.Muted(Markup.Escape(l)); ReportProgress(l); },
                600_000, cwd);
        }
        finally
        {
            Kick = false;
        }
    }

    /// <summary>Parses "[NN%]" style progress markers out of spd_dump output.</summary>
    private static void ReportProgress(string line)
    {
        if (Progress is null)
            return;
        var m = System.Text.RegularExpressions.Regex.Match(line, @"\[?\s*(\d{1,3})\s*%\]?");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int pct) && pct is >= 0 and <= 100)
            Progress(pct);
    }

    /// <summary>Reads m.bin (64 bytes from miscdata): all zeros = locked, data = unlocked.</summary>
    private static bool ReportUnlockState(string file)
    {
        byte[] data = File.ReadAllBytes(file);
        bool allZero = data.All(b => b == 0);
        if (allZero)
        {
            V1Per.Ui.Err("miscdata unlock bytes are all zero (LOCKED).");
            return false;
        }
        V1Per.Ui.Ok($"miscdata unlock bytes present ({data.Length} bytes, first=0x{data[0]:X2}).");
        return true;
    }
}