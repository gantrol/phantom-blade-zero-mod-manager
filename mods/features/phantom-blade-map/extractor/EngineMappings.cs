using System.Text.Json;
using CUE4Parse.MappingsProvider;

namespace PhantomBladeMap.Extractor;

// Native schemas exported from the matching game preserve custom field order.
// Editor schemas are useful references; their provenance remains in the output.
static class EngineMappings
{
    public static AbstractTypeMappingsProvider Read(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var result = new EmptyMappings();
        var context = result.MappingsForGame!;
        foreach (var type in doc.RootElement.GetProperty("types").EnumerateArray())
        {
            var name = type.GetProperty("name").GetString()!;
            var fields = type.GetProperty("properties").EnumerateArray().ToDictionary(p => p.GetProperty("index").GetInt32(),
                p => new PropertyInfo(p.GetProperty("arrayIndex").GetInt32(), p.GetProperty("name").GetString()!, ParseType(p), null));
            context.Types[name] = new Struct(context, name, null, fields, fields.Count);
        }
        var objectArray = new PropertyType("ArrayProperty", innerType: new PropertyType("ObjectProperty"));
        context.Types.TryAdd("FastGeoContainer", new Struct(context, "FastGeoContainer", null,
            new Dictionary<int, PropertyInfo> { [0] = new(0, "Assets", objectArray) }, 1));
        return result;
    }

    static PropertyType ParseType(JsonElement p)
    {
        string? Text(string key) => p.TryGetProperty(key, out var field) ? field.GetString() : null;
        PropertyType? Child(string key) => p.TryGetProperty(key, out var field) ? ParseType(field) : null;
        // Enum labels are not required for geometric extraction. Retain the
        // underlying serialized numeric type when no game enum table exists.
        var type = Text("type")!;
        if (type == "EnumProperty") return Child("inner") ?? new PropertyType("ByteProperty");
        return new PropertyType(type, Text("struct"), Child("inner"), Child("value"));
    }
}
