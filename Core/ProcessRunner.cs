using System.Diagnostics;
using System.IO;

namespace V1Per;

/// <summary>Runs adb / fastboot and checks whether a device is in Fastboot.</summary>
public static class ProcessRunner
{
    public static string? AdbPath { get; set; }
    public static string? FastbootPath { get; set; }

    private static readonly List<Process> Running = new();
    private static readonly object RunningLock = new();

    static ProcessRunner()
    {
        AdbPath = Resolve("adb") ?? "adb";
        FastbootPath = Resolve("fastboot") ?? "fastboot";
    }

    /// <summary>Kills every live child process started by the app (STOP button).</summary>
    public static void KillAll()
    {
        lock (RunningLock)
        {
            foreach (var p in Running)
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill(true);
                }
                catch (Exception) { }
            }
            Running.Clear();
        }
    }

    private static void Track(Process p)
    {
        lock (RunningLock)
            Running.Add(p);
    }

    /// <summary>Finds <paramref name="file"/> inside <paramref name="folder"/> walking up from the app dir.</summary>
    public static string? Locate(string folder, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, folder, file);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Finds an existing folder by walking up from the app dir.</summary>
    public static string? LocateDir(string folder)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, folder);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Runs a process streaming each output line to <paramref name="onLine"/>.
    /// Returns the full combined output. Prevents pipe deadlock on heavy output.
    /// </summary>
    public static async Task<string> RunLive(
        string exe, IReadOnlyList<string> args, Action<string>? onLine = null,
        int timeoutMs = 600_000, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDir))
            psi.WorkingDirectory = workingDir;
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        var sb = new System.Text.StringBuilder();
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return string.Empty;

            Track(process);

            void Pipe(System.IO.StreamReader r)
            {
                while (true)
                {
                    string? line = r.ReadLine();
                    if (line is null)
                        break;
                    lock (sb)
                        sb.AppendLine(line);
                    onLine?.Invoke(line);
                }
            }

            var t1 = Task.Run(() => Pipe(process.StandardOutput));
            var t2 = Task.Run(() => Pipe(process.StandardError));
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception)
                {
                    // ignore
                }
            }
            await Task.WhenAll(t1, t2).ConfigureAwait(false);
            lock (sb)
                return sb.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Prefers a bundled tool in <c>platform-tools/&lt;tool&gt;.exe</c> next to the
    /// app (walking up the output-directory tree), falls back to the plain name
    /// so PATH resolution still works.
    /// </summary>
    private static string? Resolve(string tool) => Locate("platform-tools", tool + ".exe");

    public static string Run(string tool, IReadOnlyList<string> args, int timeoutMs = 15_000)
    {
        string exe = tool == "adb" ? AdbPath ?? "adb" : FastbootPath ?? "fastboot";
        return RunProcess(exe, args, timeoutMs);
    }

    /// <summary>Runs fastboot with an optional serial (-s) prefix.</summary>
    public static string Fastboot(string? serial, IReadOnlyList<string> args, int timeoutMs = 15_000)
    {
        var full = new List<string>();
        if (!string.IsNullOrEmpty(serial))
        {
            full.Add("-s");
            full.Add(serial);
        }
        full.AddRange(args);
        return RunProcess(FastbootPath ?? "fastboot", full, timeoutMs);
    }

    private static string RunProcess(string exe, IReadOnlyList<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return string.Empty;

            Track(process);

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // ignore
                }

                return string.Empty;
            }

            return (stdout.Result + stderr.Result).Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Runs adb with an optional serial (-s) prefix.</summary>
    public static string Adb(string? serial, IReadOnlyList<string> args, int timeoutMs = 15_000)
    {
        var full = new List<string>();
        if (!string.IsNullOrEmpty(serial))
        {
            full.Add("-s");
            full.Add(serial);
        }
        full.AddRange(args);
        return Run("adb", full, timeoutMs);
    }

    /// <summary>True when `fastboot devices` reports at least one device.</summary>
    public static bool InFastbootMode()
    {
        string output = Run("fastboot", new[] { "devices" }, 8_000);
        if (string.IsNullOrWhiteSpace(output))
            return false;

        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            string lower = trimmed.ToLowerInvariant();
            if (lower.Contains("no permission") || lower.Contains("waiting for"))
                continue;
            if (trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 1)
                return true;
        }

        return false;
    }
}