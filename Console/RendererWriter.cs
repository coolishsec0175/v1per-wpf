using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace v1per_wpf;

/// <summary>
/// A TextWriter that Spectre.Console writes ANSI output into. A UI-thread
/// timer drains the buffer into a queue; the window then reveals it with a
/// smooth typewriter animation.
/// </summary>
public sealed class RendererWriter : TextWriter
{
    private readonly StringBuilder _buffer = new();
    private readonly ConcurrentQueue<string> _output = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_buffer)
            _buffer.Append(value);
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        lock (_buffer)
            _buffer.Append(value);
    }

    public override void WriteLine(string? value)
    {
        lock (_buffer)
            _buffer.Append(value).Append('\n');
    }

    /// <summary>Called on the UI thread (timer) to move buffered text into the queue.</summary>
    public void Drain()
    {
        string text;
        lock (_buffer)
        {
            if (_buffer.Length == 0)
                return;
            text = _buffer.ToString();
            _buffer.Clear();
        }
        _output.Enqueue(text);
    }

    public bool TryDequeue(out string? text) => _output.TryDequeue(out text);

    public void ClearQueue()
    {
        while (_output.TryDequeue(out _))
        {
            // discard
        }
    }

    /// <summary>Drops everything buffered and queued (used when a picker takes over the screen).</summary>
    public void ClearAll()
    {
        lock (_buffer)
            _buffer.Clear();
        ClearQueue();
    }
}