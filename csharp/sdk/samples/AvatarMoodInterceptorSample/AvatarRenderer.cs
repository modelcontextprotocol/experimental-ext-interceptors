namespace AvatarMoodInterceptorSample;

/// <summary>
/// Renders the avatar pinned to the top of the terminal while the REPL
/// scrolls underneath. Uses ANSI escape sequences (DECSTBM scroll region
/// + cursor save/restore) so avatar updates redraw in place without
/// disturbing the conversation below.
/// </summary>
public static class AvatarRenderer
{
    // Rows reserved at the top of the screen for the avatar block.
    // 4 face lines + 1 status line + 1 separator = 6.
    public const int ReservedRows = 6;

    private static readonly object ConsoleLock = new();

    private static string FaceFor(Mood mood) => mood switch
    {
        Mood.Happy => "^_^",
        Mood.Curious => "o_O",
        Mood.Focused => "-_-",
        Mood.Confused => "?_?",
        Mood.Frustrated => ">_<",
        _ => "._.",
    };

    // ANSI 256-color foreground codes per mood.
    private static string ColorFor(Mood mood) => mood switch
    {
        Mood.Happy => "\x1b[92m",       // bright green
        Mood.Curious => "\x1b[96m",     // bright cyan
        Mood.Focused => "\x1b[94m",     // bright blue
        Mood.Confused => "\x1b[93m",    // bright yellow
        Mood.Frustrated => "\x1b[91m",  // bright red
        _ => "\x1b[90m",                // bright black (gray)
    };

    /// <summary>
    /// Clears the screen, reserves the top rows for the avatar, and parks
    /// the cursor below so normal Console writes scroll in the lower region.
    /// </summary>
    public static void Initialize()
    {
        lock (ConsoleLock)
        {
            var bottom = Math.Max(Console.WindowHeight, ReservedRows + 4);

            // Clear screen + home cursor.
            Console.Out.Write("\x1b[2J\x1b[H");
            // DECSTBM: restrict scrolling to rows (ReservedRows+1)..bottom.
            Console.Out.Write($"\x1b[{ReservedRows + 1};{bottom}r");
            // Move cursor to the first row of the scrolling region.
            Console.Out.Write($"\x1b[{ReservedRows + 1};1H");
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Restores the terminal to a normal full-screen scroll region.
    /// Call before exiting.
    /// </summary>
    public static void Reset()
    {
        lock (ConsoleLock)
        {
            // Reset scroll region to full screen.
            Console.Out.Write("\x1b[r");
            // Park cursor at bottom.
            Console.Out.Write($"\x1b[{Console.WindowHeight};1H");
            Console.Out.Flush();
        }
    }

    public static void Render(AvatarState state)
    {
        var mood = state.Current;
        var label = mood.ToString().ToLowerInvariant();
        var face = FaceFor(mood);
        var color = ColorFor(mood);
        var reset = "\x1b[0m";
        var model = state.ClassifierModel ?? "(pending)";

        lock (ConsoleLock)
        {
            // Save cursor (scroll region + bottom-half cursor both preserved).
            Console.Out.Write("\x1b[s");

            // Redraw each avatar row in place. \x1b[2K clears the whole line.
            WriteRow(1, $"{color}     ___{reset}");
            WriteRow(2, $"{color}    /   \\{reset}");
            WriteRow(3, $"{color}   | {face} |{reset}    mood: {label,-11} confidence: {state.Confidence:F2}   via: {model}");
            WriteRow(4, $"{color}    \\___/{reset}");
            WriteRow(5, string.Empty);
            WriteRow(6, new string('─', Math.Max(40, Console.WindowWidth - 1)));

            // Restore cursor to the REPL's position below.
            Console.Out.Write("\x1b[u");
            Console.Out.Flush();
        }
    }

    private static void WriteRow(int row, string content)
    {
        Console.Out.Write($"\x1b[{row};1H\x1b[2K{content}");
    }
}
