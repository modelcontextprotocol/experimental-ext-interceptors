using Spectre.Console;

namespace WhyInterceptorsDemo;

/// <summary>
/// Controls the demo's tempo: wait for the presenter (default), advance on a timer (--auto), or run
/// straight through (--fast).
/// </summary>
internal sealed class Pace
{
    private enum Mode { Presenter, Auto, Fast }

    private readonly Mode _mode;

    private Pace(Mode mode) => _mode = mode;

    public static Pace From(string[] args) =>
        args.Contains("--fast") ? new Pace(Mode.Fast)
        : args.Contains("--auto") || Console.IsInputRedirected ? new Pace(Mode.Auto)
        : new Pace(Mode.Presenter);

    public bool IsFast => _mode == Mode.Fast;

    /// <summary>A short breath between lines.</summary>
    public Task Breath(int milliseconds = 700) =>
        _mode == Mode.Fast ? Task.CompletedTask : Task.Delay(_mode == Mode.Auto ? milliseconds : milliseconds / 2);

    /// <summary>A stopping point: the presenter presses Enter, or the timer elapses.</summary>
    public async Task Beat(int autoMilliseconds = 2500)
    {
        switch (_mode)
        {
            case Mode.Fast:
                return;
            case Mode.Auto:
                await Task.Delay(autoMilliseconds);
                return;
            default:
                AnsiConsole.Markup("[grey35]  ⏎[/]");
                while (Console.ReadKey(intercept: true).Key is not (ConsoleKey.Enter or ConsoleKey.Spacebar or ConsoleKey.RightArrow))
                {
                }

                // Erase the prompt so recordings stay clean.
                AnsiConsole.Write("\r   \r");
                return;
        }
    }

    /// <summary>Types text out, as if someone were entering it.</summary>
    public async Task Type(string text, string style = "white")
    {
        if (_mode == Mode.Fast)
        {
            AnsiConsole.Markup($"[{style}]{Markup.Escape(text)}[/]");
            return;
        }

        foreach (var c in text)
        {
            AnsiConsole.Markup($"[{style}]{Markup.Escape(c.ToString())}[/]");
            await Task.Delay(_mode == Mode.Auto ? 28 : 18);
        }
    }
}
