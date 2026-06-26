using System.Text.Json;
using System.Text.Json.Serialization;

namespace CameraMcp.Server.Tools;

/// <summary>Shared JSON settings for the human/agent-readable text payloads the tools return.</summary>
internal static class CameraJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
