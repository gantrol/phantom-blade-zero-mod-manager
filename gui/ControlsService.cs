using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhantomBladeModManager;

internal sealed record ControlDef(
    string Id,
    string Mod,
    string Scope,
    string Key,
    string? Modifier = null,
    string? Command = null,
    string? Field = null,
    bool Survey = false,
    bool AllowNone = false,
    List<string>? Aliases = null);

internal static class ControlsService
{
    private static readonly string[] Digits = { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };

    public static readonly string[] AllowedKeys = BuildAllowedKeys();

    public static readonly string[] NativeKeys =
    {
        "Escape", "Enter", "SpaceBar", "Tab", "Q", "E", "Z", "C", "Left", "Right", "Up", "Down"
    };

    private static string[] BuildAllowedKeys()
    {
        var list = new List<string> { "None" };
        for (int i = 1; i <= 24; i++) list.Add("F" + i);
        list.AddRange(new[]
        {
            "BackSpace", "Tab", "Enter", "Escape", "SpaceBar", "PageUp", "PageDown",
            "End", "Home", "Left", "Up", "Right", "Down", "Insert", "Delete",
            "Add", "Subtract", "Equals", "Hyphen"
        });
        for (char c = 'A'; c <= 'Z'; c++) list.Add(c.ToString());
        list.AddRange(Digits);
        return list.ToArray();
    }

    public static List<ControlDef> LoadDefinitions(string root)
    {
        var path = Path.Combine(root, "controls.json");
        return JsonSerializer.Deserialize<List<ControlDef>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static string ControlsDir()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs)) FileSafety.Fail("invalid_path", ("label", "user profile"));
        return FileSafety.ValidatePath(Path.Combine(docs, "PbzMods", "Config", "PhantomBladeControls"), "controls directory");
    }

    public static string ControlsFile() => Path.Combine(ControlsDir(), "New_CSV.csv");

    private static string NormalizeKey(string raw)
    {
        if (raw.Length == 1 && raw[0] >= '0' && raw[0] <= '9')
            return Digits[raw[0] - '0'];
        return raw;
    }

    private static Dictionary<string, string> ParseCsv(string text, Dictionary<string, string> defaults)
    {
        var lines = Regex.Split((text ?? "").TrimStart('\uFEFF').Trim(), @"\r?\n").Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0 || (lines[0].Trim() != "Name,Value" && lines[0].Trim() != "---,value"))
            FileSafety.Fail("invalid_data", ("target", "PhantomBladeControls/New_CSV.csv"));
        var values = new Dictionary<string, string>(defaults, StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var comma = line.IndexOf(',');
            if (comma <= 0) FileSafety.Fail("invalid_data", ("target", "PhantomBladeControls/New_CSV.csv"));
            var key = line[..comma].Trim();
            var raw = line[(comma + 1)..].Trim();
            var unquoted = raw.StartsWith('"') && raw.EndsWith('"') ? raw[1..^1].Replace("\"\"", "\"") : raw;
            if (!values.ContainsKey(key))
                FileSafety.Fail("invalid_data", ("target", "PhantomBladeControls/New_CSV.csv"));
            values[key] = NormalizeKey(unquoted);
        }
        return values;
    }

    private static string SerializeCsv(List<ControlDef> defs, Dictionary<string, string> values)
    {
        var keys = defs.Select(d => d.Id).Concat(new[] { "tap.buy", "tap.upgrade" });
        var rows = keys.Select(k => $"{k},{values[k]}");
        return "Name,Value\n" + string.Join("\n", rows) + "\n";
    }

    private static Dictionary<string, string> ReadValues(List<ControlDef> defs)
    {
        var dir = ControlsDir();
        var file = ControlsFile();
        FileSafety.AssertRealParentChain(file, dir, "controls file");
        var defaults = defs.ToDictionary(d => d.Id, d => NormalizeKey(d.Key), StringComparer.Ordinal);
        defaults["tap.buy"] = "true";
        defaults["tap.upgrade"] = "true";
        var info = FileSafety.GetNodeInfoIfExists(file, "controls file");
        if (info == null) return defaults;
        if (info is not FileInfo) FileSafety.Fail("expected_file", ("target", file));
        var values = ParseCsv(File.ReadAllText(file), defaults);
        foreach (var def in defs)
        {
            var v = values[def.Id];
            if (!AllowedKeys.Contains(v) || (def.Field != null && !def.AllowNone && v == "None"))
                FileSafety.Fail("controls_unknown_binding", ("id", def.Id));
        }
        foreach (var toggle in new[] { "tap.buy", "tap.upgrade" })
        {
            if (values[toggle] is not ("true" or "false"))
                FileSafety.Fail("invalid_data", ("target", toggle));
        }
        return values;
    }

    private static bool IsModEnabled(string game, ManagerState state, string modId)
    {
        if (state.Mods.TryGetValue(modId, out var runtime) && runtime.Backend == "nativeRuntime")
            return runtime.Enabled;
        var paks = Path.Combine(game, "ProjectPBZ", "Content", "Paks", "~mods");
        return modId switch
        {
            "map" => File.Exists(Path.Combine(paks, "zzz_PhantomBladeMap_P.pak")),
            "quickActions" => File.Exists(Path.Combine(paks, "zzzz_PhantomBladeQuickActions_P.pak")),
            "sprint" => File.Exists(Path.Combine(paks, "zzzz_PhantomBladeSprint_P.pak")),
            "teleport" => File.Exists(Path.Combine(paks, "zzzz_PhantomBladeTeleport_P.pak")),
            _ => state.Mods.TryGetValue(modId, out var entry) && entry.Enabled,
        };
    }

    public static Dictionary<string, object?> ReadControls(string root, ManagerState state)
    {
        var defs = LoadDefinitions(root);
        var values = ReadValues(defs);
        var catalog = ManagerEngine.LoadCatalog(root);
        var available = catalog.Select(mod => mod.Id).ToHashSet(StringComparer.Ordinal);
        var bridgeEnabled = catalog.Any(mod => mod.Id == "map" && mod.Backend == "nativeRuntime")
            || IsModEnabled(state.Game, state, "quickActions");
        var rows = defs.Where(d => available.Contains(d.Mod)).Select(d => new Dictionary<string, object?>
        {
            ["id"] = d.Id,
            ["mod"] = d.Mod,
            ["scope"] = d.Scope,
            ["key"] = values[d.Id],
            ["modifier"] = d.Modifier,
            ["field"] = d.Field,
            ["allowNone"] = d.AllowNone,
            ["defaultKey"] = NormalizeKey(d.Key),
            ["aliases"] = d.Aliases,
            ["enabled"] = IsModEnabled(state.Game, state, d.Mod),
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["file"] = ControlsFile(),
            ["values"] = values,
            ["rows"] = rows,
            ["keys"] = AllowedKeys,
            ["nativeKeys"] = NativeKeys,
            ["bridgeEnabled"] = bridgeEnabled,
        };
    }

    public static Dictionary<string, object?> WriteControls(string root, Dictionary<string, string> rawPatch, ManagerState state, bool dryRun = false)
    {
        if (rawPatch.Count == 0) FileSafety.Fail("missing_option", ("option", "--set id=value"));
        var defs = LoadDefinitions(root);
        var values = ReadValues(defs);
        foreach (var (key, rawValue) in rawPatch)
        {
            var value = NormalizeKey(rawValue);
            if (key is "tap.buy" or "tap.upgrade")
            {
                if (value is not ("true" or "false")) FileSafety.Fail("boolean_required", ("key", key));
                values[key] = value;
                continue;
            }
            var def = defs.FirstOrDefault(d => d.Id == key);
            if (def == null) FileSafety.Fail("unsupported_key", ("key", key));
            if (!AllowedKeys.Contains(value) || (def!.Field != null && !def.AllowNone && value == "None"))
                FileSafety.Fail("controls_unknown_binding", ("id", key));
            values[key] = value;
        }
        var dir = ControlsDir();
        var file = ControlsFile();
        if (!dryRun)
            FileSafety.WriteTextAtomic(file, SerializeCsv(defs, values), new[] { dir });
        var result = ReadControls(root, state);
        result["dryRun"] = dryRun;
        result["updated"] = rawPatch;
        if (dryRun) result["values"] = values;
        return result;
    }
}
