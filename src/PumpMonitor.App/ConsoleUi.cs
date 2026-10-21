using System.Text;

namespace PumpMonitor.App;

/// <summary>
/// Serialises all console output and keeps a "> " input prompt pinned below the log, so readings
/// written from other threads scroll above whatever the operator is typing instead of splitting it.
/// </summary>
/// <remarks>
/// Keys are read with <see cref="Console.ReadKey(bool)"/> and echoed by this class, so the prompt can be
/// cleared and redrawn around each log line. When input or output is redirected (piped commands, tests)
/// there is no prompt to protect, so it falls back to plain <see cref="Console.ReadLine"/> and
/// <see cref="Console.WriteLine(string)"/>.
/// </remarks>
internal sealed class ConsoleUi
{
    private const string Prompt = "> ";

    private readonly Lock _gate = new();
    private readonly StringBuilder _input = new();
    private readonly bool _interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
    private bool _closed;

    /// <summary>Writes a line above the prompt. Safe to call from any thread.</summary>
    public void WriteLine(string text) => Write(Console.Out, text);

    /// <summary>Writes a line to standard error above the prompt. Safe to call from any thread.</summary>
    public void WriteError(string text) => Write(Console.Error, text);

    /// <summary>
    /// Blocks until the operator presses Enter and returns the line, or null if input has ended.
    /// Call from one thread at a time.
    /// </summary>
    public string? ReadLine()
    {
        if (!_interactive)
        {
            return Console.ReadLine();
        }

        lock (_gate)
        {
            // The previous Enter (or a log line since) may have left a prompt drawn; replace it, don't append.
            ClearPromptLine();
            DrawPrompt();
        }

        while (true)
        {
            // Wait for the key outside the lock so log lines keep flowing while the operator thinks.
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            lock (_gate)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        string line = _input.ToString();
                        _input.Clear();
                        // Leave the submitted command in the scrollback, then start a fresh prompt.
                        ClearPromptLine();
                        Console.WriteLine(Prompt + line);
                        DrawPrompt();
                        return line;
                    case ConsoleKey.Backspace when _input.Length > 0:
                        _input.Length--;
                        break;
                    case ConsoleKey.Escape:
                        _input.Clear();
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            _input.Append(key.KeyChar);
                        }

                        break;
                }

                ClearPromptLine();
                DrawPrompt();
            }
        }
    }

    /// <summary>Removes the prompt; later writes print as plain lines. Call once input is finished.</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_interactive && !_closed)
            {
                ClearPromptLine();
            }

            _closed = true;
        }
    }

    private void Write(TextWriter writer, string text)
    {
        lock (_gate)
        {
            bool showPrompt = _interactive && !_closed;
            if (showPrompt)
            {
                ClearPromptLine();
            }

            writer.WriteLine(text);
            if (showPrompt)
            {
                DrawPrompt();
            }
        }
    }

    private void DrawPrompt()
    {
        // Show only the tail of long input so the prompt never wraps onto a second line,
        // which a carriage return alone couldn't clear.
        int room = Math.Max(1, Console.WindowWidth - Prompt.Length - 1);
        string visible = _input.Length <= room ? _input.ToString() : _input.ToString(_input.Length - room, room);
        Console.Write(Prompt + visible);
    }

    private static void ClearPromptLine() =>
        Console.Write("\r" + new string(' ', Math.Max(0, Console.WindowWidth - 1)) + "\r");
}
