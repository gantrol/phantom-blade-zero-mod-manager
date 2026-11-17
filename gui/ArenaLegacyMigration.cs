namespace PhantomBladeModManager;

// Old bundles are accepted only as evidence of ownership for retirement.
internal static class ArenaLegacyMigration
{
    public static bool IsLegacy(CatalogMod mod, ModStateRecord? record) => mod.Id == "arena" && record?.Backend == "native";

    private static List<(FileDigestEntry File, string Active, string Disabled)> Paths(string game, CatalogMod mod, ModStateRecord record)
    {
        var allowed = ArenaLoaderPackage.Paths.Select(p => "platform/steam/" + p).Concat(new[]
        {
            $"platform/steam/ue4ss/Mods/{mod.Folder}/dlls/main.dll",
            $"platform/steam/ue4ss/Mods/{mod.Folder}/enabled.txt"
        }).Concat(new[] { "pak", "utoc", "ucas" }.Select(ext => $"ProjectPBZ/Content/Paks/~mods/{mod.PakBase}.{ext}")).ToHashSet(StringComparer.Ordinal);
        if (record.Files == null || record.Files.Count is not (5 or 12)
            || record.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != record.Files.Count
            || record.Files.Any(f => !allowed.Contains(f.Path)))
            FileSafety.Fail("invalid_data", ("target", mod.Id));
        return record.Files!.Select(file =>
        {
            var active = FileSafety.AllowedPath(Path.Combine(game, file.Path), new[] { game });
            var disabled = FileSafety.AllowedPath(Path.Combine(game, "platform", "steam", ".pbz-mod-manager", "native", mod.Folder, "disabled", file.Path), new[] { game });
            FileSafety.AssertRealParentChain(active, game);
            FileSafety.AssertRealParentChain(disabled, game);
            return (file, active, disabled);
        }).ToList();
    }

    public static async Task CheckAsync(string game, CatalogMod mod, ModStateRecord record)
    {
        foreach (var (file, active, disabled) in Paths(game, mod, record))
        {
            var source = record.Enabled ? active : disabled;
            if (FileSafety.GetNodeInfoIfExists(source) is not FileInfo info || info.Length != file.Bytes
                || await RuntimePackageBuilder.HashAsync(source) != file.Sha256)
                FileSafety.Fail("target_modified", ("target", source));
            if (FileSafety.GetNodeInfoIfExists(record.Enabled ? disabled : active) != null)
                FileSafety.Fail("conflict_state", ("id", mod.Id), ("status", "conflict"));
        }
    }

    public static async Task RetireAsync(string game, CatalogMod mod, ModStateRecord record, bool dryRun, ModFileTransaction transaction)
    {
        await CheckAsync(game, mod, record);
        var archive = FileSafety.AllowedPath(Path.Combine(game, "platform", "steam", ".pbz-mod-manager", "runtime", mod.Id,
            "history", "retired-ue4ss-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")), new[] { game });
        foreach (var (file, active, disabled) in Paths(game, mod, record))
        {
            var source = record.Enabled ? active : disabled;
            var destination = FileSafety.AllowedPath(Path.Combine(archive, file.Path), new[] { archive });
            FileSafety.AssertRealParentChain(destination, game);
            await transaction.CaptureAsync(new[] { source, destination });
            if (dryRun) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
        }
    }

    public static void RequireRetired(string game, CatalogMod mod)
    {
        foreach (var relative in new[] { "platform/steam/dwmapi.dll", "platform/steam/ue4ss/UE4SS.dll",
            $"platform/steam/ue4ss/Mods/{mod.Folder}/dlls/main.dll", $"platform/steam/ue4ss/Mods/{mod.Folder}/enabled.txt" }
            .Concat(new[] { "pak", "utoc", "ucas" }.Select(ext => $"ProjectPBZ/Content/Paks/~mods/{mod.PakBase}.{ext}")))
        {
            var path = FileSafety.AllowedPath(Path.Combine(game, relative), new[] { game });
            if (FileSafety.GetNodeInfoIfExists(path) != null)
                FileSafety.Fail("target_modified", ("target", path));
        }
    }
}
