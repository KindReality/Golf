namespace ExperienceX;

// Only a rapid sequence of single clicks drills through overlapping screen bodies.
// A pause, a drag, or a different selection turns the next press into normal use
// of the selected screen. Stable IDs avoid confusing layers after a reorder.
internal sealed class ScreenClickSelection(Func<long>? clock = null)
{
    private const long CycleIntervalMilliseconds = 1000;
    private string[] _candidates = [];
    private string? _lastSelected;
    private long? _clickedAt;

    internal string? Pick(string[] candidates, string? selected)
    {
        if (candidates.Length == 0) { Reset(); return null; }
        var now = clock?.Invoke() ?? Environment.TickCount64;
        var index = Array.IndexOf(candidates, selected);
        var rapid = _clickedAt is { } previous && now - previous >= 0 && now - previous < CycleIntervalMilliseconds &&
            selected == _lastSelected && candidates.SequenceEqual(_candidates);
        var picked = candidates[index < 0 ? 0 : rapid ? (index + 1) % candidates.Length : index];
        _candidates = candidates.ToArray();
        _lastSelected = picked;
        _clickedAt = now;
        return picked;
    }

    internal void Reset() { _candidates = []; _lastSelected = null; _clickedAt = null; }
}
