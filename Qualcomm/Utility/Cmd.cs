using System;
using System.Diagnostics;
using System.Text;

namespace v1per_wpf.Qualcomm;

/// <summary>Minimal cmd.exe wrapper for the ported engine (used by lsusb / fh_loader).</summary>
public class Cmd
{
    private readonly string _deviceName;
    private readonly string _tag;

    public Cmd(string deviceName, string tag)
    {
        _deviceName = deviceName;
        _tag = tag;
    }

    public string Execute(string? deviceName, string cmd)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/C " + cmd)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var sb = new StringBuilder();
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
                return string.Empty;
            sb.Append(p.StandardOutput.ReadToEnd());
            sb.Append(p.StandardError.ReadToEnd());
            p.WaitForExit(120_000);
            return sb.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public void Execute_returnLine(string deviceName, string cmd, int timeout)
    {
        _ = Execute(deviceName, cmd);
    }
}