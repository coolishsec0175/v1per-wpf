using System.IO.Ports;
using Spectre.Console;

namespace V1Per;

/// <summary>
/// Port of the Rust MTK force-fastboot routine: floods "FASTBOOT" over the
/// preloader serial port until the device drops into Fastboot.
/// </summary>
public static class ForceFastboot
{
    public const ushort MtkVendorId = 0x0E8D;
    private static readonly byte[] BootModeCmd = "FASTBOOT"u8.ToArray();

    private static readonly string[] Keywords = { "MediaTek", "PreLoader", "MTK" };
    private static readonly int[] Baus = { 115_200, 921_600, 57_600 };

    private const double ScanWindowSecs = 120.0;
    private const double PortAttemptSecs = 2.5;
    private const double PollIntervalSecs = 0.2;
    private const double FloodIntervalSecs = 0.03;
    private const double FastbootModePollSecs = 0.5;
    private const double ProbeWriteEveryMs = 200;
    private const int RetryPortMs = 500;

public static void Run()
    {
        Ui.Rule("MTK force fastboot");
        Ui.Warn("Forcing it now niga.....");
        Ui.Info("Attempting to switch device to Fastboot...");

        if (ProcessRunner.InFastbootMode())
        {
            Ui.Ok("Device already in Fastboot mode.");
            Ui.MarkupLine($"[#{Ui.SlimeGreen.ToHex()}]OK DONE[/]");
            return;
        }

        byte[] expectedAck = BuildExpectedAck();
        DateTime scanDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(ScanWindowSecs);
        var lastAttempt = new Dictionary<string, DateTime>();
        DateTime lastModeCheck = DateTime.UtcNow;
        bool sawDevice = false;
        bool sawNoAck = false;
        bool sawOpenError = false;

        try
        {
            while (DateTime.UtcNow < scanDeadline)
            {
                if ((DateTime.UtcNow - lastModeCheck).TotalSeconds >= FastbootModePollSecs)
                {
                    if (ProcessRunner.InFastbootMode())
                    {
Ui.Ok("Device switched to Fastboot during handshake.");
                        Ui.MarkupLine($"[#{Ui.SlimeGreen.ToHex()}]OK DONE[/]");
                        return;
        }

                    lastModeCheck = DateTime.UtcNow;
                }

                var ports = RankPorts(ComPorts.Enumerate());
                if (ports.Count == 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(PollIntervalSecs));
                    continue;
                }

                foreach (var port in ports)
                {
                    DateTime now = DateTime.UtcNow;
                    if (lastAttempt.TryGetValue(port.Device, out DateTime last) &&
                        (now - last).TotalMilliseconds < RetryPortMs)
                        continue;
                    lastAttempt[port.Device] = now;

                    sawDevice = true;
                    Ui.MarkupLineInterpolated(
                        $"[#{Ui.LightBlue.ToHex()}][[*]][/] Detected candidate MTK port: [bold][#{Ui.LightBlue.ToHex()}]{port.Device}[/][/] ({port.Description})");

                    foreach (int baud in Baus)
                    {
                        (string kind, string? detail) = Attempt(port.Device, baud, expectedAck);
                        switch (kind)
                        {
                            case "success":
                                Ui.Ok("Handshake successful. Device should be in Fastboot.");
                                Ui.MarkupLine($"[#{Ui.SlimeGreen.ToHex()}]OK DONE[/]");
                                return;
                            case "open_error":
                                sawOpenError = true;
                                Ui.MarkupLineInterpolated(
                                    $"[#{Ui.Yellow.ToHex()}][[!]][/] {port.Device} @ {baud}: {detail}");
                                break;
                            case "no_ack":
                                sawNoAck = true;
                                break;
                            case "disconnected":
                                Ui.MarkupLineInterpolated(
                                    $"[#{Ui.LightBlue.ToHex()}][[*]][/] {port.Device} dropped mid-handshake; device may be switching to Fastboot.");
                                break;
                        }
                    }
                }

                Thread.Sleep(TimeSpan.FromSeconds(PollIntervalSecs));
            }
        }
        catch (OperationCanceledException)
        {
            Ui.Err("Fastboot attempt cancelled.");
            return;
        }

        if (ProcessRunner.InFastbootMode())
        {
Ui.Ok("Device switched to Fastboot during handshake.");
                        Ui.MarkupLine($"[#{Ui.SlimeGreen.ToHex()}]OK DONE[/]");
                        return;
        }

        if (!sawDevice)
        {
            Ui.Err("No MTK preloader device detected.");
            Ui.Muted("Make sure the phone is powered off and connected via USB.");
            return;
        }

        if (sawOpenError && !sawNoAck)
        {
            Ui.Err("Ports detected but could not be opened. Check driver permissions for VID 0e8d.");
            return;
        }

        Ui.Err("Device detected but no ACK received from preloader.");
        Ui.Muted("Try re-plugging the cable or holding a different key combo to reach preloader.");
    }

    private static List<ComPortInfo> RankPorts(List<ComPortInfo> ports)
    {
        var candidates = new List<(int Score, ComPortInfo Port)>();

        foreach (var port in ports)
        {
            int score = 0;
            string desc = (port.Description + " ").ToLowerInvariant();

            if (port.Vid == MtkVendorId)
                score += 100;

            foreach (string keyword in Keywords)
            {
                if (desc.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    score += 10;
            }

            if (score > 0)
                candidates.Add((score, port));
        }

        candidates.Sort((a, b) => b.Score != a.Score
            ? b.Score.CompareTo(a.Score)
            : string.CompareOrdinal(a.Port.Device, b.Port.Device));

        return candidates.Select(c => c.Port).ToList();
    }

    private static byte[] BuildExpectedAck()
    {
        // "READY" + reversed tail of "FASTBOOT" -> "READYTOO"
        var tail = BootModeCmd.Reverse().Take(3).ToArray();
        return "READY"u8.ToArray().Concat(tail).ToArray();
    }

    /// <summary>Returns (kind, detail): success / open_error / no_ack / disconnected.</summary>
    private static (string, string?) Attempt(string portName, int baud, byte[] expectedAck)
    {
        using var port = Open(portName, baud, out string? openError);
        if (port is null)
            return ("open_error", openError);

        port.ReadTimeout = 1; // effectively non-blocking, like the Rust timeout 0.0
        port.WriteTimeout = 1000;

        DateTime start = DateTime.UtcNow;
        TimeSpan floodDuration = TimeSpan.FromSeconds(Math.Min(PortAttemptSecs * 0.6, 1.5));
        TimeSpan probeDuration = TimeSpan.FromSeconds(Math.Max(PortAttemptSecs - floodDuration.TotalSeconds, 0.2));
        DateTime lastModeCheck = DateTime.UtcNow;

        // ── flood phase ──
        while (DateTime.UtcNow - start < floodDuration)
        {
            if (!TryWrite(port))
            {
                return (DisconnectResult(portName), null);
            }

            if (HasAck(port, expectedAck))
                return ("success", null);

            if ((DateTime.UtcNow - lastModeCheck).TotalSeconds >= FastbootModePollSecs)
            {
                if (ProcessRunner.InFastbootMode())
                    return ("success", null);
                lastModeCheck = DateTime.UtcNow;
            }

            Thread.Sleep(TimeSpan.FromSeconds(FloodIntervalSecs));
        }

        // ── probe phase ──
        DateTime lastWrite = DateTime.UtcNow;
        DateTime probeStart = DateTime.UtcNow;
        while (DateTime.UtcNow - probeStart < probeDuration)
        {
            if (HasAck(port, expectedAck))
                return ("success", null);

            if ((DateTime.UtcNow - lastWrite).TotalMilliseconds >= ProbeWriteEveryMs)
            {
                if (!TryWrite(port))
                {
                    return (DisconnectResult(portName), null);
                }

                lastWrite = DateTime.UtcNow;
            }

            if ((DateTime.UtcNow - lastModeCheck).TotalSeconds >= FastbootModePollSecs)
            {
                if (ProcessRunner.InFastbootMode())
                    return ("success", null);
                lastModeCheck = DateTime.UtcNow;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }

        return ("no_ack", null);
    }

    private static SerialPort? Open(string portName, int baud, out string? error)
    {
        error = null;
        try
        {
            var port = new SerialPort(portName, baud)
            {
                ReadTimeout = 1,
                WriteTimeout = 1000,
            };
            port.Open();
            return port;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// After a failed write: success if the device is now in fastboot OR the
    /// VCOM port vanished (the device switched modes and dropped the port).
    /// </summary>
    private static string DisconnectResult(string portName)
    {
        if (ProcessRunner.InFastbootMode())
            return "success";
        bool portGone = !ComPorts.Enumerate()
            .Any(p => string.Equals(p.Device, portName, StringComparison.OrdinalIgnoreCase));
        return portGone ? "success" : "disconnected";
    }

    private static bool TryWrite(SerialPort port)
    {
        try
        {
            port.Write(BootModeCmd, 0, BootModeCmd.Length);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasAck(SerialPort port, byte[] expectedAck)
    {
        try
        {
            var buffer = new byte[64];
            int count = port.Read(buffer, 0, buffer.Length);
            if (count <= 0)
                return false;

            Span<byte> read = buffer.AsSpan(0, count);
            return read.IndexOf(expectedAck) >= 0 || read.IndexOf("READY"u8) >= 0;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}