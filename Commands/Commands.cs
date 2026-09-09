using System.Diagnostics;
using System.IO;
using Spectre.Console;

namespace v1per_wpf;

/// <summary>Native implementations of the V1Per CLI commands.</summary>
public static class Commands
{
    public static void Bait()
    {
        V1Per.Ui.Warn("Nice dump. This is V1PER, an adb + fastboot wrapper. No hidden kit, no master unlock key.");
    }

    /// <summary>Launches the bundled scrcpy (mirrors the phone screen on desktop).</summary>
    public static void Scrcpy(string[] args)
    {
        string? exe = V1Per.ProcessRunner.Locate("scrcpy", "scrcpy.exe");
        if (exe is null)
        {
            V1Per.Ui.Err("scrcpy.exe not found next to the app.");
            V1Per.Ui.Muted("Expected folder: scrcpy/scrcpy.exe");
            return;
        }

        string? serial = null;
        if (args.Length > 0)
        {
            serial = string.Join(" ", args).Trim('"', '\'');
        }
        else
        {
            string adbDevices = V1Per.ProcessRunner.Run("adb", new[] { "devices" }, 8_000);
            foreach (string line in adbDevices.Split('\n').Skip(1))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1] == "device")
                {
                    serial = parts[0];
                    break;
                }
            }
        }

        if (serial is null)
        {
            V1Per.Ui.Err("No ready ADB device found to mirror.");
            V1Per.Ui.Muted("Connect the phone, enable USB debugging, then try again.");
            return;
        }

        V1Per.Ui.Info($"Launching scrcpy for {serial}...");
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            // scrcpy finds adb on PATH; make our platform-tools available.
            string? tools = V1Per.ProcessRunner.LocateDir("platform-tools");
            if (tools is not null)
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.Environment["PATH"] = tools + Path.PathSeparator + path;
            }
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
            psi.ArgumentList.Add("--stay-awake");
            var proc = Process.Start(psi);
            if (proc is null)
            {
                V1Per.Ui.Err("Failed to start scrcpy.");
                return;
            }
            V1Per.Ui.Ok($"scrcpy started (PID {proc.Id}) in its own window.");
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Failed to start scrcpy: {ex.Message}");
        }
    }

    public static void Help()
    {
        var table = new Table
        {
            Border = TableBorder.Rounded,
            BorderStyle = new Style(V1Per.Ui.PinkRed),
            Title = new TableTitle("V1Per commands", new Style(V1Per.Ui.LightBlue)),
        };
        table.AddColumn(new TableColumn(new Markup("[bold]Command[/]")).Centered());
        table.AddColumn(new TableColumn(new Markup("[bold]What it does[/]")).Centered());

        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]force[/]", "Force an MTK device into Fastboot via serial handshake");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]devices[/]", "Detect ADB + Fastboot devices and show info");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]root[/]", "Root: FolkPatch (\u22645.4) or KernelSU-Next (\u22655.10)");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]scatter[/]", "Flash firmware images from an MTK scatter file");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]anykernel[/]", "Flash a GKI AnyKernel zip (fastboot or adb root)");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]drivers[/]", "Browse & download chipset drivers to Downloads");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]scrcpy[/]", "Mirror the phone screen via bundled scrcpy");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]unisoc[/]", "Unisoc/Spreadtrum: unlock, dump, flash, root (CVE-2022-38694)");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]demo[/]", "Widget showcase (grid, tree, charts)");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]help[/]", "Show this list");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]cls[/]", "Clear the screen");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]about[/]", "About V1Per");
        table.AddRow($"[#{V1Per.Ui.LightBlue.ToHex()}]exit[/]", "Quit");

        V1Per.Ui.Write(table);
    }

    public static void About()
    {
        V1Per.Ui.Write(new Panel(
                $"[bold][#{V1Per.Ui.PinkRed.ToHex()}]V1Per[/][/]\nAndroid Servicing Toolkit (WPF)\n\n"
                + $"[#{V1Per.Ui.SlimeGreen.ToHex()}][[+]][/] MTK force fastboot via serial handshake\n"
                + $"[#{V1Per.Ui.SlimeGreen.ToHex()}][[+]][/] Device detection over ADB / Fastboot\n"
                + $"[#{V1Per.Ui.SlimeGreen.ToHex()}][[+]][/] Spectre.Console widget showcase\n\n"
                + "[grey]Only use this on a device you own.[/]")
            .Header($"about ({V1Per.Ui.PinkRed.ToHex()})")
            .BorderColor(V1Per.Ui.PinkRed)
            .Border(BoxBorder.Rounded));
    }

    public static void Devices()
    {
        V1Per.Ui.Rule("Devices");

        string adb = V1Per.ProcessRunner.Run("adb", new[] { "devices", "-l" }, 8_000);
        bool any = false;
        if (!string.IsNullOrWhiteSpace(adb))
        {
            foreach (string line in adb.Split('\n').Skip(1))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.Contains("List of devices", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1] == "device")
                {
                    any = true;
                    V1Per.Ui.Ok($"ADB device: {parts[0]}");
                    DeviceInfo(parts[0]);
                }
                else if (parts.Length >= 2 && parts[1] == "unauthorized")
                {
                    any = true;
                    V1Per.Ui.Warn($"{parts[0]} — UNAUTHORIZED (accept the USB debugging prompt)");
                }
                else if (parts.Length >= 2 && parts[1] == "offline")
                {
                    any = true;
                    V1Per.Ui.Warn($"{parts[0]} — OFFLINE");
                }
            }
        }

        string fb = V1Per.ProcessRunner.Run("fastboot", new[] { "devices" }, 8_000);
        if (!string.IsNullOrWhiteSpace(fb))
        {
            foreach (string line in fb.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.Contains("waiting", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    any = true;
                    V1Per.Ui.Ok($"Fastboot device: {parts[0]}");
                }
            }
        }

        if (!any)
        {
            V1Per.Ui.Err("No devices found.");
            V1Per.Ui.Muted("Enable USB debugging and reconnect the cable.");
        }
    }

    private static void DeviceInfo(string serial)
    {
        string Model(string prop) =>
            (V1Per.ProcessRunner.Adb(serial, new[] { "shell", "getprop", prop }, 8_000) ?? "N/A").Trim();

        string model = Model("ro.product.model");
        string sdk = Model("ro.build.version.sdk");
        string android = Model("ro.build.version.release");
        string kernel = (V1Per.ProcessRunner.Adb(serial, new[] { "shell", "uname", "-r" }, 8_000) ?? "N/A").Trim();
        string chip = Model("ro.board.platform");

        var table = new Table().HideHeaders().Border(TableBorder.None).Expand();
        table.AddColumn(new TableColumn("k"));
        table.AddColumn(new TableColumn("v"));
        void Row(string k, string v) =>
            table.AddRow($"[#{V1Per.Ui.PinkRed.ToHex()}]{k}[/]", $"[#{V1Per.Ui.LightBlue.ToHex()}]{v}[/]");

        Row("Model", model);
        Row("Android", android);
        Row("SDK", sdk);
        Row("SoC", chip);
        Row("Kernel", kernel);
        V1Per.Ui.Write(table);
    }
}