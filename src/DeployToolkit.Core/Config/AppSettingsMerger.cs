using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeployToolkit.Core.Config;

/// <summary>
/// One key's before/after value, for the confirmation UI the plan calls for
/// ("shows a before/after diff; explicit confirm before writing").
/// </summary>
public sealed record AppSettingsChange(string DottedKey, JsonNode? OldValue, JsonNode? NewValue)
{
    public bool IsNewKey => OldValue is null;
}

/// <summary>
/// Deep-merges a flat, dotted-key delta (e.g. "Smtp:Host" -> "smtp.new.com",
/// matching .NET configuration's colon-separated key convention) into an
/// existing appsettings.json, touching only the keys present in the delta.
/// Everything else in the target file — connection strings, per-client
/// overrides never seen by this tool — is preserved byte-for-byte in
/// structure, just re-serialized.
/// </summary>
public static class AppSettingsMerger
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Computes what would change, without writing anything — used to build
    /// the confirmation screen before <see cref="Apply"/> is called.
    /// </summary>
    public static IReadOnlyList<AppSettingsChange> Preview(
        string existingAppSettingsJson,
        IReadOnlyDictionary<string, object?> delta)
    {
        var root = string.IsNullOrWhiteSpace(existingAppSettingsJson)
            ? new JsonObject()
            : (JsonNode.Parse(existingAppSettingsJson) as JsonObject ?? new JsonObject());

        var changes = new List<AppSettingsChange>();

        foreach (var (dottedKey, newValue) in delta)
        {
            var oldValue = GetByDottedKey(root, dottedKey);
            var newNode = ToJsonNode(newValue);

            // Only report an actual change, not a no-op.
            if (JsonNode.DeepEquals(oldValue, newNode))
                continue;

            changes.Add(new AppSettingsChange(dottedKey, oldValue?.DeepClone(), newNode?.DeepClone()));
        }

        return changes;
    }

    /// <summary>
    /// Applies the delta on top of the existing JSON and returns the new
    /// full file content. Call <see cref="Preview"/> first and get
    /// confirmation — this method just does the write, it doesn't gate on
    /// anything itself.
    /// </summary>
    public static string Apply(string existingAppSettingsJson, IReadOnlyDictionary<string, object?> delta)
    {
        var root = string.IsNullOrWhiteSpace(existingAppSettingsJson)
            ? new JsonObject()
            : (JsonNode.Parse(existingAppSettingsJson) as JsonObject ?? new JsonObject());

        foreach (var (dottedKey, newValue) in delta)
        {
            SetByDottedKey(root, dottedKey, ToJsonNode(newValue));
        }

        return root.ToJsonString(WriteOptions);
    }

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => JsonSerializer.SerializeToNode(value),
    };

    private static JsonNode? GetByDottedKey(JsonObject root, string dottedKey)
    {
        var parts = dottedKey.Split(':', StringSplitOptions.RemoveEmptyEntries);
        JsonNode? current = root;

        foreach (var part in parts)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current))
                return null;
        }

        return current;
    }

    private static void SetByDottedKey(JsonObject root, string dottedKey, JsonNode? value)
    {
        var parts = dottedKey.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException("Key cannot be empty.", nameof(dottedKey));

        var current = root;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (current.TryGetPropertyValue(part, out var next) && next is JsonObject nextObj)
            {
                current = nextObj;
            }
            else
            {
                var created = new JsonObject();
                current[part] = created;
                current = created;
            }
        }

        current[parts[^1]] = value;
    }
}
