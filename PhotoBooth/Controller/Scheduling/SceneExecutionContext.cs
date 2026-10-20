namespace Controller.Scheduling;

// Immutable context for logs and commands; cancelling a scene never stops sent playback.
internal sealed record SceneExecutionContext(string SceneName, long Generation = 0);
