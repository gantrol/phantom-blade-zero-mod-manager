using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PhantomBladeModManager;

internal sealed record CatalogMod(
    string Id,
    string Category,
    string Folder,
    string Project,
    string? Backend = null,
    string? PakBase = null,
    string? Store = null,
    bool SaveBackup = false);

internal sealed class ModStateRecord
{
    public bool Enabled { get; set; }
    public string? Backend { get; set; }
    public string? Version { get; set; }
    public string? BuildId { get; set; }
    public string? Digest { get; set; }
    public string? Source { get; set; }
    public string? UpdatedAt { get; set; }
    public List<FileDigestEntry>? Files { get; set; }
    public List<string>? ScriptAssets { get; set; }
    public Dictionary<string, object?>? SaveBackup { get; set; }
}

internal sealed class ManagerState
{
    public int Schema { get; set; } = 1;
    public string Game { get; set; } = "";
    public string? UpdatedAt { get; set; }
    public Dictionary<string, ModStateRecord> Mods { get; set; } = new(StringComparer.Ordinal);
}

internal static class ManagerEngine
{
    private static readonly string[] PakExtensions = { "pak", "utoc", "ucas" };
    private static readonly string[] LegacyLuaFolders =
    {
        "PhantomBladeSprint",
        "PhantomBladeInfiniteTalisman",
        "PhantomBladeEquipmentSync",
        "PhantomBladeTeleport",
        "PhantomBladeStorySkip",
    };

    public static List<CatalogMod> LoadCatalog(string root)
    {
        var path = Path.Combine(root, "catalog.json");
        return JsonSerializer.Deserialize<List<CatalogMod>>(
            File.ReadAllText(path, Encoding.UTF8),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static Dictionary<string, Dictionary<string, string>> LoadLocales(string root)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root, "locales", "en.json"), Encoding.UTF8), opts)!;
        var zh = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root, "locales", "zh-CN.json"), Encoding.UTF8), opts)!;
        return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = en,
            ["zh-CN"] = zh,
        };
    }

    public static string ResolveModProjectRoot(string root, CatalogMod mod)
    {
        var subdir = string.Equals(mod.Category, "remedy", StringComparison.OrdinalIgnoreCase) ? "remedies" : "features";
        return FileSafety.ValidatePath(Path.Combine(root, "mods", subdir, mod.Project), $"{mod.Id} project");
    }

    public static string StateDirectoryFor(string game) => Path.Combine(game, "platform", "steam", ".pbz-mod-manager");

    public static string StatePathFor(string game) => Path.Combine(StateDirectoryFor(game), "state.json");

    private static string LegacyStatePathFor(string root, string game)
    {
        var id = FileSafety.StringHash(game.ToLowerInvariant())[..16];
        return Path.Combine(root, ".local", $"{id}.json");
    }

    public static string LockPathFor(string game) => Path.Combine(StateDirectoryFor(game), "operation.lock");

    public static async Task<ManagerState> ReadStateAsync(string root, string game)
    {
        var file = StatePathFor(game);
        FileSafety.AssertRealParentChain(file, game);
        if (FileSafety.GetNodeInfoIfExists(file, "state file") == null)
        {
            // The game owns installation records even when the manager is moved or reinstalled.
            file = LegacyStatePathFor(root, game);
            FileSafety.AssertRealParentChain(file, root);
        }
        if (FileSafety.GetNodeInfoIfExists(file, "state file") == null)
            return new ManagerState { Schema = 1, Game = game };
        try
        {
            var state = JsonSerializer.Deserialize<ManagerState>(
                await File.ReadAllTextAsync(file, Encoding.UTF8),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state == null || state.Schema != 1) FileSafety.Fail("invalid_data", ("target", file));
            state!.Game = game;
            state.Mods ??= new Dictionary<string, ModStateRecord>(StringComparer.Ordinal);
            return state;
        }
        catch (ManagerException) { throw; }
        catch
        {
            FileSafety.Fail("invalid_data", ("target", file));
            return null!;
        }
    }

    public static async Task WriteStateAsync(string game, ManagerState state, bool dryRun)
    {
        state.UpdatedAt = DateTime.UtcNow.ToString("O");
        if (dryRun) return;
        var file = StatePathFor(game);
        FileSafety.AssertRealParentChain(file, game);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var pending = file + ".next";
        FileSafety.AssertRealParentChain(pending, game);
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(state, FileSafety.JsonOptions) + "\n", new UTF8Encoding(false));
        File.Move(pending, file, true);
    }

    public static string? ReadInstalledGameBuildId(string game)
    {
        var manifestPath = Path.GetFullPath(Path.Combine(game, "..", "..", "appmanifest_4115450.acf"));
        try
        {
            if (FileSafety.GetNodeInfoIfExists(manifestPath, "Steam appmanifest") is not FileInfo) return null;
            var text = File.ReadAllText(manifestPath, Encoding.UTF8);
            var match = Regex.Match(text, "\"buildid\"\\s+\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryNormalizeGameDir(string? candidate, out string normalizedGame)
    {
        normalizedGame = "";
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var baseDir = FileSafety.ValidatePath(candidate!, "game directory");
            foreach (var rel in new[] { ".", "..", Path.Combine("..", "..") })
            {
                var game = Path.GetFullPath(Path.Combine(baseDir, rel));
                var exeSteam = Path.Combine(game, "platform", "steam", "PBZ.exe");
                var exeRoot = Path.Combine(game, "PBZ.exe");
                var paks = Path.Combine(game, "ProjectPBZ", "Content", "Paks");
                if ((FileSafety.GetNodeInfoIfExists(exeSteam, "PBZ.exe") is FileInfo ||
                     FileSafety.GetNodeInfoIfExists(exeRoot, "PBZ.exe") is FileInfo) &&
                    FileSafety.GetNodeInfoIfExists(paks, "Paks directory") is DirectoryInfo)
                {
                    normalizedGame = game;
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    public static string SaveConfiguredGameDir(string root, string? candidate, bool dryRun = false)
    {
        if (!TryNormalizeGameDir(candidate, out var game))
            FileSafety.Fail("invalid_game");
        var settingsPath = Path.Combine(root, "settings.local.json");
        var obj = new JsonObject();
        try
        {
            if (File.Exists(settingsPath) && JsonNode.Parse(File.ReadAllText(settingsPath, Encoding.UTF8)) is JsonObject existing)
                obj = existing;
        }
        catch { }
        obj["game"] = game;
        if (!dryRun)
            FileSafety.WriteJsonAtomic(settingsPath, obj, new[] { root });
        return game;
    }

    public static string DetectGameDir(string root, string? explicitGame)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitGame)) candidates.Add(explicitGame!);
        try
        {
            var settingsPath = Path.Combine(root, "settings.local.json");
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("game", out var gProp) && gProp.ValueKind == JsonValueKind.String)
                {
                    var g = gProp.GetString();
                    if (!string.IsNullOrWhiteSpace(g)) candidates.Add(g!);
                }
            }
        }
        catch { }
        var envGame = Environment.GetEnvironmentVariable("PBZ_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(envGame)) candidates.Add(envGame!);

        var steamRoots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        };
        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (hkcu?.GetValue("SteamPath") is string sp && !string.IsNullOrWhiteSpace(sp))
                steamRoots.Add(sp.Replace('/', '\\'));
        }
        catch { }
        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (hklm?.GetValue("InstallPath") is string ip && !string.IsNullOrWhiteSpace(ip))
                steamRoots.Add(ip.Replace('/', '\\'));
        }
        catch { }

        foreach (var steamRoot in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "PBZ"));
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf, Encoding.UTF8), "\"path\"\\s+\"([^\"\\r\\n]+)\""))
                {
                    var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                    candidates.Add(Path.Combine(lib, "steamapps", "common", "PBZ"));
                }
            }
            catch { }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryNormalizeGameDir(candidate, out var game))
                return game;
        }
        FileSafety.Fail("game_not_found");
        return null!;
    }

    private static async Task MigrateLegacyLuaIfNeeded(string root, string game, List<CatalogMod> catalog, ManagerState state, ModFileTransaction transaction)
    {
        var modsRoot = Path.Combine(game, "platform", "steam", "ue4ss", "Mods");
        var oldDisabledRoot = Path.Combine(game, "platform", "steam", ".pbz-mod-manager", "disabled");
        var oldCompanionDisabled = Path.Combine(game, "platform", "steam", ".pbz-mod-manager", "companion-disabled");
        var modsPakDir = Path.Combine(game, "ProjectPBZ", "Content", "Paks", "~mods");
        var archiveBase = Path.Combine(game, ".pbz-mod-manager", "legacy-lua-archive");

        var hasLegacy = false;
        foreach (var folder in LegacyLuaFolders)
        {
            if (FileSafety.GetNodeInfoIfExists(Path.Combine(modsRoot, folder)) != null ||
                FileSafety.GetNodeInfoIfExists(Path.Combine(oldDisabledRoot, folder)) != null)
            {
                hasLegacy = true;
                break;
            }
        }
        foreach (var ext in PakExtensions)
        {
            if (FileSafety.GetNodeInfoIfExists(Path.Combine(modsPakDir, $"zzz_PhantomBladeTalisman_P.{ext}")) != null)
            {
                hasLegacy = true;
                break;
            }
        }

        var stateNeedsMigration = state.Mods.Any(kv =>
            kv.Key is "sprint" or "talisman" or "equipment" or "teleport" or "story" &&
            kv.Value.Backend is not ("scriptPak" or "nativeRuntime"));

        if (!hasLegacy && !stateNeedsMigration) return;

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var archiveDir = Path.Combine(archiveBase, stamp);
        var allowed = new[] { game, root };
        var previouslyActiveFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task ParkTreeAsync(string source, string destination)
        {
            FileSafety.AssertRealTree(source, game);
            destination = FileSafety.AllowedPath(destination, allowed);
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Select(file => (Source: file, Target: Path.Combine(destination, Path.GetRelativePath(source, file)))).ToArray();
            await transaction.CaptureAsync(files.SelectMany(file => new[] { file.Source, file.Target }));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
        }


        foreach (var folder in LegacyLuaFolders)
        {
            var activePath = Path.Combine(modsRoot, folder);
            if (FileSafety.GetNodeInfoIfExists(activePath) != null)
            {
                previouslyActiveFolders.Add(folder);
                await ParkTreeAsync(activePath, Path.Combine(archiveDir, "active", folder));
            }
            var parkedPath = Path.Combine(oldDisabledRoot, folder);
            if (FileSafety.GetNodeInfoIfExists(parkedPath) != null)
                await ParkTreeAsync(parkedPath, Path.Combine(archiveDir, "disabled", folder));
        }
        if (FileSafety.GetNodeInfoIfExists(oldCompanionDisabled) != null)
            await ParkTreeAsync(oldCompanionDisabled, Path.Combine(archiveDir, "companion-disabled"));
        foreach (var ext in PakExtensions)
        {
            var oldPak = Path.Combine(modsPakDir, $"zzz_PhantomBladeTalisman_P.{ext}");
            if (FileSafety.GetNodeInfoIfExists(oldPak) is FileInfo fi)
            {
                var dst = Path.Combine(archiveDir, "old-talisman-pak", $"zzz_PhantomBladeTalisman_P.{ext}");
                await transaction.CaptureAsync(new[] { oldPak, dst });
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                fi.MoveTo(dst, true);
            }
        }

        foreach (var id in new[] { "sprint", "talisman", "equipment", "teleport", "story" })
        {
            var mod = catalog.FirstOrDefault(m => m.Id == id);
            var wasEnabled = (mod != null && previouslyActiveFolders.Contains(mod.Folder)) ||
                (state.Mods.TryGetValue(id, out var record) && record.Backend is not ("scriptPak" or "nativeRuntime") && record.Enabled);
            if (state.Mods.TryGetValue(id, out var rec) && rec.Backend is not ("scriptPak" or "nativeRuntime"))
            {
                state.Mods.Remove(id);
            }
            if (wasEnabled && id != "story" && mod?.Backend == "scriptPak")
            {
                await InstallScriptPak(root, game, mod, state, false, transaction, true);
            }
        }
    }

    // ==================== ScriptPak Backend ====================

    private static (string PackageRoot, JsonObject Manifest, List<FileDigestEntry> Files, string Digest, List<string> ScriptAssets)
        ReadScriptPakSource(string root, CatalogMod mod)
    {
        var modRoot = ResolveModProjectRoot(root, mod);
        var packageRoot = FileSafety.AllowedPath(Path.Combine(modRoot, "dist", "package"), new[] { modRoot }, $"{mod.Id} package");
        var manifestPath = Path.Combine(packageRoot, "manifest.json");
        if (FileSafety.GetNodeInfoIfExists(manifestPath, $"{mod.Id} manifest") is not FileInfo)
            FileSafety.Fail("missing_source", ("target", manifestPath));
        FileSafety.AssertRealTree(packageRoot, modRoot, $"{mod.Id} package");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8))?.AsObject()
            ?? throw new ManagerException("invalid_data", new() { ["target"] = manifestPath });
        if (manifest["id"]?.GetValue<string>() != mod.Id ||
            string.IsNullOrEmpty(manifest["version"]?.GetValue<string>()))
        {
            FileSafety.Fail("invalid_data", ("target", manifestPath));
        }

        var expectedPaths = PakExtensions.Select(ext => $"Paks/{mod.PakBase}.{ext}").OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var filesArray = manifest["files"]?.AsArray();
        if (filesArray == null || filesArray.Count != 3) FileSafety.Fail("invalid_data", ("target", manifestPath));
        var files = new List<FileDigestEntry>();
        foreach (var node in filesArray)
        {
            var p = node?["path"]?.GetValue<string>() ?? "";
            var b = node?["bytes"]?.GetValue<long>() ?? -1;
            var s = node?["sha256"]?.GetValue<string>() ?? "";
            files.Add(new FileDigestEntry(p, b, s));
        }
        if (!files.Select(f => f.Path).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(expectedPaths))
            FileSafety.Fail("invalid_data", ("target", manifestPath));

        foreach (var entry in files)
        {
            var full = FileSafety.AllowedPath(Path.Combine(packageRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)), new[] { packageRoot }, entry.Path);
            if (FileSafety.GetNodeInfoIfExists(full, entry.Path) is not FileInfo fi || fi.Length != entry.Bytes || FileSafety.FileHash(full) != entry.Sha256)
                FileSafety.Fail("source_corrupted", ("target", full));
        }

        var scriptAssets = manifest["scriptAssets"] is JsonArray sa
            ? sa.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList()
            : new List<string>();

        var digestLines = string.Join("\n", files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}\t{f.Bytes}\t{f.Sha256}"));
        return (packageRoot, manifest, files, FileSafety.StringHash(digestLines), scriptAssets);
    }

    private static (string ModsDir, string DisabledDir, List<(string Rel, string Path, string DisabledPath)> Files)
        ScriptPakPaths(string game, CatalogMod mod)
    {
        var modsDir = FileSafety.AllowedPath(Path.Combine(game, "ProjectPBZ", "Content", "Paks", "~mods"), new[] { game }, "Paks/~mods");
        var disabledDir = FileSafety.AllowedPath(Path.Combine(game, "platform", "steam", ".pbz-mod-manager", mod.Store!, "disabled"), new[] { game }, "disabled script pak");
        var files = PakExtensions.Select(ext =>
        {
            var name = $"{mod.PakBase}.{ext}";
            return ($"Paks/{name}", Path.Combine(modsDir, name), Path.Combine(disabledDir, name));
        }).ToList();
        return (modsDir, disabledDir, files);
    }

    private static string? CheckScriptPakGroup(List<(string Path, string Rel)> entries, List<FileDigestEntry>? expected)
    {
        var existing = new List<(string Path, string Rel, FileInfo Info)>();
        foreach (var item in entries)
        {
            var info = FileSafety.GetNodeInfoIfExists(item.Path, item.Rel);
            if (info is FileInfo fi) existing.Add((item.Path, item.Rel, fi));
            else if (info != null) return "conflict";
        }
        if (existing.Count == 0) return "absent";
        if (existing.Count != entries.Count) return "partial";
        if (expected == null || expected.Count != entries.Count) return "unmanaged";
        foreach (var item in existing)
        {
            var exp = expected.FirstOrDefault(e => e.Path == item.Rel);
            if (exp == null || exp.Bytes != item.Info.Length || exp.Sha256 != FileSafety.FileHash(item.Path))
                return "modified";
        }
        return "match";
    }

    private static CatalogMod? FindScriptAssetCollision(string root, CatalogMod mod, List<string> assets, List<CatalogMod> catalog, ManagerState state)
    {
        if (assets.Count == 0) return null;
        foreach (var other in catalog)
        {
            if (other.Id == mod.Id || other.Backend != "scriptPak") continue;
            if (!state.Mods.TryGetValue(other.Id, out var rec) || !rec.Enabled) continue;
            var otherAssets = rec.ScriptAssets;
            if (otherAssets == null || otherAssets.Count == 0)
            {
                try { otherAssets = ReadScriptPakSource(root, other).ScriptAssets; }
                catch { otherAssets = new List<string>(); }
            }
            if (otherAssets.Any(a => assets.Contains(a, StringComparer.Ordinal)))
                return other;
        }
        return null;
    }

    private static Dictionary<string, object?> InspectScriptPak(string root, string game, CatalogMod mod, ManagerState state, List<CatalogMod> catalog)
    {
        var paths = ScriptPakPaths(game, mod);
        FileSafety.AssertRealParentChain(paths.ModsDir, game, "Paks/~mods");
        FileSafety.AssertRealParentChain(paths.DisabledDir, game, "disabled script pak");
        state.Mods.TryGetValue(mod.Id, out var record);
        (string PackageRoot, JsonObject Manifest, List<FileDigestEntry> Files, string Digest, List<string> ScriptAssets)? source = null;
        string? sourceError = null;
        try { source = ReadScriptPakSource(root, mod); }
        catch (Exception ex) { sourceError = ex.Message; }

        var expectedFiles = record?.Files ?? source?.Files;
        var active = CheckScriptPakGroup(paths.Files.Select(f => (f.Path, f.Rel)).ToList(), expectedFiles);
        var parked = CheckScriptPakGroup(paths.Files.Select(f => (f.DisabledPath, f.Rel)).ToList(), expectedFiles);
        var gameBuild = ReadInstalledGameBuildId(game);
        var targetBuild = source?.Manifest["buildId"]?.GetValue<string>() ?? record?.BuildId;
        var buildCompatible = string.IsNullOrEmpty(targetBuild) || gameBuild == targetBuild;
        var scriptAssets = source?.ScriptAssets ?? record?.ScriptAssets ?? new List<string>();
        var collidingMod = FindScriptAssetCollision(root, mod, scriptAssets, catalog, state);

        bool outdated = record != null && source != null && (record.Digest != source.Value.Digest ||
            record.Version != source.Value.Manifest["version"]?.GetValue<string>());
        string status;
        bool enabled = false;
        if (!buildCompatible)
        {
            status = "incompatible_build";
            enabled = active is "match" or "modified";
        }
        else if (active != "absent" && parked != "absent") status = "conflict";
        else if (active == "match")
        {
            enabled = true;
            status = collidingMod != null ? "conflict" : outdated ? "outdated" : "enabled";
        }
        else if (parked == "match")
        {
            status = outdated ? "outdated" : "disabled";
        }
        else if (active == "modified" && parked == "absent")
        {
            enabled = true;
            status = collidingMod != null ? "conflict" : "modified";
        }
        else if (parked == "modified" && active == "absent")
        {
            status = "modified";
        }
        else if (active == "absent" && parked == "absent") status = "not_installed";
        else if (active == "unmanaged" || parked == "unmanaged") { status = "unmanaged"; enabled = active == "unmanaged"; }
        else { status = "conflict"; enabled = active != "absent"; }

        var actionable = (buildCompatible &&
            status is not ("conflict" or "unmanaged" or "modified" or "incompatible_build") &&
            (status != "not_installed" || source != null)) ||
            (!buildCompatible && enabled);

        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["category"] = mod.Category,
            ["folder"] = mod.Folder,
            ["project"] = mod.Project,
            ["backend"] = "scriptPak",
            ["status"] = status,
            ["enabled"] = enabled,
            ["actionable"] = actionable,
            ["configurable"] = false,
            ["recorded"] = record != null,
            ["sourceAvailable"] = source != null,
            ["sourceError"] = sourceError,
            ["sourceDigest"] = source?.Digest,
            ["installedDigest"] = record?.Digest,
            ["version"] = record?.Version ?? source?.Manifest["version"]?.GetValue<string>(),
            ["installedVersion"] = status == "not_installed" ? null : record?.Version ?? (active == "match" || parked == "match" ? source?.Manifest["version"]?.GetValue<string>() : null),
            ["sourceVersion"] = source?.Manifest["version"]?.GetValue<string>(),
            ["buildId"] = targetBuild,
            ["gameBuildId"] = gameBuild,
            ["collidingMod"] = collidingMod?.Id,
            ["activePath"] = paths.ModsDir,
            ["disabledPath"] = paths.DisabledDir,
        };
    }

    private static async Task<Dictionary<string, object?>> InstallScriptPak(string root, string game, CatalogMod mod, ManagerState state, bool dryRun, ModFileTransaction transaction, bool? requestedEnabled = null)
    {
        var catalog = LoadCatalog(root);
        var current = InspectScriptPak(root, game, mod, state, catalog);
        var st = current["status"] as string;
        if (st is "conflict" or "unmanaged" or "modified" or "incompatible_build")
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", st));
        var source = ReadScriptPakSource(root, mod);
        if (ReadInstalledGameBuildId(game) != source.Manifest["buildId"]?.GetValue<string>())
            FileSafety.Fail("incompatible_build", ("id", mod.Id), ("expected", source.Manifest["buildId"]?.GetValue<string>()), ("actual", ReadInstalledGameBuildId(game)));
        var colliding = FindScriptAssetCollision(root, mod, source.ScriptAssets, catalog, state);
        if (colliding != null)
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", $"script_collision:{colliding.Id}"));

        var saveBackup = mod.SaveBackup ? SaveBackupService.CreateSaveBackup(game, mod.Id, dryRun) : null;
        var enabled = requestedEnabled ?? (st == "not_installed" || (bool)current["enabled"]!);
        var paths = ScriptPakPaths(game, mod);
        await transaction.CaptureAsync(paths.Files.SelectMany(f => new[] { f.Path, f.DisabledPath }));
        if (!dryRun)
        {
            Directory.CreateDirectory(paths.ModsDir);
            foreach (var item in paths.Files)
            {
                var src = Path.Combine(source.PackageRoot, item.Rel.Replace('/', Path.DirectorySeparatorChar));
                var destination = enabled ? item.Path : item.DisabledPath;
                await RuntimePackageBuilder.CopyFileAsync(src, destination);
                File.Delete(enabled ? item.DisabledPath : item.Path);
            }
            if (CheckScriptPakGroup(paths.Files.Select(f => (enabled ? f.Path : f.DisabledPath, f.Rel)).ToList(), source.Files) != "match")
                FileSafety.Fail("install_verify_failed", ("id", mod.Id));
        }

        state.Mods[mod.Id] = new ModStateRecord
        {
            Enabled = enabled,
            Backend = "scriptPak",
            Version = source.Manifest["version"]?.GetValue<string>(),
            BuildId = source.Manifest["buildId"]?.GetValue<string>(),
            Digest = source.Digest,
            Source = Path.GetRelativePath(root, source.PackageRoot),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            Files = source.Files,
            ScriptAssets = source.ScriptAssets,
            SaveBackup = saveBackup,
        };

        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["action"] = "installed",
            ["enabled"] = enabled,
            ["dryRun"] = dryRun,
            ["digest"] = source.Digest,
            ["target"] = enabled ? paths.ModsDir : paths.DisabledDir,
            ["saveBackup"] = saveBackup,
        };
    }

    private static async Task<Dictionary<string, object?>> ToggleScriptPak(string root, string game, CatalogMod mod, ManagerState state, bool enable, bool dryRun, ModFileTransaction transaction)
    {
        var catalog = LoadCatalog(root);
        var current = InspectScriptPak(root, game, mod, state, catalog);
        var st = current["status"] as string;
        if (st == "not_installed")
        {
            if (!enable) FileSafety.Fail("not_installed", ("id", mod.Id));
            return await InstallScriptPak(root, game, mod, state, dryRun, transaction, enable);
        }
        if (st is "conflict" or "unmanaged" or "modified" || (st == "incompatible_build" && enable))
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", st));
        if (enable && st == "outdated")
            return await InstallScriptPak(root, game, mod, state, dryRun, transaction, enable);
        if ((bool)current["enabled"]! == enable)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = mod.Id,
                ["action"] = "noop",
                ["enabled"] = enable,
                ["dryRun"] = dryRun,
                ["target"] = enable ? current["activePath"] : current["disabledPath"],
            };
        }

        state.Mods.TryGetValue(mod.Id, out var record);
        (string PackageRoot, JsonObject Manifest, List<FileDigestEntry> Files, string Digest, List<string> ScriptAssets)? source = null;
        if (record == null) source = ReadScriptPakSource(root, mod);
        var expectedFiles = record?.Files ?? source!.Value.Files;
        var scriptAssets = record?.ScriptAssets ?? source?.ScriptAssets ?? new List<string>();
        if (enable)
        {
            var colliding = FindScriptAssetCollision(root, mod, scriptAssets, catalog, state);
            if (colliding != null)
                FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", $"script_collision:{colliding.Id}"));
        }

        var saveBackup = enable && mod.SaveBackup ? SaveBackupService.CreateSaveBackup(game, mod.Id, dryRun) : null;
        var paths = ScriptPakPaths(game, mod);
        await transaction.CaptureAsync(paths.Files.SelectMany(f => new[] { f.Path, f.DisabledPath }));
        if (!dryRun)
        {
            foreach (var item in paths.Files)
            {
                var from = enable ? item.DisabledPath : item.Path;
                var to = enable ? item.Path : item.DisabledPath;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to, true);
            }
            if (!enable)
            {
                if (CheckScriptPakGroup(paths.Files.Select(f => (f.DisabledPath, f.Rel)).ToList(), expectedFiles) != "match")
                    FileSafety.Fail("install_verify_failed", ("id", mod.Id));
            }
            else
            {
                if (CheckScriptPakGroup(paths.Files.Select(f => (f.Path, f.Rel)).ToList(), expectedFiles) != "match")
                    FileSafety.Fail("install_verify_failed", ("id", mod.Id));
            }
        }

        state.Mods[mod.Id] = new ModStateRecord
        {
            Enabled = enable,
            Backend = "scriptPak",
            Version = record?.Version ?? source?.Manifest["version"]?.GetValue<string>(),
            BuildId = record?.BuildId ?? source?.Manifest["buildId"]?.GetValue<string>(),
            Digest = record?.Digest ?? source?.Digest,
            Source = Path.GetRelativePath(root, Path.Combine(ResolveModProjectRoot(root, mod), "dist", "package")),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            Files = expectedFiles,
            ScriptAssets = scriptAssets,
            SaveBackup = saveBackup ?? record?.SaveBackup,
        };

        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["action"] = enable ? "enabled" : "disabled",
            ["enabled"] = enable,
            ["dryRun"] = dryRun,
            ["target"] = enable ? paths.ModsDir : paths.DisabledDir,
            ["saveBackup"] = saveBackup,
        };
    }

    private static async Task<Dictionary<string, object?>> RestoreScriptPak(string root, string game, CatalogMod mod, ManagerState state, bool dryRun, ModFileTransaction transaction)
    {
        var catalog = LoadCatalog(root);
        var current = InspectScriptPak(root, game, mod, state, catalog);
        var st = current["status"] as string;
        if (st is "conflict" or "unmanaged" or "modified")
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", st));
        var paths = ScriptPakPaths(game, mod);
        await transaction.CaptureAsync(paths.Files.SelectMany(f => new[] { f.Path, f.DisabledPath }));
        if (!dryRun)
        {
            foreach (var item in paths.Files)
            {
                FileSafety.RemoveTreeIfExists(item.Path, new[] { game });
                FileSafety.RemoveTreeIfExists(item.DisabledPath, new[] { game });
            }
        }
        state.Mods.Remove(mod.Id);
        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["action"] = st == "not_installed" ? "noop" : "restored",
            ["enabled"] = false,
            ["dryRun"] = dryRun,
            ["target"] = paths.ModsDir,
        };
    }

    // ==================== PuerTS Map Backend ====================

    private static (string PackageRoot, JsonObject Manifest, List<FileDigestEntry>PakFiles, string Digest)
        ReadPuertsMapSource(string root, CatalogMod mod)
    {
        var modRoot = ResolveModProjectRoot(root, mod);
        var packageRoot = FileSafety.AllowedPath(Path.Combine(modRoot, "dist", "puerts"), new[] { modRoot }, "map PuerTS package");
        var manifestPath = Path.Combine(packageRoot, "manifest.json");
        if (FileSafety.GetNodeInfoIfExists(manifestPath, "map PuerTS manifest") is not FileInfo)
            FileSafety.Fail("missing_source", ("target", manifestPath));
        FileSafety.AssertRealTree(packageRoot, modRoot, "map PuerTS package");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8))?.AsObject()
            ?? throw new ManagerException("invalid_data", new() { ["target"] = manifestPath });
        if (manifest["id"]?.GetValue<string>() != "map" || manifest["backend"]?.GetValue<string>() != "puerts")
            FileSafety.Fail("invalid_data", ("target", manifestPath));

        var expectedPaths = PakExtensions.Select(ext => $"Paks/zzz_PhantomBladeMap_P.{ext}").OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var pakFiles = new List<FileDigestEntry>();
        foreach (var node in manifest["files"]?.AsArray() ?? new JsonArray())
        {
            var p = node?["path"]?.GetValue<string>() ?? "";
            if (p.StartsWith("Paks/", StringComparison.Ordinal))
                pakFiles.Add(new FileDigestEntry(p, node?["bytes"]?.GetValue<long>() ?? -1, node?["sha256"]?.GetValue<string>() ?? ""));
        }
        if (!pakFiles.Select(f => f.Path).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(expectedPaths))
            FileSafety.Fail("invalid_data", ("target", manifestPath));

        foreach (var entry in pakFiles)
        {
            var full = FileSafety.AllowedPath(Path.Combine(packageRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)), new[] { packageRoot }, entry.Path);
            if (FileSafety.GetNodeInfoIfExists(full, entry.Path) is not FileInfo fi || fi.Length != entry.Bytes || FileSafety.FileHash(full) != entry.Sha256)
                FileSafety.Fail("source_corrupted", ("target", full));
        }

        var digestLines = string.Join("\n", pakFiles.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}\t{f.Bytes}\t{f.Sha256}"));
        return (packageRoot, manifest, pakFiles, FileSafety.StringHash(digestLines));
    }

    private static (string ModsDir, string DisabledDir, List<(string Rel, string Path, string DisabledPath)> Files)
        PuertsMapPaths(string game)
    {
        var modsDir = FileSafety.AllowedPath(Path.Combine(game, "ProjectPBZ", "Content", "Paks", "~mods"), new[] { game }, "Paks/~mods");
        var disabledDir = FileSafety.AllowedPath(Path.Combine(game, "platform", "steam", ".pbz-mod-manager", "puerts-map", "disabled"), new[] { game }, "disabled PuerTS map");
        var files = PakExtensions.Select(ext =>
        {
            var name = $"zzz_PhantomBladeMap_P.{ext}";
            return ($"Paks/{name}", Path.Combine(modsDir, name), Path.Combine(disabledDir, name));
        }).ToList();
        return (modsDir, disabledDir, files);
    }

    private static Dictionary<string, object?> InspectPuertsMap(string root, string game, CatalogMod mod, ManagerState state)
    {
        var paths = PuertsMapPaths(game);
        FileSafety.AssertRealParentChain(paths.ModsDir, game, "Paks/~mods");
        FileSafety.AssertRealParentChain(paths.DisabledDir, game, "disabled PuerTS map");
        state.Mods.TryGetValue(mod.Id, out var record);
        (string PackageRoot, JsonObject Manifest, List<FileDigestEntry> PakFiles, string Digest)? source = null;
        string? sourceError = null;
        try { source = ReadPuertsMapSource(root, mod); }
        catch (Exception ex) { sourceError = ex.Message; }

        var expectedFiles = record?.Files ?? source?.PakFiles;
        var active = CheckScriptPakGroup(paths.Files.Select(f => (f.Path, f.Rel)).ToList(), expectedFiles);
        var parked = CheckScriptPakGroup(paths.Files.Select(f => (f.DisabledPath, f.Rel)).ToList(), expectedFiles);
        var gameBuild = ReadInstalledGameBuildId(game);
        var targetBuild = source?.Manifest["buildId"]?.GetValue<string>() ?? record?.BuildId;
        var buildCompatible = string.IsNullOrEmpty(targetBuild) || gameBuild == targetBuild;

        bool outdated = record != null && source != null && (record.Digest != source.Value.Digest ||
            record.Version != source.Value.Manifest["version"]?.GetValue<string>());
        string status;
        bool enabled = false;
        if (!buildCompatible)
        {
            status = "incompatible_build";
            enabled = active is "match" or "modified";
        }
        else if (active != "absent" && parked != "absent") status = "conflict";
        else if (active == "match")
        {
            enabled = true;
            status = outdated ? "outdated" : "enabled";
        }
        else if (parked == "match")
        {
            status = outdated ? "outdated" : "disabled";
        }
        else if (active == "absent" && parked == "absent") status = "not_installed";
        else if (active == "unmanaged" || parked == "unmanaged") { status = "unmanaged"; enabled = active == "unmanaged"; }
        else { status = "modified"; enabled = active != "absent"; }

        var actionable = (buildCompatible &&
            status is not ("conflict" or "unmanaged" or "modified" or "incompatible_build") &&
            (status != "not_installed" || source != null)) ||
            (!buildCompatible && enabled);

        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["category"] = mod.Category,
            ["folder"] = mod.Folder,
            ["project"] = mod.Project,
            ["backend"] = "puerts",
            ["status"] = status,
            ["enabled"] = enabled,
            ["actionable"] = actionable,
            ["configurable"] = true,
            ["settingsKind"] = "puertsMap",
            ["recorded"] = record != null,
            ["sourceAvailable"] = source != null,
            ["sourceError"] = sourceError,
            ["sourceDigest"] = source?.Digest,
            ["installedDigest"] = record?.Digest,
            ["version"] = record?.Version ?? source?.Manifest["version"]?.GetValue<string>(),
            ["installedVersion"] = status == "not_installed" ? null : record?.Version ?? (active == "match" || parked == "match" ? source?.Manifest["version"]?.GetValue<string>() : null),
            ["sourceVersion"] = source?.Manifest["version"]?.GetValue<string>(),
            ["buildId"] = targetBuild,
            ["gameBuildId"] = gameBuild,
            ["activePath"] = paths.ModsDir,
            ["disabledPath"] = paths.DisabledDir,
        };
    }

    private static async Task<Dictionary<string, object?>> InstallPuertsMap(string root, string game, CatalogMod mod, ManagerState state, bool dryRun, ModFileTransaction transaction, bool? requestedEnabled = null)
    {
        var current = InspectPuertsMap(root, game, mod, state);
        var st = current["status"] as string;
        if (st is "conflict" or "unmanaged" or "modified" or "incompatible_build")
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", st));
        var source = ReadPuertsMapSource(root, mod);
        if (ReadInstalledGameBuildId(game) != source.Manifest["buildId"]?.GetValue<string>())
            FileSafety.Fail("incompatible_build", ("id", mod.Id), ("expected", source.Manifest["buildId"]?.GetValue<string>()), ("actual", ReadInstalledGameBuildId(game)));

        var enabled = requestedEnabled ?? (st == "not_installed" || (bool)current["enabled"]!);
        var paths = PuertsMapPaths(game);
        await transaction.CaptureAsync(paths.Files.SelectMany(f => new[] { f.Path, f.DisabledPath }));
        if (!dryRun)
        {
            Directory.CreateDirectory(paths.ModsDir);
            foreach (var item in paths.Files)
            {
                var src = Path.Combine(source.PackageRoot, item.Rel.Replace('/', Path.DirectorySeparatorChar));
                var destination = enabled ? item.Path : item.DisabledPath;
                await RuntimePackageBuilder.CopyFileAsync(src, destination);
                File.Delete(enabled ? item.DisabledPath : item.Path);
            }
            if (CheckScriptPakGroup(paths.Files.Select(f => (enabled ? f.Path : f.DisabledPath, f.Rel)).ToList(), source.PakFiles) != "match")
                FileSafety.Fail("install_verify_failed", ("id", mod.Id));
        }

        state.Mods[mod.Id] = new ModStateRecord
        {
            Enabled = enabled,
            Backend = "puerts",
            Version = source.Manifest["version"]?.GetValue<string>(),
            BuildId = source.Manifest["buildId"]?.GetValue<string>(),
            Digest = source.Digest,
            Source = Path.GetRelativePath(root, source.PackageRoot),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            Files = source.PakFiles,
        };

        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["action"] = "installed",
            ["enabled"] = enabled,
            ["dryRun"] = dryRun,
            ["digest"] = source.Digest,
            ["target"] = enabled ? paths.ModsDir : paths.DisabledDir,
        };
    }

    private static async Task<Dictionary<string, object?>> TogglePuertsMap(string root, string game, CatalogMod mod, ManagerState state, bool enable, bool dryRun, ModFileTransaction transaction)
    {
        var current = InspectPuertsMap(root, game, mod, state);
        var st = current["status"] as string;
        if (st == "not_installed")
        {
            if (!enable) FileSafety.Fail("not_installed", ("id", mod.Id));
            return await InstallPuertsMap(root, game, mod, state, dryRun, transaction, enable);
        }
        if (st is "conflict" or "unmanaged" or "modified" || (st == "incompatible_build" && enable))
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", st));
        if (enable && st == "outdated")
            return await InstallPuertsMap(root, game, mod, state, dryRun, transaction, enable);
        if ((bool)current["enabled"]! == enable)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = mod.Id,
                ["action"] = "noop",
                ["enabled"] = enable,
                ["dryRun"] = dryRun,
                ["target"] = enable ? current["activePath"] : current["disabledPath"],
            };
        }

        state.Mods.TryGetValue(mod.Id, out var record);
        (string PackageRoot, JsonObject Manifest, List<FileDigestEntry> PakFiles, string Digest)? source = null;
        if (record == null) source = ReadPuertsMapSource(root, mod);
        var expectedFiles = record?.Files ?? source!.Value.PakFiles;
        var paths = PuertsMapPaths(game);

        await transaction.CaptureAsync(paths.Files.SelectMany(f => new[] { f.Path, f.DisabledPath }));
        if (!dryRun)
        {
            foreach (var item in paths.Files)
            {
                var from = enable ? item.DisabledPath : item.Path;
                var to = enable ? item.Path : item.DisabledPath;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to, true);
            }
            if (enable)
            {
                if (CheckScriptPakGroup(paths.Files.Select(f => (f.Path, f.Rel)).ToList(), expectedFiles) != "match")
                    FileSafety.Fail("install_verify_failed", ("id", mod.Id));
            }
            else
            {
                if (CheckScriptPakGroup(paths.Files.Select(f => (f.DisabledPath, f.Rel)).ToList(), expectedFiles) != "match")
                    FileSafety.Fail("install_verify_failed", ("id", mod.Id));
            }
        }

        state.Mods[mod.Id] = new ModStateRecord
        {
            Enabled = enable,
            Backend = "puerts",
            Version = record?.Version ?? source?.Manifest["version"]?.GetValue<string>(),
            BuildId = record?.BuildId ?? source?.Manifest["buildId"]?.GetValue<string>(),
            Digest = record?.Digest ?? source?.Digest,
            Source = Path.GetRelativePath(root, Path.Combine(ResolveModProjectRoot(root, mod), "dist", "puerts")),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            Files = expectedFiles,
        };

        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["action"] = enable ? "enabled" : "disabled",
            ["enabled"] = enable,
            ["dryRun"] = dryRun,
            ["target"] = enable ? paths.ModsDir : paths.DisabledDir,
        };
    }

    private static async Task<Dictionary<string, object?>> RestorePuertsMap(string root, string game, CatalogMod mod, ManagerState state, bool dryRun, ModFileTransaction transaction)
    {
        var current = InspectPuertsMap(root, game, mod, state);
        var st = current["status"] as string;
        if (st is "conflict" or "unmanaged" or "modified")
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", st));
        var paths = PuertsMapPaths(game);
        await transaction.CaptureAsync(paths.Files.SelectMany(f => new[] { f.Path, f.DisabledPath }));
        if (!dryRun)
        {
            foreach (var item in paths.Files)
            {
                FileSafety.RemoveTreeIfExists(item.Path, new[] { game });
                FileSafety.RemoveTreeIfExists(item.DisabledPath, new[] { game });
            }
        }
        state.Mods.Remove(mod.Id);
        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["action"] = st == "not_installed" ? "noop" : "restored",
            ["enabled"] = false,
            ["dryRun"] = dryRun,
            ["target"] = paths.ModsDir,
        };
    }

    // ==================== NativeBundle Backend (Arena) ====================

    private static List<string> ExpectedNativeBundlePaths(CatalogMod mod, bool loaderBundle = false)
    {
        var list = new List<string>
        {
            $"platform/steam/ue4ss/Mods/{mod.Folder}/dlls/main.dll",
            $"platform/steam/ue4ss/Mods/{mod.Folder}/enabled.txt",
        };
        foreach (var ext in PakExtensions)
            list.Add($"ProjectPBZ/Content/Paks/~mods/{mod.PakBase}.{ext}");
        if (loaderBundle)
            list.AddRange(ArenaLoaderPackage.Paths.Select(path => "platform/steam/" + path));
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static (string PackageRoot, JsonObject Manifest, List<FileDigestEntry> Files, string Digest)
        ReadNativeBundleSource(string root, CatalogMod mod)
    {
        var modRoot = ResolveModProjectRoot(root, mod);
        var packageRoot = FileSafety.AllowedPath(Path.Combine(modRoot, "dist", "native"), new[] { modRoot }, $"{mod.Id} native package");
        var payloadRoot = Path.Combine(packageRoot, "payload");
        var manifestPath = Path.Combine(packageRoot, "manifest.json");
        if (FileSafety.GetNodeInfoIfExists(manifestPath, "native manifest") is not FileInfo)
            FileSafety.Fail("missing_source", ("target", manifestPath));
        FileSafety.AssertRealTree(packageRoot, modRoot, "native package");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8))?.AsObject()
            ?? throw new ManagerException("invalid_data", new() { ["target"] = manifestPath });
        if (manifest["id"]?.GetValue<string>() != mod.Folder)
            FileSafety.Fail("invalid_data", ("target", manifestPath));

        var expectedPaths = ExpectedNativeBundlePaths(mod, manifest["loaderBundle"]?.GetValue<bool>() == true);
        var files = new List<FileDigestEntry>();
        foreach (var node in manifest["files"]?.AsArray() ?? new JsonArray())
        {
            files.Add(new FileDigestEntry(
                node?["path"]?.GetValue<string>() ?? "",
                node?["bytes"]?.GetValue<long>() ?? -1,
                node?["sha256"]?.GetValue<string>() ?? ""));
        }
        if (!files.Select(f => f.Path).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(expectedPaths))
            FileSafety.Fail("invalid_data", ("target", manifestPath));

        foreach (var entry in files)
        {
            var full = FileSafety.AllowedPath(Path.Combine(payloadRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)), new[] { payloadRoot }, entry.Path);
            if (FileSafety.GetNodeInfoIfExists(full, entry.Path) is not FileInfo fi || fi.Length != entry.Bytes || FileSafety.FileHash(full) != entry.Sha256)
                FileSafety.Fail("source_corrupted", ("target", full));
        }

        var digestLines = string.Join("\n", files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}\t{f.Bytes}\t{f.Sha256}"));
        return (packageRoot, manifest, files, FileSafety.StringHash(digestLines));
    }

    private static (string ActiveDir, string DisabledDir, List<(string Rel, string Path, string DisabledPath)> Files)
        NativeBundlePaths(string game, CatalogMod mod, List<FileDigestEntry>? expected = null)
    {
        var activeDir = FileSafety.AllowedPath(Path.Combine(game, "platform", "steam", "ue4ss", "Mods", mod.Folder), new[] { game }, "active native mod");
        var disabledDir = FileSafety.AllowedPath(Path.Combine(game, "platform", "steam", ".pbz-mod-manager", "native", mod.Folder, "disabled"), new[] { game }, "disabled native mod");
        var allowed = ExpectedNativeBundlePaths(mod, true);
        var relatives = expected?.Select(f => f.Path).ToList() ?? ExpectedNativeBundlePaths(mod);
        if (relatives.Distinct(StringComparer.Ordinal).Count() != relatives.Count || relatives.Any(p => !allowed.Contains(p)))
            FileSafety.Fail("invalid_data", ("target", mod.Id));
        var files = relatives.Select(rel =>
        {
            var sysRel = rel.Replace('/', Path.DirectorySeparatorChar);
            return (rel, Path.Combine(game, sysRel), Path.Combine(disabledDir, sysRel));
        }).ToList();
        return (activeDir, disabledDir, files);
    }

    private static Dictionary<string, object?> InspectNativeBundle(string root, string game, CatalogMod mod, ManagerState state)
    {
        state.Mods.TryGetValue(mod.Id, out var record);
        (string PackageRoot, JsonObject Manifest, List<FileDigestEntry> Files, string Digest)? source = null;
        string? sourceError = null;
        try { source = ReadNativeBundleSource(root, mod); }
        catch (Exception ex) { sourceError = ex.Message; }

        var expectedFiles = record?.Files ?? source?.Files;
        var paths = NativeBundlePaths(game, mod, expectedFiles);
        FileSafety.AssertRealParentChain(paths.ActiveDir, game, "active native mod");
        FileSafety.AssertRealParentChain(paths.DisabledDir, game, "disabled native mod");
        foreach (var item in paths.Files)
        {
            FileSafety.AssertRealParentChain(item.Path, game);
            FileSafety.AssertRealParentChain(item.DisabledPath, game);
        }
        var active = CheckScriptPakGroup(paths.Files.Select(f => (f.Path, f.Rel)).ToList(), expectedFiles);
        var parked = CheckScriptPakGroup(paths.Files.Select(f => (f.DisabledPath, f.Rel)).ToList(), expectedFiles);
        var gameBuild = ReadInstalledGameBuildId(game);
        var targetBuild = source?.Manifest["gameBuild"]?.GetValue<string>() ?? record?.BuildId;
        var buildCompatible = string.IsNullOrEmpty(targetBuild) || gameBuild == targetBuild;

        bool outdated = record != null && source != null && (record.Digest != source.Value.Digest ||
            record.Version != source.Value.Manifest["version"]?.GetValue<string>());
        string status;
        bool enabled = false;
        if (!buildCompatible)
        {
            status = "incompatible_build";
            enabled = active is "match" or "modified";
        }
        else if (active != "absent" && parked != "absent") status = "conflict";
        else if (active == "match")
        {
            enabled = true;
            status = outdated ? "outdated" : "enabled";
        }
        else if (parked == "match")
        {
            status = outdated ? "outdated" : "disabled";
        }
        else if (active == "modified" && parked == "absent" && record != null)
        {
            enabled = true;
            status = "modified";
        }
        else if (parked == "modified" && active == "absent" && record != null)
        {
            status = "modified";
        }
        else if (active == "absent" && parked == "absent") status = "not_installed";
        else if (active == "unmanaged" || parked == "unmanaged") { status = "unmanaged"; enabled = active == "unmanaged"; }
        else { status = "modified"; enabled = active != "absent"; }

        var actionable = (buildCompatible &&
            status is not ("conflict" or "unmanaged" or "modified" or "incompatible_build") &&
            (status != "not_installed" || source != null)) ||
            (!buildCompatible && enabled);

        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["category"] = mod.Category,
            ["folder"] = mod.Folder,
            ["project"] = mod.Project,
            ["backend"] = "native",
            ["status"] = status,
            ["enabled"] = enabled,
            ["actionable"] = actionable,
            ["configurable"] = false,
            ["recorded"] = record != null,
            ["sourceAvailable"] = source != null,
            ["sourceError"] = sourceError,
            ["sourceDigest"] = source?.Digest,
            ["installedDigest"] = record?.Digest,
            ["version"] = record?.Version ?? source?.Manifest["version"]?.GetValue<string>(),
            ["installedVersion"] = status == "not_installed" ? null : record?.Version ?? (active == "match" || parked == "match" ? source?.Manifest["version"]?.GetValue<string>() : null),
            ["sourceVersion"] = source?.Manifest["version"]?.GetValue<string>(),
            ["buildId"] = targetBuild,
            ["gameBuildId"] = gameBuild,
            ["activePath"] = paths.ActiveDir,
            ["disabledPath"] = paths.DisabledDir,
        };
    }

    private static async Task<Dictionary<string, object?>> InstallNativeBundle(string root, string game, CatalogMod mod, ManagerState state, bool dryRun, ModFileTransaction transaction, bool? requestedEnabled = null)
    {
        var current = InspectNativeBundle(root, game, mod, state);
        var st = current["status"] as string;
        if (st is "conflict" or "unmanaged" or "modified" or "incompatible_build")
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", st));
        var source = ReadNativeBundleSource(root, mod);
        if (ReadInstalledGameBuildId(game) != source.Manifest["gameBuild"]?.GetValue<string>())
            FileSafety.Fail("incompatible_build", ("id", mod.Id), ("expected", source.Manifest["gameBuild"]?.GetValue<string>()), ("actual", ReadInstalledGameBuildId(game)));

        var keepDisabled = !(requestedEnabled ?? (st == "not_installed" || (bool)current["enabled"]!));
        var paths = NativeBundlePaths(game, mod, source.Files);
        state.Mods.TryGetValue(mod.Id, out var previous);
        foreach (var item in paths.Files)
        {
            FileSafety.AssertRealParentChain(item.Path, game);
            FileSafety.AssertRealParentChain(item.DisabledPath, game);
            if (previous?.Files?.Any(f => f.Path == item.Rel) == true) continue;
            if (FileSafety.GetNodeInfoIfExists(item.Path) != null || FileSafety.GetNodeInfoIfExists(item.DisabledPath) != null)
                FileSafety.Fail("target_modified", ("target", item.Path));
        }
        await transaction.CaptureAsync(paths.Files.SelectMany(f => new[] { f.Path, f.DisabledPath }));
        if (!dryRun)
        {
            foreach (var item in paths.Files)
            {
                var src = Path.Combine(source.PackageRoot, "payload", item.Rel.Replace('/', Path.DirectorySeparatorChar));
                var dst = keepDisabled ? item.DisabledPath : item.Path;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                await RuntimePackageBuilder.CopyFileAsync(src, dst);
                var previousPath = keepDisabled ? item.Path : item.DisabledPath;
                if (FileSafety.GetNodeInfoIfExists(previousPath) is FileInfo) File.Delete(previousPath);
            }
            var verifyTargets = paths.Files.Select(f => (keepDisabled ? f.DisabledPath : f.Path, f.Rel)).ToList();
            if (CheckScriptPakGroup(verifyTargets, source.Files) != "match")
                FileSafety.Fail("install_verify_failed", ("id", mod.Id));
        }

        state.Mods[mod.Id] = new ModStateRecord
        {
            Enabled = !keepDisabled,
            Backend = "native",
            Version = source.Manifest["version"]?.GetValue<string>(),
            BuildId = source.Manifest["gameBuild"]?.GetValue<string>(),
            Digest = source.Digest,
            Source = Path.GetRelativePath(root, source.PackageRoot),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            Files = source.Files,
        };

        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["action"] = "installed",
            ["enabled"] = !keepDisabled,
            ["dryRun"] = dryRun,
            ["digest"] = source.Digest,
            ["target"] = keepDisabled ? paths.DisabledDir : paths.ActiveDir,
        };
    }

    private static async Task<Dictionary<string, object?>> ToggleNativeBundle(string root, string game, CatalogMod mod, ManagerState state, bool enable, bool dryRun, ModFileTransaction transaction)
    {
        var current = InspectNativeBundle(root, game, mod, state);
        var st = current["status"] as string;
        if (st == "not_installed")
        {
            if (!enable) FileSafety.Fail("not_installed", ("id", mod.Id));
            return await InstallNativeBundle(root, game, mod, state, dryRun, transaction, enable);
        }
        if (st is "conflict" or "unmanaged" or "modified" || (st == "incompatible_build" && enable))
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", st));
        if (enable && st == "outdated")
            return await InstallNativeBundle(root, game, mod, state, dryRun, transaction, enable);
        if ((bool)current["enabled"]! == enable)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = mod.Id,
                ["action"] = "noop",
                ["enabled"] = enable,
                ["dryRun"] = dryRun,
                ["target"] = enable ? current["activePath"] : current["disabledPath"],
            };
        }

        state.Mods.TryGetValue(mod.Id, out var record);
        (string PackageRoot, JsonObject Manifest, List<FileDigestEntry> Files, string Digest)? source = null;
        if (record == null) source = ReadNativeBundleSource(root, mod);
        var expectedFiles = record?.Files ?? source!.Value.Files;
        var paths = NativeBundlePaths(game, mod, expectedFiles);

        await transaction.CaptureAsync(paths.Files.SelectMany(f => new[] { f.Path, f.DisabledPath }));
        if (!dryRun)
        {
            foreach (var item in paths.Files)
            {
                var from = enable ? item.DisabledPath : item.Path;
                var to = enable ? item.Path : item.DisabledPath;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to, true);
            }
        }

        state.Mods[mod.Id] = new ModStateRecord
        {
            Enabled = enable,
            Backend = "native",
            Version = record?.Version ?? source?.Manifest["version"]?.GetValue<string>(),
            BuildId = record?.BuildId ?? source?.Manifest["gameBuild"]?.GetValue<string>(),
            Digest = record?.Digest ?? source?.Digest,
            Source = Path.GetRelativePath(root, Path.Combine(ResolveModProjectRoot(root, mod), "dist", "native")),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            Files = expectedFiles,
        };

        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["action"] = enable ? "enabled" : "disabled",
            ["enabled"] = enable,
            ["dryRun"] = dryRun,
            ["target"] = enable ? paths.ActiveDir : paths.DisabledDir,
        };
    }

    private static async Task<Dictionary<string, object?>> RestoreNativeBundle(string root, string game, CatalogMod mod, ManagerState state, bool dryRun, ModFileTransaction transaction)
    {
        var current = InspectNativeBundle(root, game, mod, state);
        var st = current["status"] as string;
        if (st is "conflict" or "unmanaged" or "modified")
            FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", st));
        state.Mods.TryGetValue(mod.Id, out var record);
        var expectedFiles = record?.Files;
        if (expectedFiles == null && st != "not_installed")
            expectedFiles = ReadNativeBundleSource(root, mod).Files;
        var paths = NativeBundlePaths(game, mod, expectedFiles);
        await transaction.CaptureAsync(paths.Files.SelectMany(f => new[] { f.Path, f.DisabledPath }));
        if (!dryRun)
        {
            foreach (var item in paths.Files)
            {
                FileSafety.RemoveTreeIfExists(item.Path, new[] { game });
                FileSafety.RemoveTreeIfExists(item.DisabledPath, new[] { game });
            }
        }
        state.Mods.Remove(mod.Id);
        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["action"] = st == "not_installed" ? "noop" : "restored",
            ["enabled"] = false,
            ["dryRun"] = dryRun,
            ["target"] = paths.ActiveDir,
        };
    }

    // ==================== Unified Mod Dispatch ====================

    public static async Task<Dictionary<string, object?>> InspectModAsync(string root, string game, CatalogMod mod, ManagerState state, List<CatalogMod> catalog)
    {
        if (mod.Backend == "nativeRuntime") return await RuntimeModBackend.InspectAsync(root, game, mod, state);
        if (mod.Backend == "nativeBundle") return InspectNativeBundle(root, game, mod, state);
        if (mod.Backend == "puerts") return InspectPuertsMap(root, game, mod, state);
        if (mod.Backend == "scriptPak") return InspectScriptPak(root, game, mod, state, catalog);
        return new Dictionary<string, object?>
        {
            ["id"] = mod.Id,
            ["category"] = mod.Category,
            ["folder"] = mod.Folder,
            ["project"] = mod.Project,
            ["backend"] = "retired",
            ["status"] = "retired",
            ["enabled"] = false,
            ["actionable"] = false,
            ["configurable"] = false,
            ["recorded"] = false,
            ["sourceAvailable"] = false,
            ["activePath"] = "",
            ["disabledPath"] = "",
        };
    }

    private static List<CatalogMod> SelectMods(List<CatalogMod> catalog, string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, "all", StringComparison.OrdinalIgnoreCase))
            return catalog.Where(m => m.Backend != "retired").ToList();
        var match = catalog.FirstOrDefault(m =>
            string.Equals(m.Id, target, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Folder, target, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Project, target, StringComparison.OrdinalIgnoreCase));
        if (match == null) FileSafety.Fail("unknown_mod", ("id", target));
        if (match!.Backend == "retired") FileSafety.Fail("retired_mod", ("id", match.Id));
        return new List<CatalogMod> { match };
    }

    public static async Task<Dictionary<string, object?>> ExecuteAsync(
        string root,
        string command,
        string? target = null,
        Dictionary<string, string>? options = null,
        string? explicitGame = null,
        bool dryRun = false)
    {
        var catalog = LoadCatalog(root);
        if (command == "set-game")
        {
            var chosen = SaveConfiguredGameDir(root, target ?? explicitGame, dryRun);
            return new Dictionary<string, object?>
            {
                ["game"] = chosen,
                ["gameBuildId"] = ReadInstalledGameBuildId(chosen),
                ["dryRun"] = dryRun,
            };
        }

        var game = DetectGameDir(root, explicitGame);

        if (command == "build")
        {
            return await ModPackageBuilder.BuildAsync(root, game, target ?? "all");
        }

        var running = FileSafety.IsGameRunning();

        if (command == "status")
        {
            // Finish an interrupted transaction before comparing packages. Ordinary
            // refreshes only inspect files; recovery requires the game to be closed.
            var interrupted = new ModFileTransaction(root, game, dryRun);
            if (!dryRun && !running && interrupted.HasPendingRecovery)
                await FileSafety.WithOperationLockAsync(LockPathFor(game), new[] { game }, async () =>
                {
                    if (FileSafety.IsGameRunning()) FileSafety.Fail("game_running");
                    await interrupted.RecoverAsync();
                    return true;
                });
            var state = await ReadStateAsync(root, game);
            var inspected = new List<Dictionary<string, object?>>();
            foreach (var mod in catalog) inspected.Add(await InspectModAsync(root, game, mod, state, catalog));
            return new Dictionary<string, object?>
            {
                ["game"] = game,
                ["gameBuildId"] = ReadInstalledGameBuildId(game),
                ["running"] = running,
                ["stateFile"] = StatePathFor(game),
                ["mods"] = inspected,
            };
        }

        if (command == "record")
        {
            if (FileSafety.IsGameRunning()) FileSafety.Fail("game_running");
            return await FileSafety.WithOperationLockAsync(LockPathFor(game), new[] { game }, async () =>
            {
                var transaction = new ModFileTransaction(root, game, dryRun);
                await transaction.RecoverAsync();
                var state = await ReadStateAsync(root, game);
                foreach (var mod in catalog)
                {
                    var info = await InspectModAsync(root, game, mod, state, catalog);
                    var st = info["status"] as string;
                    if (st is "enabled" or "disabled" && !state.Mods.ContainsKey(mod.Id))
                    {
                        state.Mods[mod.Id] = new ModStateRecord
                        {
                            Enabled = st == "enabled",
                            Backend = info["backend"] as string,
                            Version = info["version"] as string,
                            BuildId = info["buildId"] as string,
                            Digest = info["sourceDigest"] as string,
                            UpdatedAt = DateTime.UtcNow.ToString("O"),
                        };
                    }
                }
                await WriteStateAsync(game, state, dryRun);
                return new Dictionary<string, object?>
                {
                    ["game"] = game,
                    ["stateFile"] = StatePathFor(game),
                    ["state"] = state,
                };
            });
        }

        if (command == "backups")
        {
            return new Dictionary<string, object?>
            {
                ["game"] = game,
                ["backupRoot"] = SaveBackupService.BackupRootFor(game),
                ["backups"] = SaveBackupService.ListSaveBackups(game),
            };
        }

        if (command == "restore-save")
        {
            if (FileSafety.IsGameRunning()) FileSafety.Fail("game_running");
            return await FileSafety.WithOperationLockAsync(LockPathFor(game), new[] { game }, () =>
            {
                var res = SaveBackupService.RestoreSaveBackup(game, target, dryRun);
                return Task.FromResult(new Dictionary<string, object?>
                {
                    ["game"] = game,
                    ["dryRun"] = dryRun,
                    ["results"] = new List<Dictionary<string, object?>> { res },
                });
            });
        }

        if (command == "controls")
        {
            var patch = new Dictionary<string, string>(StringComparer.Ordinal);
            if (options != null && options.TryGetValue("bindings", out var bindingsJson) && !string.IsNullOrWhiteSpace(bindingsJson))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(bindingsJson);
                if (parsed != null) foreach (var (k, v) in parsed) patch[k] = v;
            }
            if (options != null)
            {
                foreach (var (k, v) in options)
                {
                    if (k != "bindings") patch[k] = v;
                }
            }
            if (patch.Count == 0)
            {
                return ControlsService.ReadControls(root, await ReadStateAsync(root, game));
            }
            if (FileSafety.IsGameRunning()) FileSafety.Fail("game_running");
            return await FileSafety.WithOperationLockAsync(LockPathFor(game), new[] { game }, async () =>
            {
                var state = await ReadStateAsync(root, game);
                return ControlsService.WriteControls(root, patch, state, dryRun);
            });
        }

        if (command is "map-settings" or "config")
        {
            var patch = options ?? new Dictionary<string, string>(StringComparer.Ordinal);
            if (patch.Count == 0)
            {
                return MapSettingsService.ReadMapSettings();
            }
            if (FileSafety.IsGameRunning()) FileSafety.Fail("game_running");
            return await FileSafety.WithOperationLockAsync(LockPathFor(game), new[] { game }, () =>
                Task.FromResult(MapSettingsService.WriteMapSettings(patch, dryRun)));
        }

        if (command is "install" or "on" or "off" or "restore")
        {
            if (FileSafety.IsGameRunning()) FileSafety.Fail("game_running");
            var selected = SelectMods(catalog, target);
            return await FileSafety.WithOperationLockAsync(LockPathFor(game), new[] { game }, async () =>
            {
                var transaction = new ModFileTransaction(root, game, dryRun);
                await transaction.RecoverAsync();
                var state = await ReadStateAsync(root, game);
                try
                {
                    await transaction.CaptureAsync(new[] { StatePathFor(game) });
                    if (!dryRun && command is "install" or "on")
                        await MigrateLegacyLuaIfNeeded(root, game, catalog, state, transaction);
                    var results = new List<Dictionary<string, object?>>();
                    foreach (var mod in selected)
                    {
                        Dictionary<string, object?> res;
                        if (mod.Backend == "nativeRuntime")
                        {
                            res = await RuntimeModBackend.ApplyAsync(root, game, mod, state, command, dryRun, transaction);
                        }
                        else if (mod.Backend == "nativeBundle")
                        {
                            res = command == "install" ? await InstallNativeBundle(root, game, mod, state, dryRun, transaction)
                                : command == "on" ? await ToggleNativeBundle(root, game, mod, state, true, dryRun, transaction)
                                : command == "off" ? await ToggleNativeBundle(root, game, mod, state, false, dryRun, transaction)
                                : await RestoreNativeBundle(root, game, mod, state, dryRun, transaction);
                        }
                        else if (mod.Backend == "puerts")
                        {
                            res = command == "install" ? await InstallPuertsMap(root, game, mod, state, dryRun, transaction)
                                : command == "on" ? await TogglePuertsMap(root, game, mod, state, true, dryRun, transaction)
                                : command == "off" ? await TogglePuertsMap(root, game, mod, state, false, dryRun, transaction)
                                : await RestorePuertsMap(root, game, mod, state, dryRun, transaction);
                        }
                        else
                        {
                            res = command == "install" ? await InstallScriptPak(root, game, mod, state, dryRun, transaction)
                                : command == "on" ? await ToggleScriptPak(root, game, mod, state, true, dryRun, transaction)
                                : command == "off" ? await ToggleScriptPak(root, game, mod, state, false, dryRun, transaction)
                                : await RestoreScriptPak(root, game, mod, state, dryRun, transaction);
                        }
                        results.Add(res);
                    }
                    await WriteStateAsync(game, state, dryRun);
                    transaction.Commit();
                    return new Dictionary<string, object?>
                    {
                        ["game"] = game,
                        ["dryRun"] = dryRun,
                        ["results"] = results,
                    };
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
        }

        FileSafety.Fail("unknown_command", ("command", command));
        return null!;
    }

    public static async Task<int> RunCliAsync(string root, string[] args)
    {
        var locales = LoadLocales(root);
        var locale = "zh-CN";
        string? explicitGame = null;
        bool json = false;
        bool dryRun = false;
        string? bindingsJson = null;
        var sets = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json") json = true;
            else if (arg == "--dry-run") dryRun = true;
            else if (arg == "--game" && i + 1 < args.Length) explicitGame = args[++i];
            else if (arg == "--locale" && i + 1 < args.Length) locale = args[++i];
            else if (arg == "--bindings" && i + 1 < args.Length) bindingsJson = args[++i];
            else if (arg == "--set" && i + 1 < args.Length)
            {
                var item = args[++i];
                var eq = item.IndexOf('=');
                if (eq <= 0) FileSafety.Fail("invalid_assignment", ("item", item));
                sets[item[..eq].Trim()] = item[(eq + 1)..].Trim();
            }
            else positionals.Add(arg);
        }

        var table = locales.TryGetValue(locale, out var loc) ? loc : locales["zh-CN"];
        string T(string key) => table.TryGetValue(key, out var v) ? v : key;

        var command = positionals.Count > 0 ? positionals[0] : "status";
        var target = positionals.Count > 1 ? positionals[1] : null;
        if (!string.IsNullOrEmpty(bindingsJson)) sets["bindings"] = bindingsJson!;

        try
        {
            var output = await ExecuteAsync(root, command, target, sets, explicitGame, dryRun);
            if (json || command is "record" or "backups" or "controls" or "map-settings" or "config" or "build")
            {
                Console.WriteLine(JsonSerializer.Serialize(output, FileSafety.JsonOptions));
            }
            else if (command == "status")
            {
                Console.WriteLine($"{T("gui_gameDirectory")}: {output["game"]}");
                Console.WriteLine((bool)output["running"]! ? T("running") : T("stopped"));
                var mods = (List<Dictionary<string, object?>>)output["mods"]!;
                foreach (var item in mods)
                {
                    var id = (string)item["id"]!;
                    var cat = item.TryGetValue("category", out var cObj) && cObj is string c ? c : "feature";
                    var st = (string)item["status"]!;
                    var stText = st switch
                    {
                        "enabled" => T("enabled"),
                        "disabled" => T("disabled"),
                        "not_installed" => T("absent"),
                        "outdated" => T("different"),
                        "incompatible_build" => T("unsupported_build"),
                        "retired" => T("gui_retired"),
                        _ => T("conflict"),
                    };
                    Console.WriteLine($"{id.PadRight(18)} {T("category_" + cat).PadRight(6)} {T(id).PadRight(16)} {stText}");
                }
            }
            else if (output.TryGetValue("results", out var resObj) && resObj is List<Dictionary<string, object?>> results)
            {
                foreach (var r in results)
                    Console.WriteLine($"{r["id"]}: {r["action"]} -> {r["target"]}");
            }
            return 0;
        }
        catch (ManagerException mex)
        {
            var msg = T(mex.Code);
            foreach (var (k, v) in mex.Params) msg = msg.Replace($"{{{k}}}", Convert.ToString(v) ?? "");
            if (json)
                Console.Error.WriteLine(JsonSerializer.Serialize(new { error = mex.Code, message = msg, mex.Params }, FileSafety.JsonOptions));
            else
                Console.Error.WriteLine(msg);
            return 1;
        }
        catch (Exception ex)
        {
            if (json)
                Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "unexpected", message = ex.Message }, FileSafety.JsonOptions));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
