namespace AvatarMoodInterceptorSample;

public enum Mood
{
    Neutral,
    Happy,
    Curious,
    Focused,
    Confused,
    Frustrated,
}

public sealed class AvatarState
{
    private readonly object _lock = new();
    private readonly List<Mood> _history = [];

    public Mood Current { get; private set; } = Mood.Neutral;
    public float Confidence { get; private set; }
    public string? ClassifierModel { get; private set; }
    public IReadOnlyList<Mood> History
    {
        get { lock (_lock) { return _history.ToArray(); } }
    }

    public void Update(Mood mood, float confidence, string? classifierModel)
    {
        lock (_lock)
        {
            Current = mood;
            Confidence = confidence;
            ClassifierModel = classifierModel;
            _history.Add(mood);
        }
    }
}
