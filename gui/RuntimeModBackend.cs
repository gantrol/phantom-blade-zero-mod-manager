using System.Text;
using System.Text.Json;

namespace PhantomBladeModManager;

internal static class RuntimeModBackend
{
    private const string Backend = "nativeRuntime";
    private const string SharedId = "__nativeRuntime";
    private sealed record Package(string Root, string Version, Version RuntimeVersion, bool RuntimeVerified, string Digest, List<FileDigestEntry> Files);
    private static string Platform(string game) => Path.Combine(game, "platform", "steam");
    private static string Store(string game, CatalogMod mod) => Path.Combine(Platform(game), ".pbz-mod-manager", "runtime", mod.Id);
    private static string Scripts(string game) => Path.Combine(game, "ProjectPBZ", "Content", "JavaScript");
    private static string SourceRoot(string root, CatalogMod mod) => Path.Combine(ManagerEngine.ResolveModProjectRoot(root, mod), "dist", "runtime");

    private static string Destination(string game, CatalogMod mod, string relative, bool enabled)
    {
        string target;
        if (relative.StartsWith($"Scripts/{mod.Folder}/", StringComparison.Ordinal) && relative.EndsWith(".js", StringComparison.Ordinal))
            target = Path.Combine(Scripts(game), relative[8..]);
        else if (new[] { "pak", "utoc", "ucas" }.Any(ext => relative == $"Paks/{mod.PakBase}.{ext}"))
            target = Path.Combine(game, "ProjectPBZ", "Content", "Paks", "~mods", relative[5..]);
        else throw new ManagerException("invalid_data", new() { ["target"] = relative });
        if (!enabled) target = Path.Combine(Store(game, mod), "disabled", relative);
        target = FileSafety.AllowedPath(target, new[] { game });
        FileSafety.AssertRealParentChain(target, game);
        return target;
    }

    private static async Task<Package> ReadPackageAsync(string root, CatalogMod mod)
    {
        var directory = SourceRoot(root, mod);
        FileSafety.AssertRealTree(directory, root);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")));
        var json = doc.RootElement;
        if (json.GetProperty("id").GetString() != mod.Id || json.GetProperty("backend").GetString() != Backend
            || json.GetProperty("buildId").GetString() != RuntimePackageBuilder.BuildId)
            FileSafety.Fail("source_corrupted", ("target", directory));
        var files = json.GetProperty("files").Deserialize<List<FileDigestEntry>>(FileSafety.JsonOptions)!;
        var runtimeVersion = json.TryGetProperty("runtimeVersion", out var runtime) ? Version.Parse(runtime.GetString()!) : new Version(1, 0, 0);
        RuntimePackageBuilder.ValidateScriptBudget(files.Where(f => f.Path.EndsWith(".js", StringComparison.Ordinal))
            .Select(f => (f.Path, f.Bytes)), runtimeVersion);
        foreach (var item in files)
        {
            var path = FileSafety.AllowedPath(Path.Combine(directory, item.Path), new[] { directory });
            if (!await MatchesAsync(path, item)) FileSafety.Fail("source_corrupted", ("target", path));
        }
        if (!files.Any(f => f.Path == $"Scripts/{mod.Folder}/{(mod.Id == "map" ? "Entry" : "Runtime")}.js")
            || !files.Any(f => f.Path == "Runtime/version.dll") || !files.Any(f => f.Path == "Runtime/Bootstrap.js"))
            FileSafety.Fail("source_corrupted", ("target", directory));
        var digest = FileSafety.StringHash(string.Join("\n", files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}\t{f.Bytes}\t{f.Sha256}")));
        var verified = json.TryGetProperty("runtimeVerified", out var verification) && verification.GetBoolean();
        return new(directory, json.GetProperty("version").GetString()!, runtimeVersion, verified, digest, files);
    }

    private static async Task<bool> MatchesAsync(string path, FileDigestEntry item) =>
        FileSafety.GetNodeInfoIfExists(path) is FileInfo fi && fi.Length == item.Bytes
        && await RuntimePackageBuilder.HashAsync(path) == item.Sha256;

    private static async Task CheckInstalledAsync(string game, CatalogMod mod, ModStateRecord record)
    {
        foreach (var file in record.Files ?? [])
        {
            var path = Destination(game, mod, file.Path, record.Enabled);
            if (!await MatchesAsync(path, file)) FileSafety.Fail("target_modified", ("target", path));
            if (File.Exists(Destination(game, mod, file.Path, !record.Enabled)))
                FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", "conflict"));
        }
    }

    private static void CheckEnabledScriptBudget(string game, CatalogMod mod, ModStateRecord? previous, Package? package, ManagerState state)
    {
        var inventory = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PhantomBladeRuntime", "PhantomBladeSprint", "PhantomBladeMap", "PhantomBladeInfiniteTalisman", "PhantomBladeQuickActions", "PhantomBladeTargeting" })
        {
            var directory = Path.Combine(Scripts(game), name);
            if (!Directory.Exists(directory)) continue;
            FileSafety.AssertRealTree(directory, game);
            foreach (var file in Directory.EnumerateFiles(directory, "*.js", SearchOption.AllDirectories))
                inventory[Path.GetFullPath(file)] = new FileInfo(file).Length;
        }
        // Evaluate the resulting installation, replacing the old owned files.
        foreach (var file in previous?.Files ?? [])
            if (file.Path.StartsWith("Scripts/", StringComparison.Ordinal))
                inventory.Remove(Destination(game, mod, file.Path, true));
        foreach (var file in package?.Files ?? previous?.Files ?? [])
            if (file.Path.StartsWith("Scripts/", StringComparison.Ordinal))
                inventory[Destination(game, mod, file.Path, true)] = file.Bytes;
        var version = state.Mods.TryGetValue(SharedId, out var shared) && Version.TryParse(shared.Version, out var installed)
            ? installed : new Version(1, 0, 0);
        if (package != null && package.RuntimeVersion >= version)
        {
            version = package.RuntimeVersion;
            foreach (var file in package.Files.Where(f => f.Path.StartsWith("Runtime/", StringComparison.Ordinal) && f.Path.EndsWith(".js", StringComparison.Ordinal)))
                inventory[Path.Combine(Scripts(game), "PhantomBladeRuntime", file.Path[8..])] = file.Bytes;
        }
        // Reserve the generated enabled list, including a first installation.
        inventory[Path.Combine(Scripts(game), "PhantomBladeRuntime", "Enabled.js")] = 1024;
        RuntimePackageBuilder.ValidateScriptBudget(inventory.Select(f => (f.Key, f.Value)), version);
    }

    public static async Task<Dictionary<string, object?>> InspectAsync(string root, string game, CatalogMod mod, ManagerState state)
    {
        Package? source = null;
        string? sourceError = null;
        try { source = await ReadPackageAsync(root, mod); }
        catch (Exception error) { sourceError = error is ManagerException managed ? managed.Code : error.Message; }
        state.Mods.TryGetValue(mod.Id, out var record);
        if (record?.Backend != Backend) record = null;
        var status = record == null ? "not_installed" : record.Enabled ? "enabled" : "disabled";
        if (record != null)
        {
            try
            {
                await CheckInstalledAsync(game, mod, record);
                if (record.Enabled) await CheckSharedAsync(game, state);
                if (source != null && (record.Digest != source.Digest || record.Version != source.Version)) status = "outdated";
            }
            catch (ManagerException) { status = "modified"; }
        }
        var compatible = ManagerEngine.ReadInstalledGameBuildId(game) == RuntimePackageBuilder.BuildId;
        if (!compatible) status = "incompatible_build";
        return new()
        {
            ["id"] = mod.Id, ["category"] = mod.Category, ["folder"] = mod.Folder, ["project"] = mod.Project,
            ["backend"] = Backend, ["status"] = status, ["enabled"] = record?.Enabled ?? false,
            ["actionable"] = status != "modified" && (compatible ? source != null || record != null : record?.Enabled == true),
            ["configurable"] = mod.Id == "map", ["settingsKind"] = mod.Id == "map" ? "puertsMap" : null,
            ["recorded"] = record != null, ["sourceAvailable"] = source != null, ["sourceError"] = sourceError,
            ["sourceDigest"] = source?.Digest, ["installedDigest"] = record?.Digest,
            ["version"] = record?.Version ?? source?.Version,
            ["installedVersion"] = record?.Version, ["sourceVersion"] = source?.Version, ["buildId"] = RuntimePackageBuilder.BuildId,
            ["gameBuildId"] = ManagerEngine.ReadInstalledGameBuildId(game),
            ["runtimeVerified"] = source?.RuntimeVerified ?? false, ["activePath"] = Path.Combine(Scripts(game), mod.Folder),
            ["disabledPath"] = Path.Combine(Store(game, mod), "disabled")
        };
    }

    private static async Task CheckSharedAsync(string game, ManagerState state)
    {
        if (!state.Mods.TryGetValue(SharedId, out var record)) FileSafety.Fail("source_missing", ("target", SharedId));
        foreach (var item in record.Files ?? [])
        {
            var path = FileSafety.AllowedPath(Path.Combine(game, item.Path), new[] { game });
            FileSafety.AssertRealParentChain(path, game);
            if (!await MatchesAsync(path, item)) FileSafety.Fail("target_modified", ("target", path));
        }
    }

    private static async Task InstallSharedAsync(string game, Package package, ManagerState state, bool dryRun, ModFileTransaction transaction)
    {
        if (state.Mods.TryGetValue(SharedId, out var installed))
        {
            await CheckSharedAsync(game, state);
            // Older map/sprint packages can keep using the newer shared loader.
            if (Version.TryParse(installed.Version, out var installedVersion) && installedVersion > package.RuntimeVersion) return;
        }
        var entries = new List<FileDigestEntry>();
        foreach (var item in package.Files.Where(f => f.Path.StartsWith("Runtime/", StringComparison.Ordinal)))
        {
            var name = item.Path[8..];
            var relative = name switch
            {
                "version.dll" => "platform/steam/version.dll",
                "Bootstrap.js" or "Controls.js" or "Assets.js" or "Bindings.generated.js" => "ProjectPBZ/Content/JavaScript/PhantomBladeRuntime/" + name,
                "COMPATIBILITY.txt" or "MinHook-LICENSE.txt" => "platform/steam/PhantomBladeRuntime/" + name,
                _ => throw new ManagerException("invalid_data", new() { ["target"] = name })
            };
            var target = FileSafety.AllowedPath(Path.Combine(game, relative), new[] { game });
            FileSafety.AssertRealParentChain(target, game);
            var owned = state.Mods.TryGetValue(SharedId, out var shared)
                && shared.Files?.Any(f => f.Path == relative) == true;
            if (!owned && File.Exists(target) && !await MatchesAsync(target, item))
                FileSafety.Fail("target_modified", ("target", target));
            entries.Add(item with { Path = relative });
        }
        await transaction.CaptureAsync(entries.Select(item => Path.Combine(game, item.Path)));
        if (!dryRun)
        {
            foreach (var item in entries)
                await RuntimePackageBuilder.CopyFileAsync(Path.Combine(package.Root, "Runtime", Path.GetFileName(item.Path)), Path.Combine(game, item.Path));
            foreach (var item in entries)
                if (!await MatchesAsync(Path.Combine(game, item.Path), item))
                    FileSafety.Fail("install_verify_failed", ("id", SharedId));
        }
        state.Mods[SharedId] = new() { Backend = Backend, Enabled = true, Version = package.RuntimeVersion.ToString(), BuildId = RuntimePackageBuilder.BuildId, Files = entries };
    }

    private static async Task WriteEnabledAsync(string game, ManagerState state, bool dryRun, ModFileTransaction transaction)
    {
        if (dryRun || !state.Mods.TryGetValue(SharedId, out var shared)) return;
        var enabled = state.Mods.Where(p => p.Key != SharedId && p.Value.Backend == Backend && p.Value.Enabled).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        var file = FileSafety.AllowedPath(Path.Combine(Scripts(game), "PhantomBladeRuntime", "Enabled.js"), new[] { game });
        FileSafety.AssertRealParentChain(file, game);
        await transaction.CaptureAsync(new[] { file });
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(enabled) + ";\n", new UTF8Encoding(false));
        var relative = Path.GetRelativePath(game, file).Replace('\\', '/');
        shared.Files!.RemoveAll(f => f.Path == relative);
        shared.Files.Add(new(relative, new FileInfo(file).Length, await RuntimePackageBuilder.HashAsync(file)));
    }

    public static async Task<Dictionary<string, object?>> ApplyAsync(string root, string game, CatalogMod mod, ManagerState state, string command, bool dryRun, ModFileTransaction transaction)
    {
        state.Mods.TryGetValue(mod.Id, out var previous);
        var wasEnabled = previous?.Enabled ?? true;
        if (previous?.Backend != Backend) previous = null;
        if (previous != null) await CheckInstalledAsync(game, mod, previous);
        if (command is "install" or "on") RuntimePackageBuilder.RequireCurrentBuild(game);
        bool requestedOn = command == "on";
        Package? package = null;
        if (command == "install" || requestedOn && (previous == null || File.Exists(Path.Combine(SourceRoot(root, mod), "manifest.json"))))
            package = await ReadPackageAsync(root, mod);
        if (requestedOn && (previous == null || package != null && (package.Digest != previous.Digest || package.Version != previous.Version)))
            command = "install";
        if (command == "on" || command == "install" && (requestedOn || wasEnabled))
            CheckEnabledScriptBudget(game, mod, previous, package, state);
        await transaction.CaptureAsync((previous?.Files ?? []).SelectMany(f => new[]
        {
            Destination(game, mod, f.Path, true), Destination(game, mod, f.Path, false)
        }).Concat(state.Mods.TryGetValue(SharedId, out var sharedRecord)
            ? (sharedRecord.Files ?? []).Select(f => Path.Combine(game, f.Path)) : []));
        if (command == "install")
        {
            var enabled = requestedOn || wasEnabled;
            var files = package!.Files.Where(f => f.Path.StartsWith("Scripts/", StringComparison.Ordinal) || f.Path.StartsWith("Paks/", StringComparison.Ordinal)).ToList();
            foreach (var item in files)
            {
                bool owned = previous?.Files?.Any(f => f.Path == item.Path) == true;
                if (!owned && (previous != null || item.Path.StartsWith("Scripts/", StringComparison.Ordinal)))
                    foreach (var active in new[] { true, false })
                    {
                        var path = Destination(game, mod, item.Path, active);
                        if (File.Exists(path)) FileSafety.Fail("target_modified", ("target", path));
                    }
            }
            await transaction.CaptureAsync(files.SelectMany(f => new[]
            {
                Destination(game, mod, f.Path, true), Destination(game, mod, f.Path, false)
            }));
            await InstallSharedAsync(game, package, state, dryRun, transaction);
            if (!dryRun)
            {
                // Park the previous owned files so updates and legacy migration are reversible.
                var backup = FileSafety.AllowedPath(Path.Combine(Store(game, mod), "history", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")), new[] { game });
                var oldFiles = previous?.Files?.Select(f => Destination(game, mod, f.Path, previous.Enabled)).ToList() ?? [];
                foreach (var ext in new[] { "pak", "utoc", "ucas" })
                    oldFiles.Add(Destination(game, mod, $"Paks/{mod.PakBase}.{ext}", true));
                foreach (var old in oldFiles.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
                {
                    var parked = Path.Combine(backup, Path.GetRelativePath(game, old));
                    FileSafety.AssertRealParentChain(parked, game);
                    await transaction.CaptureAsync(new[] { old, parked });
                    Directory.CreateDirectory(Path.GetDirectoryName(parked)!);
                    File.Move(old, parked);
                }
                foreach (var item in files)
                    await RuntimePackageBuilder.CopyFileAsync(Path.Combine(package.Root, item.Path), Destination(game, mod, item.Path, enabled));
                foreach (var item in files)
                    if (!await MatchesAsync(Destination(game, mod, item.Path, enabled), item)) FileSafety.Fail("install_verify_failed", ("id", mod.Id));
            }
            state.Mods[mod.Id] = new()
            {
                Backend = Backend, Enabled = enabled, Version = package.Version, BuildId = RuntimePackageBuilder.BuildId,
                Digest = package.Digest, Source = Path.GetRelativePath(root, package.Root), Files = files, UpdatedAt = DateTime.UtcNow.ToString("O")
            };
        }
        else if (previous != null)
        {
            if (command == "on") await CheckSharedAsync(game, state);
            var enable = command == "on";
            if (!dryRun && (previous.Enabled != enable || command == "restore"))
            {
                foreach (var item in previous.Files ?? [])
                {
                    var from = Destination(game, mod, item.Path, previous.Enabled);
                    if (command == "restore") File.Delete(from);
                    else
                    {
                        var to = Destination(game, mod, item.Path, enable);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.Move(from, to);
                    }
                }
            }
            if (command == "restore") state.Mods.Remove(mod.Id);
            else { previous.Enabled = enable; previous.UpdatedAt = DateTime.UtcNow.ToString("O"); }
        }
        await WriteEnabledAsync(game, state, dryRun, transaction);
        if (!state.Mods.Any(p => p.Key != SharedId && p.Value.Backend == Backend) && state.Mods.ContainsKey(SharedId))
        {
            await CheckSharedAsync(game, state);
            if (!dryRun)
            {
                foreach (var file in state.Mods[SharedId].Files ?? []) File.Delete(Path.Combine(game, file.Path));
                File.Delete(Path.Combine(Scripts(game), "PhantomBladeRuntime", "Enabled.js"));
            }
            state.Mods.Remove(SharedId);
        }
        return new() { ["id"] = mod.Id, ["action"] = command, ["enabled"] = state.Mods.TryGetValue(mod.Id, out var current) && current.Enabled, ["dryRun"] = dryRun };
    }
}
