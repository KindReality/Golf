using System.Text.Json;
namespace PhotoBooth.Networking;

// Creation geometry is dimensions only. Position belongs to the Controller's perspective editor.
public sealed record PerspectiveCreateRequest
{
    public string ControllerId { get; init; } = "";
    public string PerspectiveId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public double WidthPercent { get; init; } = 100;
    public double HeightPercent { get; init; } = 100;
    public void Validate()
    {
        if (!Guid.TryParse(ControllerId, out _) || !Guid.TryParse(PerspectiveId, out _)) throw new ArgumentException("Perspective creation requires Controller and perspective IDs.");
        new PerspectiveDefinition { Id = PerspectiveId, Name = Name, Rectangle = Rectangle() }.Validate();
    }
    public PerspectiveRectangle Rectangle() => new() { X = (100 - WidthPercent) / 2, Y = (100 - HeightPercent) / 2, Width = WidthPercent, Height = HeightPercent };
    public byte[] Command(BroadcastOptions options, string device)
    {
        Validate();
        return BroadcastEnvelope.Create(options, "command", device, new { command = "perspective.create", ControllerId, PerspectiveId, Name, WidthPercent, HeightPercent });
    }
    public static PerspectiveCreateRequest Parse(JsonElement body)
    {
        foreach (var required in new[] { "controllerId", "perspectiveId", "name", "widthPercent", "heightPercent" })
            if (!body.EnumerateObject().Any(f => f.Name.Equals(required, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Missing perspective creation field: " + required);
        foreach (var field in body.EnumerateObject())
            if (new[] { "x", "y", "rectangle", "media", "value" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Perspective creation includes width and height only, without position or media.");
        var request = body.Deserialize<PerspectiveCreateRequest>(ControllerSharedDefinitions.Json) ?? throw new ArgumentException("Missing perspective creation."); request.Validate(); return request;
    }
}
