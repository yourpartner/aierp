using System.Text.Json.Nodes;

namespace Server.Infrastructure;

internal static class JsonNodeExtensionsCompat
{
    public static string GetRawText(this JsonNode? node)
    {
        return node?.ToJsonString() ?? "null";
    }

    public static string GetRawText(this JsonObject? node)
    {
        return node?.ToJsonString() ?? "null";
    }

    public static string GetRawText(this JsonArray? node)
    {
        return node?.ToJsonString() ?? "[]";
    }
}
