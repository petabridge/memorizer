using System.Text.Json;
using System.Text.Json.Nodes;

namespace Memorizer.Services;

public static class ProviderConfigSanitizer
{
    public static JsonObject RedactForDisplay(JsonDocument config)
    {
        var node = JsonNode.Parse(config.RootElement.GetRawText());
        if (node is not JsonObject obj)
        {
            return new JsonObject();
        }

        RedactObject(obj);
        return obj;
    }

    public static JsonDocument CleanForStorage(IReadOnlyDictionary<string, object> requestConfig)
    {
        var node = JsonSerializer.SerializeToNode(requestConfig) as JsonObject ?? new JsonObject();
        CleanObjectForStorage(node);
        return JsonDocument.Parse(node.ToJsonString());
    }

    private static void CleanObjectForStorage(JsonObject obj)
    {
        foreach (var (key, value) in obj.ToList())
        {
            if (IsConfiguredMarker(key) || IsSensitiveKey(key))
            {
                obj.Remove(key);
                continue;
            }

            if (value is JsonObject childObject)
            {
                CleanObjectForStorage(childObject);
            }
            else if (value is JsonArray childArray)
            {
                CleanArrayForStorage(childArray);
            }
        }
    }

    private static void CleanArrayForStorage(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item is JsonObject childObject)
            {
                CleanObjectForStorage(childObject);
            }
            else if (item is JsonArray childArray)
            {
                CleanArrayForStorage(childArray);
            }
        }
    }

    private static void RedactObject(JsonObject obj)
    {
        foreach (var (key, value) in obj.ToList())
        {
            if (IsSensitiveKey(key))
            {
                obj.Remove(key);
                obj[$"{key}Configured"] = HasConfiguredValue(value);
                continue;
            }

            if (value is JsonObject childObject)
            {
                RedactObject(childObject);
            }
            else if (value is JsonArray childArray)
            {
                RedactArray(childArray);
            }
        }
    }

    private static void RedactArray(JsonArray array)
    {
        foreach (var item in array)
        {
            if (item is JsonObject childObject)
            {
                RedactObject(childObject);
            }
            else if (item is JsonArray childArray)
            {
                RedactArray(childArray);
            }
        }
    }

    private static bool HasConfiguredValue(JsonNode? value)
    {
        if (value is null || value.GetValueKind() is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        return value.GetValueKind() != JsonValueKind.String ||
               !string.IsNullOrWhiteSpace(value.GetValue<string>());
    }

    private static bool IsConfiguredMarker(string key)
    {
        return key.EndsWith("Configured", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSensitiveKey(string key)
    {
        if (IsConfiguredMarker(key))
        {
            return false;
        }

        var normalized = key.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized is "apikey" or "token" or "accesstoken" or "secret" or "password" or "authorization" ||
               normalized.EndsWith("apikey", StringComparison.Ordinal) ||
               normalized.EndsWith("token", StringComparison.Ordinal) ||
               normalized.EndsWith("secret", StringComparison.Ordinal) ||
               normalized.EndsWith("password", StringComparison.Ordinal);
    }
}
