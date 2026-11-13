using System.Text.Json;

namespace PhantomBladeModManager;

internal static class TargetingPackageBuilder
{
    private const string Version = "0.1.0";
    private const string ExecutableSha256 = "4415933ce33476f14c837abb61a1b60af8b78cd81783896501913a7aac69e423";

    public static async Task<Dictionary<string, object?>> BuildAsync(string root, string game)
    {
        RuntimePackageBuilder.RequireCurrentBuild(game);
        var executable = Path.Combine(game, "platform", "steam", "PBZ.exe");
        if (await RuntimePackageBuilder.HashAsync(executable) != ExecutableSha256)
            throw new InvalidOperationException("TARGETING_EXECUTABLE_MISMATCH: review current targeting interfaces before building");
        var modRoot = Path.Combine(root, "mods", "features", "phantom-blade-targeting");
        var output = Path.Combine(modRoot, "dist", "runtime");
        FileSafety.RemoveTreeIfExists(output, new[] { Path.Combine(modRoot, "dist") });
        await RuntimePackageBuilder.CopyScriptsAsync(Path.Combine(modRoot, "puerts"), Path.Combine(output, "Scripts", "PhantomBladeTargeting"));
        await RuntimePackageBuilder.AddLoaderAsync(root, output);
        var files = await RuntimePackageBuilder.InventoryAsync(output);
        var manifest = new { schema = 1, id = "targeting", version = Version, backend = "nativeRuntime",
            buildId = RuntimePackageBuilder.BuildId, executableSha256 = ExecutableSha256,
            runtimeVersion = RuntimePackageBuilder.RuntimeVersion, runtimeVerified = true, files };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, FileSafety.JsonOptions));
        return new() { ["id"] = "targeting", ["version"] = Version, ["output"] = output, ["files"] = files.Count };
    }
}
