using System.Text.Json;

namespace PhantomBladeModManager;

internal static class ArenaLoaderPackage
{
    // Separate proxy from the PuerTS version.dll. All files belong to the arena bundle.
    public static readonly string[] Paths =
    [
        "dwmapi.dll", "ue4ss/UE4SS.dll", "ue4ss/UE4SS-settings.ini",
        "ue4ss/MemberVariableLayout.ini", "ue4ss/UE4SS_Signatures/GNatives.lua",
        "ue4ss/UE4SS_Signatures/GUObjectArray.lua", "ue4ss/LICENSE"
    ];

    public static async Task<List<(string Source, string Target)>> ReadAsync(string modRoot, string buildId)
    {
        var cache = Path.Combine(modRoot, ".local", "loader");
        var lockFile = Path.Combine(modRoot, "native", "loader.lock.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(lockFile));
        if (document.RootElement.GetProperty("buildId").GetString() != buildId)
            FileSafety.Fail("incompatible_build", ("id", "arena"), ("actual", buildId));
        var files = document.RootElement.GetProperty("files").EnumerateArray().ToArray();
        if (!files.Select(f => f.GetProperty("path").GetString()).Order(StringComparer.Ordinal)
            .SequenceEqual(Paths.Order(StringComparer.Ordinal)))
            FileSafety.Fail("invalid_data", ("target", lockFile));
        var result = new List<(string Source, string Target)>();
        foreach (var file in files)
        {
            var relative = file.GetProperty("path").GetString()!;
            var path = FileSafety.AllowedPath(Path.Combine(cache, relative), new[] { cache });
            FileSafety.AssertRealParentChain(path, modRoot);
            if (FileSafety.GetNodeInfoIfExists(path) is not FileInfo)
                FileSafety.Fail("missing_source", ("target", path));
            if (new FileInfo(path).Length != file.GetProperty("bytes").GetInt64()
                || await RuntimePackageBuilder.HashAsync(path) != file.GetProperty("sha256").GetString())
                FileSafety.Fail("source_corrupted", ("target", path));
            result.Add((path, "platform/steam/" + relative));
        }
        return result;
    }
}
