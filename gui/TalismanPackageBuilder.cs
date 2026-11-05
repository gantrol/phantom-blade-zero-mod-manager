using System.Text;
using System.Text.Json;

namespace PhantomBladeModManager;

internal static class TalismanPackageBuilder
{
    private const string Version = "0.3.0";
    private const string Prefix = "ProjectPBZ/Content/JavaScript/";

    public static async Task<Dictionary<string, object?>> BuildAsync(string root, string game)
    {
        RuntimePackageBuilder.RequireCurrentBuild(game);
        var toolkit = Path.Combine(root, "mods", "features", "phantom-blade-map", ".local-runtime", "pak-tools");
        var repak = Path.Combine(toolkit, "repak.exe");
        var key = (await File.ReadAllTextAsync(Path.Combine(toolkit, "key.local"))).Trim();
        var pak = Path.Combine(game, "ProjectPBZ", "Content", "Paks", "ProjectPBZ-Windows.pak");
        var originals = new Dictionary<string, string>();
        async Task<string> ReadOriginalAsync(string asset, string expected)
        {
            var bytes = await FileSafety.RunToolCheckedAsync(repak, new[] { "-a", key, "get", pak, Prefix + asset });
            var digest = FileSafety.BytesHash(bytes);
            if (digest != expected) throw new InvalidOperationException($"SOURCE_MISMATCH: {asset} ({digest})");
            originals[Prefix + asset] = digest;
            return Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        }

        var map = await ReadOriginalAsync("UI/Widget/Map.js", "44e6a10741403af0cc5dee63ec816dd2096a71d08668ec8f864994ceb945dc70");
        var upgrade = await ReadOriginalAsync("UI/Widget/UpgradeSystemWidget_V1.js", "b7265bfb92aaec254eb5812c306fe5c46b661b379646a0e20a928fa42f94ffd4");

        // Carry forward the reviewed game's gates and refund logic; change only the
        // nearby-bell check, confirmation cost display and the two debit statements.
        var gates = Section(map, "    runTeleportGates(", "\n    }");
        var nearbyBells = Section(gates, "        if (gameSystem) {", "\n        }");
        gates = ReplaceOnce(gates, nearbyBells, "");
        var confirm = Section(map, "    confirmAndConsumeCharm(", "\n    }");
        var costDescription = Section(confirm, "        let owned =", "        let descriptor =");
        confirm = ReplaceOnce(confirm, costDescription,
            "        let confirmText = cpp_1.FText.FromString(Locales.text(\"teleportConfirm\"));\n        let descriptor =");
        confirm = ReplaceOnce(confirm, "false, TeleportCharmConfig.ItemID, 1)", "false, TeleportCharmConfig.ItemID, 0)");
        confirm = ReplaceOnce(confirm, "            backpack.RemovePropsFromBackPack(1, TeleportCharmConfig.ItemID, true);", "");
        var downgrade = Section(upgrade, "    executeDowngrade(", "\n    }");
        downgrade = ReplaceOnce(downgrade,
            "            backpackSubsystem.RemovePropsFromBackPack(cost, fuItemID, false); // 回退符消耗静默扣除：不广播 UpdateProps，不弹道具墨条", "");

        var generated = """
            "use strict";
            // Generated from SHA-256 checked game scripts for Steam build 26367565.
            const UE = require("ue");
            const puerts_1 = require("puerts");
            const cpp_1 = require("cpp");
            const TsHelper_1 = require("Library/TsHelper");
            const Locales = require("./Locales");
            """ + "\n"
            + Section(map, "const TeleportCharmConfig =", "\n};") + "\n"
            + Section(map, "const FlowCriticalSectionSource =", "\n};") + "\n"
            + Section(upgrade, "const ITEM_RETURN_WEAPON_FU =", ";") + "\n"
            + Section(upgrade, "const ITEM_RETURN_SHAQI_WEAPON_FU =", ";") + "\n"
            + Section(upgrade, "function calcDowngradeFuCost(", "\n}") + "\n"
            + "class MapHooks {\n" + gates + "\n" + confirm + "\n}\n"
            + "class UpgradeHooks {\n" + downgrade + "\n}\n"
            + "module.exports = { MapHooks, UpgradeHooks };\n";

        var modRoot = Path.Combine(root, "mods", "features", "phantom-blade-infinite-talisman");
        var output = Path.Combine(modRoot, "dist", "runtime");
        FileSafety.RemoveTreeIfExists(output, new[] { Path.Combine(modRoot, "dist") });
        var modules = Path.Combine(output, "Scripts", "PhantomBladeInfiniteTalisman");
        await RuntimePackageBuilder.CopyScriptsAsync(Path.Combine(modRoot, "puerts"), modules);
        await File.WriteAllTextAsync(Path.Combine(modules, "Hooks.generated.js"), generated, new UTF8Encoding(false));
        await RuntimePackageBuilder.AddLoaderAsync(root, output);
        var files = await RuntimePackageBuilder.InventoryAsync(output);
        var manifest = new { schema = 1, id = "talisman", version = Version, backend = "nativeRuntime", buildId = RuntimePackageBuilder.BuildId, runtimeVersion = RuntimePackageBuilder.RuntimeVersion, runtimeVerified = true, originals, files };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, FileSafety.JsonOptions));
        return new() { ["id"] = "talisman", ["version"] = Version, ["output"] = output, ["files"] = files.Count };
    }

    // Exact source fingerprints above make these indentation boundaries unambiguous.
    private static string Section(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        var to = from < 0 ? -1 : source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        if (to < 0) throw new InvalidOperationException($"TALISMAN_ANCHOR_MISMATCH: {start}");
        return source[from..(to + end.Length)];
    }

    private static string ReplaceOnce(string source, string before, string after)
    {
        var parts = source.Split(new[] { before }, StringSplitOptions.None);
        if (parts.Length != 2) throw new InvalidOperationException($"TALISMAN_PATCH_MISMATCH: {before}");
        return parts[0] + after + parts[1];
    }
}
