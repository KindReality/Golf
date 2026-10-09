namespace ExperienceX;

internal enum RendererState
{
    Starting,
    Idle,
    Opening,
    Playing,
    FadingOut,
    Failed
}

// The worker is the only writer; other threads may inspect the current state.
internal sealed class RendererStateMachine
{
    private int _state = (int)RendererState.Starting;
    public RendererState Current => (RendererState)Volatile.Read(ref _state);

    public void TransitionTo(RendererState next)
    {
        var previous = Current;
        bool allowed = previous == next || next == RendererState.Failed || (previous, next) switch
        {
            (RendererState.Starting, RendererState.Idle) => true,
            (RendererState.Idle, RendererState.Opening) => true,
            (RendererState.Opening, RendererState.Playing or RendererState.Idle) => true,
            (RendererState.Playing, RendererState.FadingOut or RendererState.Idle) => true,
            (RendererState.FadingOut, RendererState.Idle) => true,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Invalid renderer transition: {previous} → {next}.");
        Volatile.Write(ref _state, (int)next);
    }
}
