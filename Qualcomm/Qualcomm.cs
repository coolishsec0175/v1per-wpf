using System;
using System.IO;
using System.Linq;
using v1per_wpf.Qualcomm;
using V1Per.Qualcomm.Services;

namespace v1per_wpf;

/// <summary>
/// Qualcomm EDL (9008) flashing, ported from SnapdragonFlash-Tool (MIT).
/// Drives Sahara + Firehose through the SerialPortDevice engine.
/// </summary>
public static class QcFlash
{
    /// <summary>Progress 0..1 raised while a flash op runs (GUI progress bar).</summary>
    public static Action<float>? Progress;

    public static async Task Run(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        string[] rest = args.Skip(1).ToArray();

        switch (sub)
        {
            case "detect":
            case "state":
            case "devices":
                Detect();
                break;

            case "flash":
                await Flash(rest);
                break;

            case "dump":
                await Dump(rest);
                break;

            case "cloud":
                await CloudQuery(rest);
                break;

            case "storage":
                await DetectStorage(rest);
                break;

            case "help":
            default:
                Help();
                break;
        }
    }

    private static void Help()
    {
        V1Per.Ui.Rule("qualcomm subcommands");
        V1Per.Ui.Muted("  qualcomm detect              — show EDL (9008) download-mode ports");
        V1Per.Ui.Muted("  qualcomm flash <fwdir>      — flash a firmware folder (rawprogram*.xml + firehose programmer)");
        V1Per.Ui.Muted("  qualcomm dump <fwdir>       — read partitions from rawprogram*.xml to restore/");
        V1Per.Ui.Muted("  qualcomm cloud <port>       — query cloud loader for device on port");
        V1Per.Ui.Muted("  qualcomm storage <port>     — detect storage type (UFS/eMMC) on port");
    }

    /// <summary>Prints detected Qualcomm EDL COM ports (9008).</summary>
    public static void Detect()
    {
        V1Per.Ui.Rule("Qualcomm EDL state");
        var ports = ComPortCtrl.getDevicesQc();
        if (ports.Length == 0)
        {
            V1Per.Ui.Err("No Qualcomm EDL (9008) device found.");
            V1Per.Ui.Muted("Power off, hold VOL UP + plug USB (or use an EDL cable) to enter 9008 mode.");
            return;
        }
        foreach (string p in ports)
            V1Per.Ui.Ok($"EDL port: {p}");
        V1Per.Ui.Muted("Select the port in the Qualcomm tab to flash.");
    }

    private static async Task Flash(string[] args)
    {
        string fwDir = args.Length > 0
            ? string.Join(" ", args).Trim('"', '\'')
            : await Terminal.PromptAsync("Firmware folder (with rawprogram0.xml + firehose programmer): ");
        if (!Directory.Exists(fwDir))
        {
            V1Per.Ui.Err($"Folder not found: {fwDir}");
            return;
        }
        string port = await PickPortAsync();
        if (port is null)
            return;

        V1Per.Ui.Rule("Qualcomm EDL flash");
        await RunFlash(port, fwDir);
    }

    private static async Task Dump(string[] args)
    {
        string fwDir = args.Length > 0
            ? string.Join(" ", args).Trim('"', '\'')
            : await Terminal.PromptAsync("Firmware folder (with rawprogram0.xml): ");
        if (!Directory.Exists(fwDir))
        {
            V1Per.Ui.Err($"Folder not found: {fwDir}");
            return;
        }
        string port = await PickPortAsync();
        if (port is null)
            return;

        V1Per.Ui.Rule("Qualcomm EDL dump");
        await RunDump(port, fwDir);
    }

    private static async Task CloudQuery(string[] args)
    {
        string port = args.Length > 0 ? args[0] : await PickPortAsync();
        if (port is null) return;

        V1Per.Ui.Rule("Cloud Loader Query");
        var device = new SerialPortDevice
        {
            deviceName = port,
            swPath = "",
            verbose = true,
            UseCloudLoader = true,
            startTime = DateTime.Now,
        };
        RegisterDevice(port, device);
        try
        {
            var ports = ComPortCtrl.getDevicesQc();
            if (!ports.Contains(port))
            {
                V1Per.Ui.Err($"Port {port} not found in EDL devices.");
                return;
            }
            V1Per.Ui.Muted("Attempting Sahara handshake to identify device...");
            var info = new SaharaDeviceInfo { SerialPort = port };
            var match = await CloudLoaderService.FindLoaderAsync(info, "");
            if (!string.IsNullOrEmpty(match.LoaderPath))
            {
                V1Per.Ui.Ok($"Loader found: {match.LoaderPath} (source: {match.Source})");
            }
            else
            {
                V1Per.Ui.Err("No loader found for this device.");
            }
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Cloud query failed: {ex.Message}");
        }
        finally
        {
            UnregisterDevice();
        }
    }

    private static async Task DetectStorage(string[] args)
    {
        string port = args.Length > 0 ? args[0] : await PickPortAsync();
        if (port is null) return;

        V1Per.Ui.Rule("Storage Detection");
        var device = new SerialPortDevice
        {
            deviceName = port,
            swPath = "",
            verbose = true,
            startTime = DateTime.Now,
        };
        RegisterDevice(port, device);
        try
        {
            V1Per.Ui.Muted("Detecting storage type...");
            var info = StorageDetectService.DetectFromDeviceXml("");
            V1Per.Ui.Ok($"Storage type: {info.Type}");
            V1Per.Ui.Muted($"Sector size: {info.SectorSize} bytes");
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Storage detection failed: {ex.Message}");
        }
        finally
        {
            UnregisterDevice();
        }
    }

    private static async Task<string?> PickPortAsync()
    {
        var ports = ComPortCtrl.getDevicesQc();
        if (ports.Length == 0)
        {
            V1Per.Ui.Err("No Qualcomm EDL device detected. Enter 9008 mode first.");
            return null;
        }
        if (ports.Length == 1)
            return ports[0];
        int idx = await Dialogs.PickAsync("Select EDL port", ports.ToList());
        return idx >= 0 ? ports[idx] : null;
    }

    private static async Task RunFlash(string port, string fwDir)
    {
        var device = new SerialPortDevice
        {
            deviceName = port,
            swPath = fwDir,
            verbose = true,
            startTime = DateTime.Now,
        };
        RegisterDevice(port, device);
        V1Per.Ui.Warn($"Flashing {port} from {fwDir}. Do NOT unplug.");
        if (!await Terminal.ConfirmAsync("Proceed?"))
        {
            UnregisterDevice();
            return;
        }
        try
        {
            await Task.Run(() => device.flash());
            V1Per.Ui.Ok("Flash finished.");
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Flash failed: {ex.Message}");
        }
        finally
        {
            UnregisterDevice();
        }
    }

    private static async Task RunDump(string port, string fwDir)
    {
        var device = new SerialPortDevice
        {
            deviceName = port,
            swPath = fwDir,
            verbose = true,
            startTime = DateTime.Now,
        };
        RegisterDevice(port, device);
        V1Per.Ui.Warn($"Dumping {port} partitions to restore/. Do NOT unplug.");
        if (!await Terminal.ConfirmAsync("Proceed?"))
        {
            UnregisterDevice();
            return;
        }
        try
        {
            await Task.Run(() => device.CheckSha256());
            V1Per.Ui.Ok("Dump finished (see restore/ folder).");
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Dump failed: {ex.Message}");
        }
        finally
        {
            UnregisterDevice();
        }
    }

    private static void RegisterDevice(string port, DeviceCtrl ctrl)
    {
        FlashingDevice.flashDeviceList.Clear();
        FlashingDevice.flashDeviceList.Add(new Device
        {
            Name = port,
            StartTime = DateTime.Now,
            DeviceCtrl = ctrl,
            IsDone = false,
        });
        FlashingDevice.Progress = p => Progress?.Invoke(p);
    }

    private static void UnregisterDevice()
    {
        FlashingDevice.flashDeviceList.Clear();
        FlashingDevice.Progress = null;
    }
}