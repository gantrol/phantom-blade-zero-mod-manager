using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PhantomBladeModManager;

internal static class MapSettingsService
{
    private const int CurrentSchemaVersion = 6;
    private static readonly Dictionary<string, string[]> LegacyGroups = new()
    {
        ["filterCollection"] = new[] { "bell", "weapon", "shadowWeapon", "accessory", "medicine", "challenge", "amber", "blackstone", "flower" },
        ["filterDocuments"] = new[] { "book" },
        ["filterMaterials"] = new[] { "soulMaterial", "upgradeMaterial", "growthItem" },
        ["filterMoney"] = new[] { "dust" },
        ["filterKeyItems"] = new[] { "key", "treasure" },
        ["filterSupplies"] = new[] { "supply", "chest" },
    };

    private static readonly HashSet<string> LegacyKeys = new(
        new[] { "showRoute" }.Concat(LegacyGroups.Values.SelectMany(x => x)),
        StringComparer.Ordinal);

    private static readonly Dictionary<string, object> Defaults = new(StringComparer.Ordinal)
    {
        ["visibility"] = true,
        ["exploredOnly"] = false,
        ["showFog"] = true,
        ["remainingOnly"] = true,
        ["showList"] = true,
        ["view"] = "native",
        ["minimap"] = true,
        ["showFloors"] = true,
        ["showTraversal"] = true,
        ["autoCollect"] = false,
        ["filterCollection"] = true,
        ["filterDocuments"] = true,
        ["filterMaterials"] = true,
        ["filterMoney"] = true,
        ["filterKeyItems"] = true,
        ["filterSupplies"] = true,
        ["filterTraversalVertical"] = true,
        ["filterTraversalSlides"] = true,
        ["filterTraversalRopes"] = true,
        ["filterTraversalClimbing"] = true,
        ["npc"] = true,
        ["treeHeart"] = false,
        ["marker"] = true,
    };

    private static readonly Dictionary<string, string> FieldAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["map-style"] = "view",
        ["mapStyle"] = "view",
        ["view"] = "view",
        ["explored-only"] = "exploredOnly",
        ["exploredOnly"] = "exploredOnly",
        ["show-fog"] = "showFog",
        ["showFog"] = "showFog",
        ["overlay-visible"] = "visibility",
        ["overlayVisible"] = "visibility",
        ["visibility"] = "visibility",
        ["visible"] = "visibility",
        ["minimap-visible"] = "minimap",
        ["minimapVisible"] = "minimap",
        ["minimap"] = "minimap",
        ["remaining-list"] = "showList",
        ["remainingList"] = "showList",
        ["showList"] = "showList",
        ["listVisible"] = "showList",
        ["remaining-only"] = "remainingOnly",
        ["remainingOnly"] = "remainingOnly",
    };

    public static string SettingsRootDir()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs)) FileSafety.Fail("invalid_path", ("label", "user profile"));
        return FileSafety.ValidatePath(Path.Combine(docs, "PbzMods", "Config", "PhantomBladeMap"), "PuerTS settings directory");
    }

    private static string Checksum(string text)
    {
        uint value = 2166136261u;
        foreach (char c in text)
        {
            value = unchecked((value ^ c) * 16777619u);
        }
        return value.ToString("x8");
    }

    private sealed record JournalRecord(int Format, string ModId, int SchemaVersion, int Generation, int Revision, JsonObject Values);

    private static JournalRecord DecodeJournal(byte[] buffer)
    {
        var raw = buffer.Length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE
            ? Encoding.Unicode.GetString(buffer)
            : Encoding.UTF8.GetString(buffer);
        var rows = Regex.Split(raw.TrimStart('\uFEFF').Trim(), @"\r?\n");
        if (rows.Length != 2 || rows[0] != "Name,Payload,Checksum")
            FileSafety.Fail("invalid_data");
        var cells = rows[1].Split(',');
        if (cells.Length != 3 || cells[0] != "config" || Checksum(cells[1]) != cells[2])
            FileSafety.Fail("invalid_data");
        var decoded = Uri.UnescapeDataString(cells[1]);
        var node = JsonNode.Parse(decoded)?.AsObject();
        if (node == null) FileSafety.Fail("invalid_data");
        var format = node!["format"]?.GetValue<int>() ?? -1;
        var modId = node["modId"]?.GetValue<string>() ?? "";
        var schemaVersion = node["schemaVersion"]?.GetValue<int>() ?? -1;
        var generation = node["generation"]?.GetValue<int>() ?? -1;
        var revision = node["revision"]?.GetValue<int>() ?? -1;
        var values = node["values"]?.AsObject();
        if (format != 1 || modId != "PhantomBladeMap" || schemaVersion is < 1 or > CurrentSchemaVersion || generation < 0 || revision < 0 || values == null)
            FileSafety.Fail("invalid_data");

        foreach (var (key, val) in values!)
        {
            if (!Defaults.ContainsKey(key) && !LegacyKeys.Contains(key))
                FileSafety.Fail("invalid_data");
            if (key == "view")
            {
                var s = val?.GetValue<string>();
                if (s is not ("native" or "survey")) FileSafety.Fail("invalid_data");
            }
            else if (val == null || val.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            {
                FileSafety.Fail("invalid_data");
            }
        }
        return new JournalRecord(format, modId, schemaVersion, generation, revision, values);
    }

    private static Dictionary<string, object> Migrate(JournalRecord record)
    {
        var migrated = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in record.Values)
        {
            if (v == null) continue;
            migrated[k] = k == "view" ? v.GetValue<string>() : v.GetValue<bool>();
        }
        if (record.SchemaVersion < 3)
        {
            migrated["remainingOnly"] = true;
            migrated["exploredOnly"] = true;
        }
        if (record.SchemaVersion < 4)
            migrated["view"] = "native";
        if (record.SchemaVersion < 6)
            migrated["exploredOnly"] = false;
        migrated.Remove("showRoute");
        foreach (var (group, keys) in LegacyGroups)
        {
            if (!migrated.ContainsKey(group) && keys.All(k => migrated.TryGetValue(k, out var b) && b is false))
                migrated[group] = false;
            foreach (var k in keys) migrated.Remove(k);
        }
        var result = new Dictionary<string, object>(Defaults, StringComparer.Ordinal);
        foreach (var (k, v) in migrated) result[k] = v;
        return result;
    }

    private static (int? Slot, JournalRecord? Record, Dictionary<string, object> Values) LoadCurrent()
    {
        var root = SettingsRootDir();
        var records = new List<(int Slot, JournalRecord Record, string RawJson)>();
        foreach (var slot in new[] { 0, 1 })
        {
            var file = Path.Combine(root, slot.ToString(), "New_CSV.csv");
            var info = FileSafety.GetNodeInfoIfExists(file, "map settings slot");
            if (info is FileInfo fi)
            {
                if (fi.Length > 1024 * 1024) FileSafety.Fail("invalid_data");
                var bytes = File.ReadAllBytes(file);
                var rec = DecodeJournal(bytes);
                records.Add((slot, rec, rec.Values.ToJsonString()));
            }
        }
        records.Sort((a, b) => b.Record.Generation.CompareTo(a.Record.Generation));
        if (records.Count == 2 && records[0].Record.Generation == records[1].Record.Generation && records[0].RawJson != records[1].RawJson)
            FileSafety.Fail("invalid_data");
        if (records.Count == 0)
            return (null, null, new Dictionary<string, object>(Defaults, StringComparer.Ordinal));
        return (records[0].Slot, records[0].Record, Migrate(records[0].Record));
    }

    public static Dictionary<string, object?> ReadMapSettings()
    {
        var (_, _, values) = LoadCurrent();
        return FormatSnapshot(values);
    }

    public static Dictionary<string, object?> WriteMapSettings(Dictionary<string, string> patch, bool dryRun = false)
    {
        var (currentSlot, currentRecord, values) = LoadCurrent();
        bool changed = false;
        foreach (var (rawKey, rawVal) in patch)
        {
            var field = FieldAliases.TryGetValue(rawKey, out var mapped) ? mapped : rawKey;
            if (!Defaults.ContainsKey(field)) FileSafety.Fail("invalid_data", ("key", rawKey));
            if (field == "view")
            {
                if (rawVal is not ("native" or "survey")) FileSafety.Fail("invalid_data", ("key", rawKey));
                values["view"] = rawVal;
            }
            else
            {
                if (rawVal is not ("true" or "false")) FileSafety.Fail("invalid_data", ("key", rawKey));
                values[field] = rawVal == "true";
            }
            changed = true;
        }
        if (changed && !dryRun)
        {
            var root = SettingsRootDir();
            var nextSlot = currentSlot.HasValue ? 1 - currentSlot.Value : 0;
            var recordObj = new
            {
                format = 1,
                modId = "PhantomBladeMap",
                schemaVersion = CurrentSchemaVersion,
                generation = (currentRecord?.Generation ?? 0) + 1,
                revision = (currentRecord?.Revision ?? 0) + 1,
                values,
            };
            var payload = Uri.EscapeDataString(JsonSerializer.Serialize(recordObj));
            var file = Path.Combine(root, nextSlot.ToString(), "New_CSV.csv");
            var content = $"Name,Payload,Checksum\nconfig,{payload},{Checksum(payload)}\n";
            FileSafety.WriteTextAtomic(file, content, new[] { root });
        }
        return FormatSnapshot(values);
    }

    private static Dictionary<string, object?> FormatSnapshot(Dictionary<string, object> values)
    {
        return new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["backend"] = "puerts",
            ["mapStyle"] = (string)values["view"],
            ["exploredOnly"] = (bool)values["exploredOnly"],
            ["showFog"] = (bool)values["showFog"],
            ["remainingOnly"] = (bool)values["remainingOnly"],
            ["overlayVisible"] = (bool)values["visibility"],
            ["minimapVisible"] = (bool)values["minimap"],
            ["remainingList"] = (bool)values["showList"],
            ["values"] = values,
            ["defaults"] = Defaults,
        };
    }
}
