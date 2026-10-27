using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDevControlPanel.Core;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };
}
