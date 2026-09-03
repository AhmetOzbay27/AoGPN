using System.Text.Json;

namespace ServiceLib.Common;

public sealed record DashboardMessage(string Action, IReadOnlyDictionary<string, JsonElement> Payload)
{
    public bool TryGetString(string name, out string value)
    {
        value = string.Empty;
        if (!Payload.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = element.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 64)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    public bool TryGetStringArray(string name, out string[] values)
    {
        values = [];
        if (!Payload.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var result = element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 64)
            .Cast<string>()
            .ToArray();
        values = result;
        return result.Length > 0;
    }

    public bool TryGetBoolean(string name, out bool value)
    {
        value = false;
        if (!Payload.TryGetValue(name, out var element)
            || (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }
}

public static class DashboardMessageParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    public static bool TryParse(string? raw, out DashboardMessage? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 128 * 1024)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw, Options);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("action", out var actionElement)
                || actionElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var action = actionElement.GetString();
            if (!DashboardMessagePolicy.IsAllowedAction(action))
            {
                return false;
            }

            var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!property.NameEquals("action"))
                {
                    payload[property.Name] = property.Value.Clone();
                }
            }

            message = new DashboardMessage(action!, payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
