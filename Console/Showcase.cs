using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace V1Per;

/// <summary>
/// Renders the Spectre.Console-style two-column showcase: pink category
/// labels on the left, widget demos on the right, over the deep navy theme.
/// </summary>
public static class Showcase
{
    public static void Show()
    {
        Ui.Rule("V1Per showcase");

        var grid = new Grid();
        grid.AddColumn(new GridColumn().Width(16).NoWrap().RightAligned().PadRight(2));
        grid.AddColumn(new GridColumn().PadRight(0));

        grid.AddRow(new Markup($"[bold #{Ui.PinkRed.ToHex()}]Colors[/]"), Colors());
        grid.AddRow(new Markup($"[bold #{Ui.PinkRed.ToHex()}]OS[/]"), Os());
        grid.AddRow(new Markup($"[bold #{Ui.PinkRed.ToHex()}]Styles & Text[/]"), Styles());
        grid.AddRow(new Markup($"[bold #{Ui.PinkRed.ToHex()}]Markup[/]"), Markup());
        grid.AddRow(new Markup($"[bold #{Ui.PinkRed.ToHex()}]Tables & Trees[/]"), TablesAndTrees());
        grid.AddRow(new Markup($"[bold #{Ui.PinkRed.ToHex()}]Charts[/]"), Charts());

        Ui.Write(grid);
        AnsiConsole.WriteLine();
    }

    private static IRenderable Colors()
    {
        var text = new StringBuilder();
        text.AppendLine($"Color system: [bold #{Ui.Yellow.ToHex()}]{AnsiConsole.Profile.Capabilities.ColorSystem}[/]");
        text.AppendLine("24-bit truecolor gradient canvas:");
        text.AppendLine(GradientStrip(56, Ui.LightBlue, Ui.Mauve));
        text.Append(GradientStrip(56, Ui.PinkRed, Ui.SlimeGreen));
        return new Markup(text.ToString());
    }

    private static IRenderable Os()
    {
        var os = Environment.OSVersion;
        var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        return new Markup(
            $"[#{Ui.LightBlue.ToHex()}]Platform:[/] {os}\n"
            + $"[#{Ui.LightBlue.ToHex()}]Runtime:[/] {runtime}\n"
            + $"[#{Ui.LightBlue.ToHex()}]Arch:[/] {arch}\n"
            + $"[#{Ui.LightBlue.ToHex()}]Bits:[/] {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
    }

    private static IRenderable Styles()
    {
        var demo = new Markup(
            "[bold]bold[/]  [italic]italic[/]  [underline]underline[/]  [strikethrough]strike[/]\n"
            + $"[dim]dim[/]  [#{Ui.Mauve.ToHex()}]colored[/]  [#{Ui.SlimeGreen.ToHex()} on #{Ui.Background.ToHex()}]block bg[/]  [blink]blink[/]\n\n"
            + "Justified text blocks:");

        var cols = new Table().HideHeaders().Border(TableBorder.None).Expand();
        cols.AddColumn(new TableColumn(new Markup($"[#{Ui.SlimeGreen.ToHex()}]Left[/]")).LeftAligned());
        cols.AddColumn(new TableColumn(new Markup($"[#{Ui.Yellow.ToHex()}]Center[/]")).Centered());
        cols.AddColumn(new TableColumn(new Markup($"[#{Ui.LightBlue.ToHex()}]Right[/]")).RightAligned());
        cols.AddRow(
            new Markup($"[#{Ui.SlimeGreen.ToHex()}]Aligns flush to the left edge of its cell column.[/]"),
            new Markup($"[#{Ui.Yellow.ToHex()}]Centers text between both edges of its cell.[/]"),
            new Markup($"[#{Ui.LightBlue.ToHex()}]Flush right, ragged on the left side of its cell.[/]"));

        return new Rows(demo, cols);
    }

    private static IRenderable Markup()
    {
        return new Markup(
            "Inline BBCode-style tags: "
            + $"[#{Ui.PinkRed.ToHex()}]pink[/] "
            + $"[#{Ui.SlimeGreen.ToHex()}]green[/] "
            + $"[#{Ui.Yellow.ToHex()}]yellow[/] "
            + $"[#{Ui.LightBlue.ToHex()}]blue[/] "
            + $"[#{Ui.Mauve.ToHex()}]mauve[/]\n"
            + $"[bold #{Ui.Yellow.ToHex()}]Terminal emoji:[/] ♡ ✓ ✗ ⚠ 🔧 📱");
    }

    private static IRenderable TablesAndTrees()
    {
        var tree = new Tree($"[bold #{Ui.LightBlue.ToHex()}]/sdcard[/]");
        tree.AddNode($"[#{Ui.PinkRed.ToHex()}]Download[/]").AddNode($"[#{Ui.SlimeGreen.ToHex()}]patched_boot.img[/]");
        tree.AddNode($"[#{Ui.PinkRed.ToHex()}]Download[/]").AddNode($"[#{Ui.Yellow.ToHex()}]v1per_input.img[/]");
        tree.AddNode($"[#{Ui.LightBlue.ToHex()}]Android[/]").AddNode($"[#{Ui.Mauve.ToHex()}]data[/]");

        var table = new Table
        {
            Border = TableBorder.Rounded,
            BorderStyle = new Style(Ui.PinkRed),
        };
        table.AddColumn(new TableColumn(new Markup($"[bold #{Ui.Yellow.ToHex()}]File tree[/]")).Centered());
        table.AddRow(tree);

        return new Rows(
            new Markup("A [bold]Tree[/] nested inside a bordered [bold]Table[/] cell:\n\n"),
            table);
    }

    private static IRenderable Charts()
    {
        var chart = new BarChart()
            .Width(56)
            .Label("Patched partitions")
            .CenterLabel()
            .AddItem("boot", 22, Ui.SlimeGreen)
            .AddItem("init_boot", 14, Ui.LightBlue)
            .AddItem("vendor_boot", 9, Ui.Yellow)
            .AddItem("dtbo", 4, Ui.PinkRed);

        return new Rows(
            new Markup("Horizontal bar chart with legend keys:\n\n"),
            chart);
    }

    private static string GradientStrip(int width, Color from, Color to)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < width; i++)
        {
            double t = width <= 1 ? 0 : (double)i / (width - 1);
            int r = (int)Math.Round(from.R + (to.R - from.R) * t);
            int g = (int)Math.Round(from.G + (to.G - from.G) * t);
            int b = (int)Math.Round(from.B + (to.B - from.B) * t);
            sb.Append($"[#{r:x2}{g:x2}{b:x2}]█[/]");
        }
        return sb.ToString();
    }
}