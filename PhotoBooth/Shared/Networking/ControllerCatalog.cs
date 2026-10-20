using System.Text.Json;

namespace PhotoBooth.Networking;

// LAN peers publish only their own definitions. A local client receives this combined catalog.
internal sealed record ControllerCatalog
{
    public required string LocalControllerId { get; init; }
    public required ControllerSharedDefinitions[] Controllers { get; init; }
    public const int MaximumBytes = 16 * 1024 * 1024;
    public void Validate()
    {
        if (!Guid.TryParse(LocalControllerId, out _) || Controllers is null || Controllers.Length is < 1 or > 128 ||
            Controllers.Count(c => c?.ControllerId == LocalControllerId) != 1)
            throw new ArgumentException("A controller catalog requires one local controller and at most 128 distinct controllers.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var controller in Controllers)
        {
            if (controller is null || !ids.Add(controller.ControllerId)) throw new ArgumentException("Duplicate or missing controller definition.");
            controller.Validate();
        }
    }
    public byte[] Serialize()
    {
        Validate(); var bytes = JsonSerializer.SerializeToUtf8Bytes(this, ControllerSharedDefinitions.Json);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Controller catalog exceeds 16 MB.");
        return bytes;
    }
}
