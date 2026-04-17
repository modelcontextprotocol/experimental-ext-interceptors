namespace AvatarMoodInterceptorSample;

public static class AvatarRenderer
{
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

    private static ConsoleColor ColorFor(Mood mood) => mood switch
    {
        Mood.Happy => ConsoleColor.Green,
        Mood.Curious => ConsoleColor.Cyan,
        Mood.Focused => ConsoleColor.Blue,
        Mood.Confused => ConsoleColor.Yellow,
        Mood.Frustrated => ConsoleColor.Red,
        _ => ConsoleColor.Gray,
    };

    public static void Render(AvatarState state)
    {
        var mood = state.Current;
        var label = mood.ToString().ToLowerInvariant();
        var face = FaceFor(mood);
        var color = ColorFor(mood);
        var model = state.ClassifierModel ?? "(pending)";

        lock (ConsoleLock)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine();
            Console.WriteLine("     ___");
            Console.WriteLine("    /   \\");
            Console.WriteLine($"   | {face} |    mood: {label}   confidence: {state.Confidence:F2}   via: {model}");
            Console.WriteLine("    \\___/");
            Console.WriteLine();
            Console.ForegroundColor = prev;
        }
    }
}
