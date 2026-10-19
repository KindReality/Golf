using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoBooth.Networking;

[JsonConverter(typeof(CommandPriorityConverter))]
public enum CommandPriority { Low, Medium, High }

public sealed class CommandPriorityConverter : JsonConverter<CommandPriority>
{
    public override CommandPriority Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString()?.ToLowerInvariant() switch
        {
            "low" => CommandPriority.Low, "medium" => CommandPriority.Medium, "high" => CommandPriority.High,
            _ => throw new JsonException("Priority must be low, medium or high.")
        } : throw new JsonException("Priority must be low, medium or high.");
    public override void Write(Utf8JsonWriter writer, CommandPriority value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
}
