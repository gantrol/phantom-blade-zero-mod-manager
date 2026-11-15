using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhantomBladeModManager;

internal static class RuntimePackageBuilder
{
    public const string BuildId = "26367565";
    public const string RuntimeVersion = "1.5.0";

    // Match native/runtime/Loader.cpp. Geometry is loaded by PuerTS on demand;
    // its disk inventory must not be mistaken for resident source memory.
    public static void ValidateScriptBudget(IEnumerable<(string Path, long Bytes)> scripts, Version runtimeVersion)
    {
        var legacy = runtimeVersion < new Version(1, 2, 0);
        long total = 0;
        int count = 0;
        foreach (var (path, bytes) in scripts)
        {
            total += bytes;
            count++;
            if (bytes < 0 || bytes > 16L * 1024 * 1024 || count > 32768 || total > (legacy ? 64L : 512L) * 1024 * 1024)
                FileSafety.Fail("runtime_script_budget", ("target", path), ("version", runtimeVersion.ToString()));
        }
    }

    public static void RequireCurrentBuild(string game)
    {
        if (ManagerEngine.ReadInstalledGameBuildId(game) != BuildId)
            FileSafety.Fail("incompatible_build", ("expected", BuildId), ("actual", ManagerEngine.ReadInstalledGameBuildId(game)));
    }

    public static async Task<string> HashAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    public static async Task CopyFileAsync(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await input.CopyToAsync(output);
    }

    public static async Task CopyScriptsAsync(string source, string target, HashSet<string>? excluded = null)
    {
        FileSafety.AssertRealTree(source, source);
        foreach (var file in Directory.EnumerateFiles(source, "*.js", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (excluded != null && relative.Split(Path.DirectorySeparatorChar).Any(excluded.Contains)) continue;
            await CopyFileAsync(file, Path.Combine(target, relative));
        }
    }

    public static async Task<List<FileDigestEntry>> InventoryAsync(string directory)
    {
        var files = new List<FileDigestEntry>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            files.Add(new(Path.GetRelativePath(directory, file).Replace('\\', '/'), new FileInfo(file).Length, await HashAsync(file)));
        ValidateScriptBudget(files.Where(f => f.Path.EndsWith(".js", StringComparison.Ordinal))
            .Select(f => (f.Path, f.Bytes)), Version.Parse(RuntimeVersion));
        return files;
    }

    public static async Task AddLoaderAsync(string root, string package)
    {
        await FileSafety.RunToolCheckedAsync("cmd.exe", new[] { "/d", "/c", Path.Combine(root, "native", "runtime", "Build.cmd") });
        await CopyFileAsync(Path.Combine(root, ".local", "runtime-build", "version.dll"), Path.Combine(package, "Runtime", "version.dll"));
        foreach (var name in new[] { "Bootstrap.js", "Controls.js", "Assets.js", "COMPATIBILITY.txt" })
            await CopyFileAsync(Path.Combine(root, "native", "runtime", name), Path.Combine(package, "Runtime", name));
        using var controls = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "controls.json")));
        await File.WriteAllTextAsync(Path.Combine(package, "Runtime", "Bindings.generated.js"), "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(controls.RootElement) + ";\n", new UTF8Encoding(false));
        await CopyFileAsync(Path.Combine(root, "mods", "features", "phantom-blade-training", "native", "vendor", "minhook", "LICENSE.txt"), Path.Combine(package, "Runtime", "MinHook-LICENSE.txt"));
        await CopyFileAsync(Path.Combine(root, "native", "runtime", "vendor", "miniz", "LICENSE"), Path.Combine(package, "Runtime", "miniz-LICENSE.txt"));
    }

    public static async Task<Dictionary<string, object?>> BuildSprintAsync(string root, string game)
    {
        RequireCurrentBuild(game);
        var modRoot = Path.Combine(root, "mods", "features", "phantom-blade-sprint");
        var output = Path.Combine(modRoot, "dist", "runtime");
        FileSafety.RemoveTreeIfExists(output, new[] { Path.Combine(modRoot, "dist") });
        var modules = Path.Combine(output, "Scripts", "PhantomBladeSprint");
        await CopyScriptsAsync(Path.Combine(modRoot, "puerts"), modules);
        await AddLoaderAsync(root, output);
        var files = await InventoryAsync(output);
        var manifest = new { schema = 1, id = "sprint", version = "0.2.0", backend = "nativeRuntime", buildId = BuildId, runtimeVersion = RuntimeVersion, runtimeVerified = true, controlsApi = 1, files };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, FileSafety.JsonOptions));
        return new() { ["id"] = "sprint", ["version"] = "0.2.0", ["output"] = output, ["files"] = files.Count };
    }

    public static async Task<Dictionary<string, object?>> BuildScriptModAsync(
        string root, string game, string sourcePak, string repak, string key, RuntimeModBuildSpec spec)
    {
        RequireCurrentBuild(game);
        var originals = new Dictionary<string, string>();
        var expected = spec.Sources.ToDictionary(hook => hook.AssetPath, hook => hook.AltSha256 == null ? new[] { hook.Sha256 } : new[] { hook.Sha256, hook.AltSha256 });
        foreach (var (asset, hashes) in spec.ExtraOriginals ?? []) expected[asset] = hashes;
        foreach (var (asset, hashes) in expected)
        {
            var bytes = await FileSafety.RunToolCheckedAsync(repak, new[] { "-a", key, "get", sourcePak, asset });
            var digest = FileSafety.BytesHash(bytes);
            if (!hashes.Contains(digest)) throw new InvalidOperationException($"SOURCE_MISMATCH: {asset} ({digest})");
            originals[asset] = digest;
        }
        var modRoot = Path.Combine(root, "mods", spec.Subpath);
        var output = Path.Combine(modRoot, "dist", "runtime");
        FileSafety.RemoveTreeIfExists(output, new[] { Path.Combine(modRoot, "dist") });
        var modules = Path.Combine(output, "Scripts", spec.ModuleNamespace);
        foreach (var file in spec.ModuleFiles)
        {
            var target = Path.Combine(modules, file);
            if (spec.ControlsApi && file == "Controls.js")
            {
                Directory.CreateDirectory(modules);
                await File.WriteAllTextAsync(target,
                    "\"use strict\";\nmodule.exports = require(\"../PhantomBladeRuntime/Controls\");\n", new UTF8Encoding(false));
            }
            else await CopyFileAsync(Path.Combine(modRoot, "puerts", file), target);
        }
        await AddLoaderAsync(root, output);
        foreach (var name in new[] { "INSTALL.txt", "COMPATIBILITY.txt" })
        {
            var source = Path.Combine(modRoot, name);
            if (File.Exists(source)) await CopyFileAsync(source, Path.Combine(output, name));
        }
        var files = await InventoryAsync(output);
        var manifest = new Dictionary<string, object?>
        {
            ["schema"] = 1, ["id"] = spec.Id, ["version"] = spec.Version, ["backend"] = "nativeRuntime",
            ["buildId"] = BuildId, ["runtimeVersion"] = RuntimeVersion, ["runtimeVerified"] = false,
            ["originals"] = originals, ["files"] = files
        };
        if (spec.ControlsApi) manifest["controlsApi"] = 1;
        foreach (var (name, value) in spec.ExtraManifestFields ?? []) manifest[name] = value;
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, FileSafety.JsonOptions));
        return new() { ["id"] = spec.Id, ["version"] = spec.Version, ["output"] = output, ["files"] = files.Count };
    }
}
