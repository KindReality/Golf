using PhotoBooth.Networking;

namespace ExperienceX;

// Dispatcher-owned cache; only LocalControllerConnection supplies live updates.
internal sealed class ControllerDefinitionsCache(Func<ControllerSharedDefinitions, Task> apply)
{
    private readonly Dictionary<string, (string Revision, ControllerSharedDefinitions Definitions)> _loaded = new();
    private string? _localControllerId;
    private long _seen;
    private ControllerCatalog? _currentCatalog;
    public string[] Roles => RoleCatalog.Names(_loaded.Values.SelectMany(c => c.Definitions.Roles));
    public ControllerSharedDefinitions? LocalController => Environment.TickCount64 - _seen < 10000 &&
        _localControllerId is not null && _loaded.TryGetValue(_localControllerId, out var local) ? local.Definitions : null;
    public PerspectiveSelection? FindPerspective(string controllerId, string perspectiveId) =>
        _loaded.TryGetValue(controllerId, out var loaded) && loaded.Definitions.Perspectives.FirstOrDefault(p => p.Id == perspectiveId) is { } p ? loaded.Definitions.Select(p) : null;
    public IReadOnlyList<PerspectiveSelection> Perspectives => _loaded.Values.SelectMany(c => c.Definitions.Perspectives.Select(c.Definitions.Select))
        .OrderBy(p => p.ControllerName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public void Seed(IEnumerable<ControllerSharedDefinitions> definitions)
    {
        foreach (var d in definitions) { d.Validate(); _loaded[d.ControllerId] = (ControllerSharedDefinitions.Revision(d.Serialize()), d); }
    }
    public async Task ApplyAsync(ControllerCatalog catalog)
    {
        if (ReferenceEquals(_currentCatalog, catalog)) { _seen = Environment.TickCount64; return; }
        catalog.Validate();
        foreach (var definition in catalog.Controllers)
        {
            var revision = ControllerSharedDefinitions.Revision(definition.Serialize());
            if (_loaded.TryGetValue(definition.ControllerId, out var current) && current.Revision == revision) continue;
            await apply(definition);
            _loaded[definition.ControllerId] = (revision, definition);
        }
        _localControllerId = catalog.LocalControllerId; _seen = Environment.TickCount64; _currentCatalog = catalog;
    }
}
