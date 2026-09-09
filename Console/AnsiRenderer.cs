using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace v1per_wpf;

/// <summary>
/// Parses Spectre.Console ANSI escape sequences and renders them as colored
/// text into a WPF RichTextBox. Handles SGR colors/styles, OSC 11 background
/// changes and the clear-screen sequence.
/// </summary>
public sealed class AnsiRenderer
{
    private readonly RichTextBox _box;
    private readonly FlowDocument _doc;
    private Paragraph _paragraph;
    private StyleState _state = new();
    private readonly StringBuilder _buf = new();
    private State _mode = State.Text;

    private const int MaxBlocks = 600;

    public AnsiRenderer(RichTextBox box)
    {
        _box = box;
        _doc = box.Document;
        _paragraph = NewParagraph();
    }

    public void Feed(string chunk)
    {
        foreach (char c in chunk)
            Process(c);
        Flush();
    }

    public void FeedLine(string line) => Feed(line + "\n");

    public void Clear() => ClearScreen();

    /// <summary>Synchronous text write (used by interactive pickers).</summary>
    public void WriteDirect(string text, (int, int, int)? fg = null, (int, int, int)? bg = null)
    {
        if (text.Length == 0)
            return;
        var run = new Run(text)
        {
            Foreground = fg is { } f
                ? new SolidColorBrush(Color.FromRgb((byte)f.Item1, (byte)f.Item2, (byte)f.Item3))
                : new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            Background = bg is { } b
                ? new SolidColorBrush(Color.FromRgb((byte)b.Item1, (byte)b.Item2, (byte)b.Item3))
                : new SolidColorBrush(Colors.Transparent),
        };
        _paragraph.Inlines.Add(run);
    }

    /// <summary>Starts a new line for <see cref="WriteDirect"/>.</summary>
    public void NewLineDirect() => _paragraph = NewParagraph();

    private void Process(char c)
    {
        switch (_mode)
        {
            case State.Text:
                if (c == '\x1b')
                {
                    Flush();
                    _mode = State.Escape;
                }
                else
                {
                    _buf.Append(c);
                }
                break;

            case State.Escape:
                switch (c)
                {
                    case '[':
                        _mode = State.Csi;
                        break;
                    case ']':
                        _mode = State.Osc;
                        break;
                    default:
                        _mode = State.Text;
                        break;
                }
                break;

            case State.Csi:
                if (c == 'm')
                {
                    ApplySgr(_buf.ToString());
                    _buf.Clear();
                    _mode = State.Text;
                }
                else if (c == 'J')
                {
                    if (_buf.Length == 0 || _buf.ToString() == "2")
                        ClearScreen();
                    _buf.Clear();
                    _mode = State.Text;
                }
                else if ((c >= 'A' && c <= 'D') || c == 'H' || c == 'f' || c == 'K')
                {
                    // cursor movement / erase-line — not needed for our output
                    _buf.Clear();
                    _mode = State.Text;
                }
                else
                {
                    _buf.Append(c);
                }
                break;

            case State.Osc:
                if (c == '\x07') // BEL terminates OSC too
                {
                    ApplyOsc(_buf.ToString());
                    _buf.Clear();
                    _mode = State.Text;
                }
                else if (c == '\\' && _buf.ToString().EndsWith('\x1b'))
                {
                    _buf.Length -= 1; // drop the ESC
                    ApplyOsc(_buf.ToString());
                    _buf.Clear();
                    _mode = State.Text;
                }
                else
                {
                    _buf.Append(c);
                }
                break;
        }
    }

    private void Flush()
    {
        if (_buf.Length == 0)
            return;

        string text = _buf.ToString();
        _buf.Clear();

        string[] parts = text.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                BreakLine();
            if (parts[i].Length > 0)
                AppendRun(parts[i]);
        }
    }

    private void AppendRun(string text)
    {
        var run = new Run(text)
        {
            Foreground = ForegroundBrush(_state.Fg),
            Background = BackgroundBrush(_state.Bg),
            FontWeight = _state.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = _state.Italic ? FontStyles.Italic : FontStyles.Normal,
        };

        var decos = new TextDecorationCollection();
        if (_state.Underline)
            decos.Add(TextDecorations.Underline);
        if (_state.Strikethrough)
            decos.Add(TextDecorations.Strikethrough);
        if (decos.Count > 0)
            run.TextDecorations = decos;

        _paragraph.Inlines.Add(run);
    }

    private void BreakLine()
    {
        _paragraph = NewParagraph();
    }

    private Paragraph NewParagraph()
    {
        if (_doc.Blocks.Count >= MaxBlocks && _doc.Blocks.Count > 0)
            _doc.Blocks.Remove(_doc.Blocks.FirstBlock);
        var p = new Paragraph { Margin = new Thickness(0) };
        _doc.Blocks.Add(p);
        return p;
    }

    private void ClearScreen()
    {
        _doc.Blocks.Clear();
        _paragraph = NewParagraph();
    }

    private void ApplySgr(string paramString)
    {
        if (string.IsNullOrEmpty(paramString))
        {
            _state.Reset();
            return;
        }

        var parts = paramString.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int code))
                continue;

            switch (code)
            {
                case 0: _state.Reset(); break;
                case 1: _state.Bold = true; break;
                case 3: _state.Italic = true; break;
                case 4: _state.Underline = true; break;
                case 7: _state.Reverse = true; break;
                case 9: _state.Strikethrough = true; break;
                case 22: _state.Bold = false; break;
                case 23: _state.Italic = false; break;
                case 24: _state.Underline = false; break;
                case 27: _state.Reverse = false; break;
                case 29: _state.Strikethrough = false; break;
                case 39: _state.Fg = null; break;
                case 49: _state.Bg = null; break;

                case 38: // foreground, possibly 2;r;g;b or 5;n
                case 48: // background
                    (int r, int g, int b)? color = ParseColor(parts, i);
                    if (color is { } c)
                    {
                        if (code == 38) _state.Fg = c;
                        else _state.Bg = c;
                    }
                    break;
            }
        }
    }

    private static (int, int, int)? ParseColor(string[] parts, int i)
    {
        if (i + 1 >= parts.Length)
            return null;
        if (!int.TryParse(parts[i + 1], out int kind))
            return null;

        if (kind == 2 && i + 4 < parts.Length) // 38;2;r;g;b
        {
            if (int.TryParse(parts[i + 2], out int r) &&
                int.TryParse(parts[i + 3], out int g) &&
                int.TryParse(parts[i + 4], out int b))
                return (Clamp(r), Clamp(g), Clamp(b));
        }
        else if (kind == 5 && i + 2 < parts.Length) // 38;5;n
        {
            if (int.TryParse(parts[i + 2], out int n))
                return FromIndex256(n);
        }
        return null;
    }

    private void ApplyOsc(string osc)
    {
        if (!osc.StartsWith("11;", StringComparison.Ordinal))
            return;
        string color = osc[3..];
        if (color.StartsWith('#'))
            color = color[1..];
        if (color.Length == 6 && int.TryParse(color, System.Globalization.NumberStyles.HexNumber, null, out int value))
        {
            var c = new Color
            {
                R = (byte)((value >> 16) & 0xFF),
                G = (byte)((value >> 8) & 0xFF),
                B = (byte)(value & 0xFF),
                A = 255,
            };
            _box.Background = BackgroundBrush((c.R, c.G, c.B));
        }
    }

    private static SolidColorBrush ForegroundBrush((int, int, int)? rgb)
    {
        if (rgb is { } v)
            return new SolidColorBrush(Color.FromRgb((byte)v.Item1, (byte)v.Item2, (byte)v.Item3));
        // No color set: fall back to the default text color (NOT transparent).
        return new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)); // #CDD6F4
    }

    private static SolidColorBrush BackgroundBrush((int, int, int)? rgb)
    {
        if (rgb is { } v)
            return new SolidColorBrush(Color.FromRgb((byte)v.Item1, (byte)v.Item2, (byte)v.Item3));
        return new SolidColorBrush(Colors.Transparent);
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 255);

    private static (int, int, int) FromIndex256(int n)
    {
        if (n < 16)
        {
            return (n) switch
            {
                0 => (0, 0, 0), 1 => (128, 0, 0), 2 => (0, 128, 0), 3 => (128, 128, 0),
                4 => (0, 0, 128), 5 => (128, 0, 128), 6 => (0, 128, 128), 7 => (192, 192, 192),
                8 => (128, 128, 128), 9 => (255, 0, 0), 10 => (0, 255, 0), 11 => (255, 255, 0),
                12 => (0, 0, 255), 13 => (255, 0, 255), 14 => (0, 255, 255), _ => (255, 255, 255),
            };
        }
        if (n < 232)
        {
            int v = n - 16;
            int r = v / 36, g = (v / 6) % 6, b = v % 6;
            int f(int x) => x == 0 ? 0 : 55 + x * 40;
            return (f(r), f(g), f(b));
        }
        int gray = 8 + (n - 232) * 10;
        return (gray, gray, gray);
    }

    private enum State { Text, Escape, Csi, Osc }

    private sealed class StyleState
    {
        public (int, int, int)? Fg;
        public (int, int, int)? Bg;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strikethrough;
        public bool Reverse;

        public void Reset()
        {
            Fg = null;
            Bg = null;
            Bold = Italic = Underline = Strikethrough = Reverse = false;
        }
    }
}