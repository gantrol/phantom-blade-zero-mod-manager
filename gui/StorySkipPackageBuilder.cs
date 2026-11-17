using System.Text.Json;

namespace PhantomBladeModManager;

internal static class StorySkipPackageBuilder
{
    private const string Version = "0.2.0";
    private const string ExecutableSha256 = "4415933ce33476f14c837abb61a1b60af8b78cd81783896501913a7aac69e423";

    public static async Task<Dictionary<string, object?>> BuildAsync(string root, string game)
    {
        RuntimePackageBuilder.RequireCurrentBuild(game);
        if (await RuntimePackageBuilder.HashAsync(Path.Combine(game, "platform", "steam", "PBZ.exe")) != ExecutableSha256)
            throw new InvalidOperationException("STORY_EXECUTABLE_MISMATCH: review QTE and native skip interfaces before building");
        var modRoot = Path.Combine(root, "mods", "features", "phantom-blade-story-skip");
        var output = Path.Combine(modRoot, "dist", "runtime");
        FileSafety.RemoveTreeIfExists(output, new[] { Path.Combine(modRoot, "dist") });
        await RuntimePackageBuilder.CopyScriptsAsync(Path.Combine(modRoot, "puerts"), Path.Combine(output, "Scripts", "PhantomBladeStorySkip"));
        await RuntimePackageBuilder.AddLoaderAsync(root, output);
        await RuntimePackageBuilder.CopyFileAsync(Path.Combine(modRoot, "INSTALL.txt"), Path.Combine(output, "INSTALL.txt"));
        var files = await RuntimePackageBuilder.InventoryAsync(output);
        var manifest = new
        {
            schema = 1, id = "story", version = Version, backend = "nativeRuntime",
            buildId = RuntimePackageBuilder.BuildId, executableSha256 = ExecutableSha256,
            runtimeVersion = RuntimePackageBuilder.RuntimeVersion, runtimeVerified = false,
            input = new { key = "Gamepad_FaceButton_Right", trigger = "pressed", mode = "continuous-native-skip" },
            exclusions = new { qteNodes = true, recursiveQteTracks = true, qteFailureBranches = true,
                activeQteInput = true, dialogue = true, unknownScenes = true },
            files
        };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, FileSafety.JsonOptions));
        return new() { ["id"] = "story", ["version"] = Version, ["output"] = output, ["files"] = files.Count };
    }
}
