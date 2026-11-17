using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhantomBladeModManager;

internal static class ArenaPackageBuilder
{
    private const string ExecutableSha256 = "4415933ce33476f14c837abb61a1b60af8b78cd81783896501913a7aac69e423";
    private static readonly Dictionary<string, string> Originals = new()
    {
        ["UI/Widget/StartMenu.js"] = "1b76d61e28a654a4bb22f0ac476b4ec624a321e0a6d748ad7ef8192919485080",
        ["UI/Widget/Character_V2.js"] = "f9dd5c79375f06ad13ade3ba52cee1b7894a2cbb155f60e984bf78e9e1380819",
        ["UI/Widget/AbilityWidget.js"] = "805dee1b74d00cc8738bdc4ddfa045a9f72c32da3a22dd0843bab793b37690b4"
    };

    public static async Task<Dictionary<string, object?>> BuildAsync(string root, string game)
    {
        RuntimePackageBuilder.RequireCurrentBuild(game);
        if (await RuntimePackageBuilder.HashAsync(Path.Combine(game, "platform", "steam", "PBZ.exe")) != ExecutableSha256)
            throw new InvalidOperationException("ARENA_EXECUTABLE_MISMATCH");
        var toolkit = Path.Combine(root, "mods", "features", "phantom-blade-map", ".local-runtime", "pak-tools");
        var key = (await File.ReadAllTextAsync(Path.Combine(toolkit, "key.local"))).Trim();
        foreach (var (module, expected) in Originals)
        {
            var bytes = await FileSafety.RunToolCheckedAsync(Path.Combine(toolkit, "repak.exe"), new[] { "-a", key, "get",
                Path.Combine(game, "ProjectPBZ", "Content", "Paks", "ProjectPBZ-Windows.pak"), "ProjectPBZ/Content/JavaScript/" + module });
            if (FileSafety.BytesHash(bytes) != expected) throw new InvalidOperationException("ARENA_SOURCE_MISMATCH: " + module);
        }
        var modRoot = Path.Combine(root, "mods", "features", "phantom-blade-training");
        async Task<JsonNode> Read(string file) => JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, file)))!;
        var catalog = (await Read("data/encounter-candidates.json")).AsObject();
        var sources = await Read("data/encounter-sources.json");
        if (sources["buildId"]!.GetValue<string>() != RuntimePackageBuilder.BuildId) throw new InvalidOperationException("ARENA_CATALOG_BUILD");
        var evidence = sources["evidence"]!.AsArray().ToDictionary(n => n!["bossId"]!.GetValue<string>());
        foreach (var scene in catalog["scenes"]!.AsArray())
        {
            var source = evidence[scene!["bossId"]!.GetValue<string>()]!;
            scene["spawner"] = (source["spawner"] ?? source["actor"])!.DeepClone();
            scene["actor"] = source["actor"]!.DeepClone();
        }
        var locales = new JsonObject { ["en"] = await Read("locales/en.json"), ["zh-Hans"] = await Read("locales/zh-Hans.json") };
        if (!locales["en"]!.AsObject().Select(p => p.Key).ToHashSet().SetEquals(locales["zh-Hans"]!.AsObject().Select(p => p.Key)))
            throw new InvalidOperationException("ARENA_LOCALE_KEYS");
        var output = Path.Combine(modRoot, "dist", "runtime");
        FileSafety.RemoveTreeIfExists(output, new[] { Path.Combine(modRoot, "dist") });
        var modules = Path.Combine(output, "Scripts", "PhantomBladeArena");
        await RuntimePackageBuilder.CopyScriptsAsync(Path.Combine(modRoot, "puerts"), modules);
        async Task Module(string name, JsonNode value) => await File.WriteAllTextAsync(Path.Combine(modules, name + ".generated.js"),
            "\"use strict\";\nmodule.exports=" + value.ToJsonString() + ";\n", new UTF8Encoding(false));
        await Module("Catalog", catalog);
        await Module("Locales", locales);
        await Module("Equipment", await Read("data/equipment-catalog.json"));
        await Module("PracticeCatalog", await Read("data/practice-catalog.json"));
        await Module("Template", await Read("data/high-cycle-template.json"));
        await Module("Stages", await Read("data/progression-presets.draft.json"));
        await RuntimePackageBuilder.AddLoaderAsync(root, output);
        var files = await RuntimePackageBuilder.InventoryAsync(output);
        var manifest = new { schema = 1, id = "arena", version = "0.1.0", backend = "nativeRuntime", arenaBridge = 1,
            buildId = RuntimePackageBuilder.BuildId, executableSha256 = ExecutableSha256,
            runtimeVersion = RuntimePackageBuilder.RuntimeVersion, runtimeVerified = false, originals = Originals, files };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, FileSafety.JsonOptions));
        return new() { ["id"] = "arena", ["version"] = "0.1.0", ["output"] = output, ["files"] = files.Count };
    }
}
