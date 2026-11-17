using System.Text;
using System.Text.Json;

namespace PhantomBladeModManager;

internal static class LoadoutsPackageBuilder
{
    private const string Version = "0.1.0";
    private const string ExecutableSha256 = "4415933ce33476f14c837abb61a1b60af8b78cd81783896501913a7aac69e423";
    private static readonly Dictionary<string, string> Originals = new()
    {
        ["ProjectPBZ/Content/JavaScript/UI/Widget/Character_V2.js"] = "f9dd5c79375f06ad13ade3ba52cee1b7894a2cbb155f60e984bf78e9e1380819",
        ["ProjectPBZ/Content/JavaScript/UI/Widget/Character_V3.js"] = "d3af74d7c1f16fe70e6290809a47b6f5064c07b07f7dfba8cb6bddb317b4d561"
    };

    public static async Task<Dictionary<string, object?>> BuildAsync(string root, string game)
    {
        RuntimePackageBuilder.RequireCurrentBuild(game);
        if (await RuntimePackageBuilder.HashAsync(Path.Combine(game, "platform", "steam", "PBZ.exe")) != ExecutableSha256)
            throw new InvalidOperationException("LOADOUTS_EXECUTABLE_MISMATCH");
        var toolkit = Path.Combine(root, "mods", "features", "phantom-blade-map", ".local-runtime", "pak-tools");
        var key = (await File.ReadAllTextAsync(Path.Combine(toolkit, "key.local"))).Trim();
        foreach (var (asset, expected) in Originals)
        {
            var bytes = await FileSafety.RunToolCheckedAsync(Path.Combine(toolkit, "repak.exe"),
                new[] { "-a", key, "get", Path.Combine(game, "ProjectPBZ", "Content", "Paks", "ProjectPBZ-Windows.pak"), asset });
            if (FileSafety.BytesHash(bytes) != expected) throw new InvalidOperationException($"LOADOUTS_SOURCE_MISMATCH: {asset}");
        }
        var modRoot = Path.Combine(root, "mods", "features", "phantom-blade-loadouts");
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "data", "presets.json")));
        using var items = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "data", "items.json")));
        var locales = new Dictionary<string, Dictionary<string, string>>();
        foreach (var language in new[] { "en", "zh-CN" })
            locales[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(
                await File.ReadAllTextAsync(Path.Combine(modRoot, "locales", language + ".json")))!;
        Validate(data.RootElement, items.RootElement, locales);

        var output = Path.Combine(modRoot, "dist", "runtime");
        FileSafety.RemoveTreeIfExists(output, new[] { Path.Combine(modRoot, "dist") });
        var modules = Path.Combine(output, "Scripts", "PhantomBladeLoadouts");
        await RuntimePackageBuilder.CopyScriptsAsync(Path.Combine(modRoot, "puerts"), modules);
        await WriteModuleAsync(Path.Combine(modules, "Presets.generated.js"), data.RootElement);
        await WriteModuleAsync(Path.Combine(modules, "Locales.generated.js"), locales);
        await RuntimePackageBuilder.AddLoaderAsync(root, output);
        var files = await RuntimePackageBuilder.InventoryAsync(output);
        var manifest = new { schema = 1, id = "loadouts", version = Version, backend = "nativeRuntime",
            buildId = RuntimePackageBuilder.BuildId, executableSha256 = ExecutableSha256,
            runtimeVersion = RuntimePackageBuilder.RuntimeVersion, runtimeVerified = false, originals = Originals, files };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, FileSafety.JsonOptions));
        return new() { ["id"] = "loadouts", ["version"] = Version, ["output"] = output,
            ["presets"] = data.RootElement.GetProperty("presets").GetArrayLength(), ["files"] = files.Count };
    }

    private static Task WriteModuleAsync<T>(string path, T value) => File.WriteAllTextAsync(path,
        "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(value) + ";\n", new UTF8Encoding(false));

    private static void Validate(JsonElement data, JsonElement registry, Dictionary<string, Dictionary<string, string>> locales)
    {
        void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("LOADOUTS_DATA_INVALID: " + detail);
        }
        Require(data.GetProperty("schemaVersion").GetInt32() == 2, "schemaVersion");
        Require(data.GetProperty("buildId").GetString() == RuntimePackageBuilder.BuildId
            && registry.GetProperty("buildId").GetString() == RuntimePackageBuilder.BuildId, "buildId");
        Require(locales["en"].Keys.ToHashSet().SetEquals(locales["zh-CN"].Keys), "locale keys");
        var items = registry.GetProperty("items");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Resource(string key) => Require(locales.Values.All(locale => locale.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text)), key);
        foreach (var group in new[] { "stages", "useCases" })
            foreach (var item in data.GetProperty(group).EnumerateArray()) Resource(item.GetProperty("labelKey").GetString()!);
        foreach (var preset in data.GetProperty("presets").EnumerateArray())
        {
            var id = preset.GetProperty("id").GetString()!;
            Require(ids.Add(id), "duplicate " + id);
            foreach (var key in new[] { "labelKey", "summaryKey", "rotationKey" }) Resource(preset.GetProperty(key).GetString()!);
            var equipment = preset.GetProperty("equipment");
            var weaponIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kind in new[] { "main", "shadow", "accessories", "medicines" })
            {
                var values = kind == "accessories" ? equipment.GetProperty(kind).GetProperty("itemIds") : equipment.GetProperty(kind);
                Require(values.GetArrayLength() == 2 || kind == "accessories" && values.GetArrayLength() is >= 1 and <= 6, id + "/" + kind);
                var unique = new HashSet<string>(StringComparer.Ordinal);
                var counts = new Dictionary<int, int>();
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Null) { Require(kind is "main" or "shadow", id + "/null slot"); continue; }
                    var itemId = value.GetString()!;
                    Require(unique.Add(itemId) && items.TryGetProperty(itemId, out var entry) && entry.GetProperty("kind").GetString() == kind, id + "/" + itemId);
                    if (kind is "main" or "shadow") weaponIds.Add(itemId);
                    if (kind == "accessories")
                    {
                        var type = items.GetProperty(itemId).GetProperty("type").GetInt32();
                        counts[type] = counts.GetValueOrDefault(type) + 1;
                        Require(counts[type] <= 3, id + "/accessory category cap");
                    }
                }
            }
            var accessories = equipment.GetProperty("accessories");
            var mode = accessories.GetProperty("mode").GetString();
            Require(mode is "exact" or "required" or "priority", id + "/accessory mode");
            Require(mode != "exact" || accessories.GetProperty("itemIds").GetArrayLength() == 6, id + "/exact slots");
            foreach (var value in preset.GetProperty("requirements").GetProperty("soulForgedItemIds").EnumerateArray())
                Require(weaponIds.Contains(value.GetString()!), id + "/soul forge");
        }
    }
}
