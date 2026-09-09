using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace v1per_wpf;

/// <summary>
/// Root pipeline ported from v1per.py: detects the kernel, picks FolkPatch
/// (below 5.10) or KernelSU-Next (5.10+), installs the manager, patches the
/// boot image on-device, unlocks the bootloader and flashes it.
/// </summary>
public static class Root
{
    private static readonly (int, int) KsuMinKernel = (5, 10);
    private static readonly Regex KernelRe = new(@"(\d+)\.(\d+)(?:\.(\d+))?");
    private static readonly Regex KmiRe = new(@"(android\d+)-(\d+\.\d+)", RegexOptions.IgnoreCase);

    public static async Task Run(string[] args)
    {
        string? serial = ReadyAdbDevice();
        if (serial is null)
        {
            V1Per.Ui.Err("No ready ADB device. Accept USB debugging and reconnect.");
            return;
        }

        V1Per.Ui.Info($"Device connected: {serial}");
        DevicesInfo(serial);

        string kernel = GetKernelVersion(serial);
        var tup = ParseKernelTuple(kernel);
        bool useFolk;
        if (tup is null)
        {
            V1Per.Ui.Warn($"Could not parse kernel version: {kernel}");
            V1Per.Ui.Warn("Defaulting to KernelSU-Next");
            useFolk = false;
        }
        else
        {
            useFolk = UsesFolk(tup.Value);
            if (useFolk)
                V1Per.Ui.Ok($"Kernel {tup.Value.Item1}.{tup.Value.Item2}.{tup.Value.Item3} — below 5.10, using FolkPatch");
            else
                V1Per.Ui.Ok($"Kernel {tup.Value.Item1}.{tup.Value.Item2}.{tup.Value.Item3} — 5.10 or above, using KernelSU-Next");
        }

        JsonElement? release;
        string managerLabel, apkLabel;
        if (useFolk)
        {
            V1Per.Ui.Info("Fetching release info for FolkPatch...");
            release = await Github.FetchLatest(Github.FolkReleasesUrl);
            managerLabel = "FolkPatch";
            apkLabel = "FolkPatch.apk";
        }
        else
        {
            V1Per.Ui.Info("Fetching release info for KernelSU-Next...");
            release = await Github.FetchLatest(Github.KsuReleasesUrl);
            managerLabel = "KernelSU-Next";
            apkLabel = "KernelSU.apk";
        }

        var apk = release is { } rel ? Github.PickApk(rel, useFolk ? "folkpatch" : "universal", "release") : null;
        if (apk is null)
        {
            V1Per.Ui.Err("Failed to fetch release. Check internet.");
            return;
        }
        string tagName = release!.Value.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "unknown" : "unknown";
        V1Per.Ui.Ok($"Found version: {tagName}");

        V1Per.Ui.Info($"Downloading {apkLabel}...");
        string? apkPath = await Github.DownloadAsync(Github.Url(apk.Value), Github.Name(apk.Value));
        if (apkPath is null)
        {
            V1Per.Ui.Err("Download failed.");
            return;
        }

        V1Per.Ui.Info($"Installing {managerLabel} on device...");
        if (!InstallApk(serial, apkPath))
        {
            V1Per.Ui.Err("Install failed.");
            return;
        }
        V1Per.Ui.Ok("Installation successful!");

        V1Per.Ui.Info("Patching boot...");
        string bootImg = args.Length > 0 ? string.Join(" ", args).Trim('"', '\'') : await Terminal.PromptAsync("Enter boot.img path: ");
        if (bootImg.Length == 0 || !File.Exists(bootImg))
        {
            V1Per.Ui.Err($"File not found: {bootImg}");
            return;
        }

        string partition;
        string? patchedLocal;
        if (useFolk)
        {
            if (Path.GetFileName(bootImg).ToLowerInvariant().Contains("init_boot"))
            {
                V1Per.Ui.Err("FolkPatch needs boot.img, not init_boot.img.");
                return;
            }
            partition = "boot";
            patchedLocal = await AutoPatchWithKptools(serial, bootImg);
        }
        else
        {
            partition = PartitionFromImage(bootImg);
            string abi = (V1Per.ProcessRunner.Adb(serial, new[] { "shell", "getprop", "ro.product.cpu.abi" }, 8_000) ?? "N/A").Trim();
            patchedLocal = await AutoPatchWithKsud(serial, bootImg, release.Value, abi);
        }

        if (patchedLocal is null)
        {
            V1Per.Ui.Err("Auto-patch failed.");
            return;
        }
        V1Per.Ui.Ok($"Patching complete: {patchedLocal}");

        await FlashPatched(serial, partition, patchedLocal);
    }

    // ── device / kernel helpers ─────────────────────────────────────

    /// <summary>Patches a boot image on-device without fetching/installing the manager (for Unisoc flow).</summary>
    public static async Task<string?> PatchOnly(string serial, string bootImg)
    {
        if (!File.Exists(bootImg))
        {
            V1Per.Ui.Err($"File not found: {bootImg}");
            return null;
        }

        string kernel = GetKernelVersion(serial);
        var tup = ParseKernelTuple(kernel);
        bool useFolk;
        if (tup is null)
        {
            V1Per.Ui.Warn($"Could not parse kernel version: {kernel}");
            V1Per.Ui.Warn("Defaulting to KernelSU-Next");
            useFolk = false;
        }
        else
        {
            useFolk = UsesFolk(tup.Value);
            if (useFolk)
                V1Per.Ui.Ok($"Kernel {tup.Value.Item1}.{tup.Value.Item2}.{tup.Value.Item3} — below 5.10, using FolkPatch");
            else
                V1Per.Ui.Ok($"Kernel {tup.Value.Item1}.{tup.Value.Item2}.{tup.Value.Item3} — 5.10 or above, using KernelSU-Next");
        }

        if (useFolk)
        {
            if (Path.GetFileName(bootImg).ToLowerInvariant().Contains("init_boot"))
            {
                V1Per.Ui.Err("FolkPatch needs boot.img, not init_boot.img.");
                return null;
            }
            return await AutoPatchWithKptools(serial, bootImg);
        }
        else
        {
            var release = await Github.FetchLatest(Github.KsuReleasesUrl);
            if (release is null)
            {
                V1Per.Ui.Err("Failed to fetch KernelSU-Next release.");
                return null;
            }
            string abi = (V1Per.ProcessRunner.Adb(serial, new[] { "shell", "getprop", "ro.product.cpu.abi" }, 8_000) ?? "N/A").Trim();
            return await AutoPatchWithKsud(serial, bootImg, release.Value, abi);
        }
    }

    private static string? ReadyAdbDevice()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string output = V1Per.ProcessRunner.Run("adb", new[] { "devices" }, 8_000);
            foreach (string line in output.Split('\n').Skip(1))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1] == "device")
                    return parts[0];
            }
            if (attempt == 0 && !string.IsNullOrWhiteSpace(output))
            {
                V1Per.Ui.Muted($"adb devices → {output.Replace("\n", " | ")}");
            }
            Thread.Sleep(800);
        }
        return null;
    }

    private static void DevicesInfo(string serial)
    {
        string Prop(string p) => (V1Per.ProcessRunner.Adb(serial, new[] { "shell", "getprop", p }, 8_000) ?? "N/A").Trim();
        string kernel = (V1Per.ProcessRunner.Adb(serial, new[] { "shell", "uname", "-r" }, 8_000) ?? "N/A").Trim();

        var table = new Table().HideHeaders().Border(TableBorder.None).Expand();
        table.AddColumn(new TableColumn("k"));
        table.AddColumn(new TableColumn("v"));
        void Row(string k, string v) => table.AddRow($"[#{V1Per.Ui.PinkRed.ToHex()}]{k}[/]", $"[#{V1Per.Ui.LightBlue.ToHex()}]{v}[/]");
        Row("Model", Prop("ro.product.model"));
        Row("Android", Prop("ro.build.version.release"));
        Row("SDK", Prop("ro.build.version.sdk"));
        Row("Kernel", kernel);
        V1Per.Ui.Write(table);
    }

    private static string GetKernelVersion(string serial)
    {
        string uname = (V1Per.ProcessRunner.Adb(serial, new[] { "shell", "uname", "-r" }, 8_000) ?? string.Empty).Trim();
        if (uname.Length > 0 && !uname.Equals("N/A", StringComparison.Ordinal))
            return uname;

        string ver = (V1Per.ProcessRunner.Adb(serial, new[] { "shell", "cat", "/proc/version" }, 8_000) ?? string.Empty).Trim();
        if (ver.Length > 0)
        {
            var m = Regex.Match(ver, @"Linux version (\S+)");
            return m.Success ? m.Groups[1].Value : ver[..Math.Min(80, ver.Length)];
        }
        return "N/A";
    }

    private static (int, int, int)? ParseKernelTuple(string text)
    {
        var m = KernelRe.Match(text ?? string.Empty);
        if (!m.Success)
            return null;
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    private static bool UsesFolk((int, int, int) tup)
    {
        int major = KsuMinKernel.Item1, minor = KsuMinKernel.Item2;
        return tup.Item1 < major || (tup.Item1 == major && tup.Item2 < minor);
    }

    private static bool InstallApk(string serial, string apkPath)
    {
        string result = V1Per.ProcessRunner.Adb(serial, new[] { "install", "-r", apkPath }, 120_000);
        if (!string.IsNullOrEmpty(result) && result.Contains("success", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(result))
            V1Per.Ui.Muted(Markup.Escape(result.Replace("\n", " | ")[..Math.Min(240, result.Length)]));
        return false;
    }

    private static string PartitionFromImage(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.Contains("init_boot"))
            return "init_boot";
        if (name.Contains("vendor_boot"))
            return "vendor_boot";
        return "boot";
    }

    // ── ksud / kptools patching ─────────────────────────────────────

    private static JsonElement? PickKsud(JsonElement release, string abi)
    {
        string want = abi switch
        {
            "x86_64" => "ksud-x86_64-linux-android",
            "armeabi-v7a" or "armeabi" => "ksud-armv7-linux-androideabi",
            _ => "ksud-aarch64-linux-android",
        };
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (Github.Name(asset) == want)
                return asset;
        }
        foreach (var asset in assets.EnumerateArray())
        {
            string name = Github.Name(asset);
            if (name.StartsWith("ksud-") && name.Contains("android"))
                return asset;
        }
        return null;
    }

    private static string? ParseKmi(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = KmiRe.Match(text.Replace("_", "-"));
        return m.Success ? $"{m.Groups[1].Value.ToLowerInvariant()}-{m.Groups[2].Value}" : null;
    }

    private static string? DetectKmi(string serial, string? remoteKsud)
    {
        if (remoteKsud is not null)
        {
            string out0 = V1Per.ProcessRunner.Adb(serial, new[] { "shell", remoteKsud, "boot-info", "current-kmi" }, 20_000) ?? string.Empty;
            string? kmi = ParseKmi(out0);
            if (kmi is not null)
                return kmi;
        }
        string? kmi2 = ParseKmi(V1Per.ProcessRunner.Adb(serial, new[] { "shell", "uname", "-r" }, 8_000));
        if (kmi2 is not null)
            return kmi2;
        foreach (string prop in new[] { "ro.kernel.version", "ro.boot.kernel", "ro.build.version.release" })
        {
            string? kmi3 = ParseKmi(V1Per.ProcessRunner.Adb(serial, new[] { "shell", "getprop", prop }, 8_000));
            if (kmi3 is not null)
                return kmi3;
        }
        return ParseKmi(V1Per.ProcessRunner.Adb(serial, new[] { "shell", "cat", "/proc/version" }, 8_000));
    }

    private static async Task<string?> AutoPatchWithKsud(string serial, string imagePath, JsonElement release, string abi)
    {
        var ksud = PickKsud(release, abi);
        if (ksud is null)
        {
            V1Per.Ui.Err("No on-device ksud in this release.");
            return null;
        }
        string? ksudLocal = await Github.DownloadAsync(Github.Url(ksud.Value), Github.Name(ksud.Value));
        if (ksudLocal is null)
            return null;

        const string remoteKsud = "/data/local/tmp/ksud";
        const string remoteIn = "/sdcard/Download/v1per_input.img";
        const string remoteOutName = "kernelsu_patched.img";
        string remoteOut = $"/sdcard/Download/{remoteOutName}";

        V1Per.ProcessRunner.Adb(serial, new[] { "shell", "mkdir", "-p", "/sdcard/Download" }, 10_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "push", ksudLocal, remoteKsud }, 60_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "push", imagePath, remoteIn }, 120_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "shell", "chmod", "755", remoteKsud }, 10_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "shell", "rm", "-f", remoteOut }, 10_000);

        V1Per.Ui.Info("Reading KMI...");
        string? kmi = DetectKmi(serial, remoteKsud);
        if (kmi is not null)
            V1Per.Ui.Ok($"KMI: {kmi}");
        else
            V1Per.Ui.Warn("No KMI string on this kernel. Letting ksud read it from the image.");

        var patchArgs = new List<string>
        {
            "shell", remoteKsud, "boot-patch", "-b", remoteIn, "-o", "/sdcard/Download",
            "--out-name", remoteOutName,
        };
        if (kmi is not null)
        {
            patchArgs.Add("--kmi");
            patchArgs.Add(kmi);
        }

        V1Per.Ui.Info("Waiting for patched image...");
        string result = V1Per.ProcessRunner.Adb(serial, patchArgs, 180_000) ?? string.Empty;
        string exists = V1Per.ProcessRunner.Adb(serial, new[] { "shell", "ls", remoteOut }, 10_000) ?? string.Empty;
        if (exists.Length == 0 || exists.Contains("No such"))
        {
            if (result.Length > 0)
                foreach (string line in result.Split('\n').TakeLast(8))
                    V1Per.Ui.Muted(line);
            V1Per.Ui.Err("ksud did not produce a patched image.");
            return null;
        }

        string patchedLocal = Path.Combine(Github.DownloadsHome(), "patched_boot.img");
        V1Per.ProcessRunner.Adb(serial, new[] { "pull", remoteOut, patchedLocal }, 120_000);
        if (!File.Exists(patchedLocal) || new FileInfo(patchedLocal).Length < 4096)
        {
            V1Per.Ui.Err("Failed to pull patched image.");
            return null;
        }
        return patchedLocal;
    }

    private static async Task<string?> AutoPatchWithKptools(string serial, string imagePath)
    {
        var release = await Github.FetchLatest(Github.KpReleasesUrl);
        if (release is null)
            return null;
        var tools = Github.PickAsset(release.Value, "kptools-android");
        var kpimg = Github.PickAsset(release.Value, "kpimg-android");
        if (tools is null || kpimg is null)
        {
            V1Per.Ui.Err("No kptools/kpimg in KernelPatch release.");
            return null;
        }

        string? kptoolsLocal = await Github.DownloadAsync(Github.Url(tools.Value), Github.Name(tools.Value));
        string? kpimgLocal = await Github.DownloadAsync(Github.Url(kpimg.Value), Github.Name(kpimg.Value));
        if (kptoolsLocal is null || kpimgLocal is null)
            return null;

        const string remoteBin = "/data/local/tmp/kptools";
        const string remoteKpimg = "/data/local/tmp/kpimg-android";
        const string work = "/data/local/tmp/v1per_kp";
        const string remoteIn = $"{work}/boot.img";
        string remoteOut = "/sdcard/Download/folkpatch_patched.img";

        V1Per.ProcessRunner.Adb(serial, new[] { "shell", "rm", "-rf", work }, 10_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "shell", "mkdir", "-p", work }, 10_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "shell", "mkdir", "-p", "/sdcard/Download" }, 10_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "push", kptoolsLocal, remoteBin }, 60_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "push", kpimgLocal, remoteKpimg }, 60_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "push", imagePath, remoteIn }, 120_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "shell", "chmod", "755", remoteBin }, 10_000);
        V1Per.ProcessRunner.Adb(serial, new[] { "shell", "rm", "-f", remoteOut }, 10_000);

        V1Per.Ui.Info("Waiting for patched image...");
        V1Per.ProcessRunner.Adb(serial, new[]
        {
            "shell", remoteBin, "-p", "--image", remoteIn, "--skey", "su",
            "--kpimg", remoteKpimg, "--out", remoteOut,
        }, 180_000);

        string exists = V1Per.ProcessRunner.Adb(serial, new[] { "shell", "ls", remoteOut }, 10_000) ?? string.Empty;
        if (exists.Length == 0 || exists.Contains("No such"))
        {
            V1Per.ProcessRunner.Adb(serial, new[] { "shell", "sh", "-c", $"cd {work} && {remoteBin} unpack boot.img" }, 60_000);
            string kernelPath = $"{work}/kernel";
            string hasKernel = V1Per.ProcessRunner.Adb(serial, new[] { "shell", "ls", kernelPath }, 10_000) ?? string.Empty;
            if (hasKernel.Length == 0 || hasKernel.Contains("No such"))
            {
                V1Per.Ui.Err("kptools did not unpack a kernel.");
                return null;
            }
            V1Per.ProcessRunner.Adb(serial, new[]
            {
                "shell", remoteBin, "-p", "--image", kernelPath, "--skey", "su",
                "--kpimg", remoteKpimg, "--out", kernelPath,
            }, 180_000);
            V1Per.ProcessRunner.Adb(serial, new[] { "shell", "sh", "-c", $"cd {work} && {remoteBin} repack boot.img" }, 60_000);
            foreach (string candidate in new[] { $"{work}/new-boot.img", $"{work}/boot.img" })
            {
                string listed = V1Per.ProcessRunner.Adb(serial, new[] { "shell", "ls", candidate }, 10_000) ?? string.Empty;
                if (listed.Length > 0 && !listed.Contains("No such"))
                {
                    remoteOut = candidate;
                    break;
                }
            }
        }

        string patchedLocal = Path.Combine(Github.DownloadsHome(), "patched_boot.img");
        V1Per.ProcessRunner.Adb(serial, new[] { "pull", remoteOut, patchedLocal }, 120_000);
        if (!File.Exists(patchedLocal) || new FileInfo(patchedLocal).Length < 4096)
        {
            V1Per.Ui.Err("Failed to pull patched image.");
            return null;
        }
        return patchedLocal;
    }

    // ── flash + unlock ──────────────────────────────────────────────

    public static async Task FlashPatched(string serial, string partition, string patchedLocal)
    {
        V1Per.Ui.Info("Rebooting device to bootloader mode...");
        V1Per.ProcessRunner.Adb(serial, new[] { "reboot", "bootloader" }, 15_000);

        V1Per.Ui.Info("Waiting for device in fastboot mode...");
        string? fbSerial = null;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(2000);
            var devs = FastbootDevices();
            if (devs.Count > 0)
            {
                fbSerial = devs[0];
                break;
            }
        }
        if (fbSerial is null)
        {
            V1Per.Ui.Err("Fastboot device not detected.");
            return;
        }
        V1Per.Ui.Ok("Device detected in fastboot mode");

        bool unlocked = IsUnlocked(fbSerial);
        if (unlocked)
        {
            V1Per.Ui.Ok("Bootloader is UNLOCKED");
        }
        else
        {
            V1Per.Ui.Warn("Bootloader is LOCKED");
            V1Per.Ui.Info("Attempting to unlock bootloader...");
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                V1Per.Ui.Info($"Attempt {attempt}/5");
                V1Per.Ui.Warn("Please confirm unlock on your device (press VOLUME UP when prompted)");
                string unlockFb = V1Per.ProcessRunner.Fastboot(fbSerial, new[] { "flashing", "unlock" }, 20_000);
                if (unlockFb.Length == 0 || unlockFb.Contains("unknown", StringComparison.OrdinalIgnoreCase))
                    V1Per.ProcessRunner.Fastboot(fbSerial, new[] { "oem", "unlock" }, 20_000);
                await Task.Delay(4000);
                var devs = FastbootDevices();
                if (devs.Count > 0)
                    fbSerial = devs[0];
                if (IsUnlocked(fbSerial))
                {
                    V1Per.Ui.Ok("Bootloader successfully unlocked");
                    unlocked = true;
                    break;
                }
                if (attempt < 5)
                    V1Per.Ui.Warn("Unlock attempt failed, retrying...");
            }
            if (!unlocked)
                V1Per.Ui.Warn("Could not verify unlock status. Continuing...");
        }

        V1Per.Ui.Info($"Flashing partition \"{partition}\" with {patchedLocal}");
        string flashResult = V1Per.ProcessRunner.Fastboot(fbSerial, new[] { "flash", partition, patchedLocal }, 90_000);
        bool ok = FlashLooksOk(flashResult);
        if (!ok)
        {
            foreach (string slot in new[] { "a", "b" })
            {
                flashResult = V1Per.ProcessRunner.Fastboot(fbSerial, new[] { "flash", $"{partition}_{slot}", patchedLocal }, 90_000);
                if (FlashLooksOk(flashResult))
                {
                    ok = true;
                    break;
                }
            }
        }
        if (ok)
        {
            V1Per.Ui.Ok("Flash successful, rebooting device...");
            V1Per.ProcessRunner.Fastboot(fbSerial, new[] { "reboot" }, 10_000);
        }
        else
        {
            V1Per.Ui.Err("Flash may have failed.");
            if (flashResult.Length > 0)
                foreach (string line in flashResult.Split('\n').Take(12))
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

    private static bool IsUnlocked(string serial)
    {
        string output = V1Per.ProcessRunner.Fastboot(serial, new[] { "getvar", "unlocked" }, 10_000);
        if (string.IsNullOrEmpty(output))
            return false;
        foreach (string line in output.Split('\n'))
        {
            if (line.ToLowerInvariant().Replace("(bootloader)", "").Contains("unlocked: yes"))
                return true;
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
}