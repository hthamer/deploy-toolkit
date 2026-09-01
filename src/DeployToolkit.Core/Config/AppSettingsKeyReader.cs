using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeployToolkit.Core.Config;

/// <summary>
/// Flattens an appsettings.json file into the flat, dotted-key form the
/// Deployer's <see cref="AppSettingsMerger"/> consumes (e.g.
/// <c>Logging:LogLevel:Default</c> → <c>"Information"</c>), so the Packager's
/// appsettings-delta step can auto-seed EVERY key the published
/// <c>appsettings.json</c> declares — the user then edits only the values that
/// change in this release (and can add/remove keys freely). User request:
/// "Always track this sensitive file appsettings.json, and add the appsettings
/// different keys automatically to the new package (and I can modify it
/// manually if I want)."
///
/// <b>Key convention</b>: the .NET configuration colon-separated dotted form
/// (matching <see cref="AppSettingsMerger.SetByDottedKey"/>). Nested objects
/// descend with <c>:</c>; arrays index with <c>:0</c>, <c>:1</c>, … so the
/// resulting keys round-trip through <see cref="AppSettingsMerger.Apply"/>
/// without loss. <c>null</c> values are preserved (a delta value of null
/// removes the key on the target — see <see cref="KeyValueDeltaGrid"/>).
///
/// Unreadable / non-JSON / non-object files yield an empty dictionary (the
/// caller falls through to an empty seed — never crashes the wizard).
/// </summary>
public static class AppSettingsKeyReader
{
    /// <summary>
    /// Reads <paramref name="appSettingsJson"/> and returns every leaf key as
    /// a dotted-key → value-text mapping. Values are rendered as JSON for
    /// scalars (so <c>true</c> round-trips as bool, <c>42</c> as number) and
    /// as compact JSON for objects/arrays (so nested structures stay editable
    /// in the delta grid and re-merge correctly). Returns an empty dictionary
    /// for null/empty/non-object JSON — never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadKeys(string? appSettingsJson)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(appSettingsJson))
            return result;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(appSettingsJson);
        }
        catch (JsonException)
        {
            return result; // not JSON — nothing to seed
        }
        catch (ArgumentException)
        {
            return result;
        }

        if (root is not JsonObject obj)
            return result; // top-level array / scalar — no dotted keys to seed

        Flatten(obj, prefix: string.Empty, result);
        return result;
    }

    /// <summary>Reads the <c>appsettings.json</c> at <paramref name="filePath"/>
    /// if it exists, otherwise returns an empty dictionary. Same semantics as
    /// <see cref="ReadKeys(string?)"/> — never throws (unreadable file → empty).</summary>
    public static IReadOnlyDictionary<string, string> ReadKeysFromFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            return ReadKeys(File.ReadAllText(filePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void Flatten(JsonNode node, string prefix, Dictionary<string, string> result)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    if (child is null)
                    {
                        // Explicit null value — keep the key so the user can
                        // decide (null in the delta removes the key on target).
                        result[DottedKey(prefix, name)] = "null";
                        continue;
                    }
                    Flatten(child, DottedKey(prefix, name), result);
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is null)
                    {
                        result[DottedKey(prefix, i.ToString())] = "null";
                        continue;
                    }
                    Flatten(child, DottedKey(prefix, i.ToString()), result);
                }
                break;

            default:
                // Scalar leaf — render as JSON so the delta grid's JSON-or-string
                // parser round-trips the type (true→bool, 42→number, "x"→string).
                result[prefix] = node.ToJsonString();
                break;
        }
    }

    private static string DottedKey(string prefix, string segment) =>
        prefix.Length == 0 ? segment : $"{prefix}:{segment}";
}
