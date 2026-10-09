using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedmineCli;

/// <summary>
/// JSON helpers. Redmine's answers are kept as <see cref="JsonNode"/> and printed as they came (the --json output is
/// "Redmine's response as is"), so nothing here goes through reflection-based serialization (the exe is Native AOT).
/// </summary>
internal static class Json
{
    // Like JSON.stringify(value, null, 2): two spaces, LF, Japanese as is.
    private static readonly JsonWriterOptions Indented = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Format(JsonNode? node) => Write(node, Indented);

    public static string Serialize(JsonNode? node) => Write(node, Compact);

    private static string Write(JsonNode? node, JsonWriterOptions options)
    {
        if (node is null)
        {
            return "null";
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            node.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Parses text (a leading BOM is ignored). Throws <see cref="JsonException"/> when it is not JSON.</summary>
    public static JsonNode? Parse(string text) => JsonNode.Parse(text.TrimStart('﻿'));

    /// <summary>The member of an object, or null when the node is not an object or has no such member.</summary>
    public static JsonNode? Get(this JsonNode? node, string key) => node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary>The value when it is a JSON string, else null.</summary>
    public static string? Str(this JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Like JavaScript's String(x) for display: "" for null, the text of a string, the JSON of anything else.</summary>
    public static string Text(this JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => Serialize(node),
    };

    /// <summary>The value when it is an integral JSON number, else null.</summary>
    public static long? Long(this JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && long.TryParse(Serialize(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    /// <summary>The value when it is a JSON number, else null.</summary>
    public static double? Double(this JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(Serialize(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    /// <summary>JavaScript truthiness: false for null, false, 0, "" (objects and arrays are true).</summary>
    public static bool Truthy(this JsonNode? node) => node switch
    {
        null => false,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => value.Double() is not (0 or double.NaN),
            _ => true,
        },
        _ => true,
    };

    /// <summary>The array's items taken out of it, so they can be put into other nodes (a node has one parent).</summary>
    public static List<JsonNode> Detach(this JsonArray? array)
    {
        if (array is null)
        {
            return [];
        }
        var items = array.Where(item => item is not null).Select(item => item!).ToList();
        array.Clear();
        return items;
    }

    /// <summary>The name member of an object ("" when missing), like format.js name().</summary>
    public static string Name(this JsonNode? node) => node.Get("name").Str() ?? "";
}
