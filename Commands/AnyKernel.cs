using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace v1per_wpf;

/// <summary>
/// GKI AnyKernel flasher, modeled after HorizonKernelFlasher: over adb root it
/// extracts the zip's own META-INF update-binary script and runs it
/// ("sh update-binary 3 1 &lt;zip&gt;"), letting AnyKernel handle partition/slot
/// detection and boot repacking. Falls back to fastboot flash of a boot.img
/// payload when the device isn't rooted.
/// </summary>
public static class AnyKernel
{
    private static readonly string[] KnownPartitions =
        { "boot", "init_boot", "vendor_boot", "dtbo", "vendor_dtbo", "recovery" };

    public static async Task Run(string[] args)
    {
        V1Per.Ui.Rule("GKI AnyKernel flasher");
        string zipPath = args.Length > 0
            ? string.Join(" ", args).Trim('"', '\'')
            : await Terminal.PromptAsync("AnyKernel zip path: ");

        if (!File.Exists(zipPath))
        {
            V1Per.Ui.Err($"File not found: {zipPath}");
            return;
        }

        string targetPartition = string.Empty;
        var bootImages = new List<string>();
        var rawKernels = new List<string>();
        bool hasUpdateBinary = false;

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                    continue;
                string name = Path.GetFileName(entry.FullName);

                if (name.Equals("anykernel.sh", StringComparison.OrdinalIgnoreCase))
                {
                    using var sr = new StreamReader(entry.Open());
                    targetPartition = ParseBlock(sr.ReadToEnd());
                }
                if (entry.FullName.EndsWith("update-binary", StringComparison.OrdinalIgnoreCase))
                    hasUpdateBinary = true;
                if (name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                {
                    string p = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                    if (KnownPartitions.Contains(p))
                        bootImages.Add(entry.FullName);
                }
                if (Regex.IsMatch(name,
                        @"^(Image(\..+)?|zImage(-dtb)?|boot\.img|init_boot\.img|vendor_boot\.img)$",
                        RegexOptions.IgnoreCase))
                    rawKernels.Add(entry.FullName);
            }
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Failed to read zip: {ex.Message}");
            V1Per.Ui.Muted("Make sure it's a valid AnyKernel zip archive.");
            return;
        }

        V1Per.Ui.Info($"Target partition (anykernel.sh): [bold]{(targetPartition.Length == 0 ? "auto" : targetPartition)}[/]");
        if (bootImages.Count > 0)
        {
            V1Per.Ui.Ok($"Boot payloads found: {string.Join(", ", bootImages.Select(Path.GetFileName))}");
            if (rawKernels.Count > 0)
                V1Per.Ui.Muted($"Raw kernels also present: {string.Join(", ", rawKernels.Select(Path.GetFileName).Take(4))}");
        }
        else if (rawKernels.Count > 0)
        {
            V1Per.Ui.Info($"Raw kernel present: {string.Join(", ", rawKernels.Select(Path.GetFileName).Take(4))}");
        }

        bool root = HasRoot();
        string mode;
        if (root)
            mode = (await Terminal.PromptAsync("Flash via [[a]]db root (AnyKernel script) or [[f]]astboot? [[a/f]]: ")).Trim().ToLowerInvariant();
        else
        {
            V1Per.Ui.Warn("No root detected via 'adb shell su -c id'.");
            V1Per.Ui.Muted("If you ARE rooted, grant ADB shell root access first:");
            V1Per.Ui.Muted("  KernelSU: Superuser → allow 'shell' (UID 2000)");
            V1Per.Ui.Muted("  Magisk:   Superuser → allow shell when prompted");
            mode = "f";
        }

        if (mode is "a" or "adb")
        {
            if (!hasUpdateBinary)
            {
                V1Per.Ui.Err("This zip has no META-INF update-binary script.");
                return;
            }
            await FlashViaAnyKernelScript(zipPath);
            return;
        }

        // ── fastboot path: extract a boot.img payload and flash it ──
        if (bootImages.Count == 0)
        {
            V1Per.Ui.Err("No complete boot.img payload in this zip for fastboot.");
            V1Per.Ui.Muted("This zip has a raw kernel — it needs root to flash (AnyKernel update-binary).");
            V1Per.Ui.Muted("Root first: type 'root' with your stock boot.img path, then retry 'anykernel'.");
            return;
        }

        string? chosen = PickPayload(bootImages, targetPartition);
        string partition = TargetPartitionName(chosen, targetPartition);
        V1Per.Ui.Info($"Flashing [bold]{Path.GetFileName(chosen)}[/] → [bold]{partition}[/]");

        string local = Path.Combine(Github.DownloadsHome(), $"{partition}_anykernel.img");
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(chosen, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                V1Per.Ui.Err($"Could not find {chosen} in the zip.");
                return;
            }
            entry.ExtractToFile(local, true);
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Failed to extract payload: {ex.Message}");
            return;
        }
        V1Per.Ui.Ok($"Extracted: {local}");
        await FlashViaFastboot(partition, local);
    }

    private static bool HasRoot()
    {
        string output = V1Per.ProcessRunner.Adb(null, new[] { "shell", "su", "-c", "id" }, 8_000) ?? string.Empty;
        return output.Contains("uid=0");
    }

    private static Task FlashViaAnyKernelScript(string zipPath)
    {
        const string dir = "/data/local/tmp/v1per_ak";
        const string remoteZip = $"{dir}/ak.zip";
        const string remoteBin = $"{dir}/update-binary";

        V1Per.Ui.Info("Preparing /data/local/tmp/v1per_ak...");
        V1Per.ProcessRunner.Adb(null, new[] { "shell", "mkdir", "-p", dir }, 10_000);

        V1Per.Ui.Info("Pushing AnyKernel zip to device...");
        V1Per.ProcessRunner.Adb(null, new[] { "push", zipPath, remoteZip }, 120_000);

        V1Per.Ui.Info("Extracting update-binary from zip...");
        string localBin = Path.Combine(Github.DownloadsHome(), "update-binary");
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("update-binary", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                V1Per.Ui.Err("No update-binary found in zip.");
                return Task.CompletedTask;
            }
            entry.ExtractToFile(localBin, true);
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Failed to extract update-binary: {ex.Message}");
            return Task.CompletedTask;
        }

        V1Per.ProcessRunner.Adb(null, new[] { "push", localBin, remoteBin }, 60_000);
        V1Per.ProcessRunner.Adb(null, new[] { "shell", "chmod", "755", remoteBin }, 10_000);

        // Pick an ash to run the script: KernelSU-Next busybox → the zip's own
        // busybox (works on KoWSU/Magisk/KernelSU) → system sh.
        string shellBin;
        string ksuCheck = V1Per.ProcessRunner.Adb(null, new[] { "shell", "su", "-c", "[ -f /data/adb/ksu/bin/busybox ] && echo yes" }, 10_000) ?? string.Empty;
        if (ksuCheck.Contains("yes"))
        {
            shellBin = "/data/adb/ksu/bin/busybox ash";
        }
        else
        {
            string localBb = Path.Combine(Github.DownloadsHome(), "busybox");
            bool hasZipBb = false;
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var bbEntry = zip.Entries.FirstOrDefault(x =>
                    x.FullName.EndsWith("tools/busybox", StringComparison.OrdinalIgnoreCase));
                if (bbEntry is not null)
                {
                    bbEntry.ExtractToFile(localBb, true);
                    hasZipBb = true;
                }
            }
            catch (Exception)
            {
                // ignore
            }

            if (hasZipBb)
            {
                V1Per.ProcessRunner.Adb(null, new[] { "push", localBb, $"{dir}/busybox" }, 60_000);
                V1Per.ProcessRunner.Adb(null, new[] { "shell", "chmod", "755", $"{dir}/busybox" }, 10_000);
                shellBin = $"{dir}/busybox ash";
            }
            else
            {
                shellBin = "sh";
            }
        }

        V1Per.Ui.Warn("Flashing via AnyKernel update-binary (root). Do not unplug!");
        string cmd = $"cd {dir} && AKHOME={dir}/tmp {shellBin} {remoteBin} 3 1 {remoteZip}";
        string result = V1Per.ProcessRunner.Adb(null, new[] { "shell", "su", "-c", $"\"{cmd}\"" }, 300_000) ?? string.Empty;
        if (result.Length > 0)
        {
            var shown = 0;
            foreach (string rawLine in result.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;
                if (line.Contains("records out"))
                {
                    V1Per.Ui.Ok("Boot partition flashed.");
                    continue;
                }
                if (!IsImportant(line) || shown >= 12)
                    continue;
                V1Per.Ui.Muted(Markup.Escape(line));
                shown++;
            }
        }
        else
        {
            V1Per.Ui.Ok("update-binary finished.");
        }

        V1Per.Ui.Ok("Flash complete, rebooting device...");
        V1Per.ProcessRunner.Adb(null, new[] { "reboot" }, 10_000);
        return Task.CompletedTask;
    }

    private static readonly string[] ImportantKeywords =
    {
        "repack", "records", "copied", "done", "flash", "success", "error",
        "fail", "no such", "invalid", "wrong", "=>", "mounted",
    };

    private static readonly string[] NoiseSubstrings =
    {
        "header_ver", "kernel_sz", "ramdisk_sz", "os_version", "os_patch_level",
        "pagesize", "cmdline", "kernel_fmt", "ramdisk_fmt", "asn.1", "vbmeta",
        "/proc/self/fd", "same file", "cpio",
    };

    private static bool IsImportant(string line)
    {
        if (NoiseSubstrings.Any(n => line.Contains(n, StringComparison.OrdinalIgnoreCase)))
            return false;
        return ImportantKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    // ── fastboot helpers ────────────────────────────────────────────

    private static string ParseBlock(string sh)
    {
        var m = Regex.Match(sh, @"block=(?:/dev/block/[^;]+/by-name/)?([a-zA-Z0-9_]+)\s*;", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant().TrimEnd('_') : string.Empty;
    }

    private static string? PickPayload(List<string> bootImages, string target)
    {
        foreach (string img in bootImages)
        {
            if (Path.GetFileNameWithoutExtension(img).ToLowerInvariant() == target)
                return img;
        }
        foreach (string img in bootImages)
        {
            if (img.ToLowerInvariant().EndsWith("boot.img"))
                return img;
        }
        return bootImages[0];
    }

    private static string TargetPartitionName(string? image, string target)
    {
        if (image is not null)
        {
            string fromName = Path.GetFileNameWithoutExtension(image).ToLowerInvariant();
            if (KnownPartitions.Contains(fromName))
                return fromName;
        }
        return target.Length > 0 ? target : "boot";
    }

    private static async Task FlashViaFastboot(string partition, string image)
    {
        V1Per.Ui.Info("Rebooting device to bootloader mode...");
        V1Per.ProcessRunner.Adb(null, new[] { "reboot", "bootloader" }, 15_000);

        V1Per.Ui.Info("Waiting for device in fastboot mode...");
        string? serial = null;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(2000);
            var devs = FastbootDevices();
            if (devs.Count > 0)
            {
                serial = devs[0];
                break;
            }
        }
        if (serial is null)
        {
            V1Per.Ui.Err("Fastboot device not detected.");
            return;
        }
        V1Per.Ui.Ok("Device detected in fastboot mode");

        V1Per.Ui.Info($"Flashing partition \"{partition}\" with {Path.GetFileName(image)}");
        string result = V1Per.ProcessRunner.Fastboot(serial, new[] { "flash", partition, image }, 90_000);
        bool ok = FlashLooksOk(result);
        if (!ok)
        {
            foreach (string slot in new[] { "a", "b" })
            {
                result = V1Per.ProcessRunner.Fastboot(serial, new[] { "flash", $"{partition}_{slot}", image }, 90_000);
                if (FlashLooksOk(result))
                {
                    ok = true;
                    break;
                }
            }
        }
        if (ok)
        {
            V1Per.Ui.Ok("Flash successful, rebooting device...");
            V1Per.ProcessRunner.Fastboot(serial, new[] { "reboot" }, 10_000);
        }
        else
        {
            V1Per.Ui.Err("Flash may have failed.");
            if (result.Length > 0)
                foreach (string line in result.Split('\n').Take(12))
                    V1Per.Ui.Muted(line);
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

    private static bool FlashLooksOk(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return false;
        string lower = output.ToLowerInvariant();
        if (lower.Contains("failed") || lower.Contains("error:") || lower.Contains("unknown partition"))
            return false;
        return lower.Contains("okay") || lower.Contains("finished") || lower.Contains("sending");
    }
}