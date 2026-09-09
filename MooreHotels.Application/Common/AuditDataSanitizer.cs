using System.Text.Json;
using System.Text.Json.Nodes;

namespace MooreHotels.Application.Common;

public static class AuditDataSanitizer
{
    public const string RedactedValue = "[REDACTED]";

    private static readonly string[] SensitivePropertySuffixes =
    [
        "address", "authorization", "body", "cookie", "credential",
        "description", "details", "email", "firstname", "ipaddress",
        "lastname", "message", "name", "notes", "password", "phone",
        "reason", "recoverycode", "secret", "sharedkey", "title", "token"
    ];

    public static string? SanitizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(RedactedValue);
        }

        if (root is not JsonObject)
            return JsonSerializer.Serialize(RedactedValue);

        SanitizeNode(root);
        return root?.ToJsonString() ?? "null";
    }

    private static void SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var propertyName in jsonObject.Select(property => property.Key).ToArray())
            {
                if (IsSensitive(propertyName))
                    jsonObject[propertyName] = RedactedValue;
                else
                    SanitizeNode(jsonObject[propertyName]);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
                SanitizeNode(item);
        }
    }

    private static bool IsSensitive(string propertyName)
    {
        var normalized = new string(propertyName
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return SensitivePropertySuffixes.Any(suffix =>
            normalized.EndsWith(suffix, StringComparison.Ordinal));
    }
}
