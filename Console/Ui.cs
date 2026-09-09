using Spectre.Console;

namespace V1Per;

/// <summary>
/// Spectre.Console-inspired look: deep navy/slate background with crisp
/// pastel accents (pink, green, yellow, light blue) for high contrast.
/// </summary>
public static class Ui
{
    // Catppuccin-Mocha-ish pastels that pop on the deep navy background.
    public static readonly Color Background = new(30, 30, 46);     // #1E1E2E deep navy/slate
    public static readonly Color PinkRed = new(243, 139, 168);     // #F38BA8 pastel pink
    public static readonly Color DeepRed = new(231, 130, 132);     // #E78284 pastel red
    public static readonly Color SlimeGreen = new(166, 227, 161);  // #A6E3A1 pastel green
    public static readonly Color Yellow = new(249, 226, 175);      // #F9E2AF pastel yellow
    public static readonly Color LightBlue = new(137, 180, 250);   // #89B4FA pastel blue
    public static readonly Color Lavender = new(180, 190, 254);    // #B4BEFE pastel lavender
    public static readonly Color Mauve = new(203, 166, 247);       // #CBA6F7 pastel mauve

    /// <summary>
    /// Re-applies the background before the next output. In the WPF shell the
    /// window background is owned by XAML, so this is a no-op.
    /// </summary>
    public static void Paint() { }

    /// <summary>No-op in the WPF shell (background is set in XAML).</summary>
    public static void SetBackground() => EnableVirtualTerminal();

    /// <summary>No-op in the WPF shell.</summary>
    public static void RestoreBackground() { }

    private static void EnableVirtualTerminal()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                IntPtr handle = Native.GetStdHandle(-11);
                if (Native.GetConsoleMode(handle, out uint mode))
                    Native.SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
            }
            catch (Exception)
            {
                // not a real console — ignore
            }
        }
    }

    // ── Helpers that re-paint the background before writing ──────────

    public static void Markup(string markup)
    {
        Paint();
        AnsiConsole.Markup(markup);
    }

    public static void MarkupLine(string markup)
    {
        Paint();
        AnsiConsole.MarkupLine(markup);
    }

    public static void MarkupLineInterpolated(FormattableString markup)
    {
        Paint();
        AnsiConsole.MarkupLineInterpolated(markup);
    }

    public static void Write(Table table)
    {
        Paint();
        AnsiConsole.Write(table);
    }

    public static void Write(Panel panel)
    {
        Paint();
        AnsiConsole.Write(panel);
    }

    public static void Write(Rule rule)
    {
        Paint();
        AnsiConsole.Write(rule);
    }

    public static void Write(FigletText figlet)
    {
        Paint();
        AnsiConsole.Write(figlet);
    }

    public static void Write(Grid grid)
    {
        Paint();
        AnsiConsole.Write(grid);
    }

    public static void Write(BarChart chart)
    {
        Paint();
        AnsiConsole.Write(chart);
    }

    public static void Ok(string message) =>
        MarkupLine($"[#{SlimeGreen.ToHex()}][[+]][/] {message}");

    public static void Info(string message) =>
        MarkupLine($"[#{LightBlue.ToHex()}][[*]][/] {message}");

    public static void Warn(string message) =>
        MarkupLine($"[#{Yellow.ToHex()}][[!]][/] {message}");

    public static void Err(string message) =>
        MarkupLine($"[#{DeepRed.ToHex()}][[!]][/] {message}");

    public static void Muted(string message) =>
        MarkupLine($"[grey]{message}[/]");

    public static void Rule(string title) =>
        Write(new Rule($"[#{PinkRed.ToHex()}]{title}[/]")
        {
            Style = new Style(PinkRed),
        });

    public static void Banner()
    {
        Write(new FigletText("V1PER")
        {
            Color = PinkRed,
        });
        MarkupLine($"[#{PinkRed.ToHex()}]android service toolkit[/]");
        MarkupLine($"[#{Yellow.ToHex()}]made with love by kouyuki♡[/]");
        Paint();
        AnsiConsole.WriteLine();
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }
}