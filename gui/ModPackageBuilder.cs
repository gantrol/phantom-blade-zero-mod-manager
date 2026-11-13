using System.Text;
using System.Text.Json;

namespace PhantomBladeModManager;

internal sealed record ScriptHookSpec(string AssetPath, string Sha256, string Anchor, string Injection, string? AltSha256 = null)
{
    public bool Matches(string digest) =>
        string.Equals(digest, Sha256, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(AltSha256) && string.Equals(digest, AltSha256, StringComparison.OrdinalIgnoreCase));
}

internal sealed record JsPakBuildSpec(
    string Id,
    string Subpath,
    string PakBase,
    string Version,
    string ModuleNamespace,
    string[] ModuleFiles,
    ScriptHookSpec[] Hooks,
    bool ControlsApi = false,
    Dictionary<string, string[]>? ExtraOriginals = null,
    Dictionary<string, object?>? ExtraManifestFields = null,
    string BuildId = "26367565");

internal static class ModPackageBuilder
{
    private const string Prefix = "ProjectPBZ/Content/JavaScript/";

    private static readonly JsPakBuildSpec[] JsSpecs =
    {
        new(
            "killingIntent",
            "features/phantom-blade-killing-intent",
            "zzzz_PhantomBladeKillingIntent_P",
            "0.1.0",
            "PhantomBladeKillingIntent",
            new[] { "Config.js", "Meter.js", "Runtime.js" },
            new[]
            {
                new ScriptHookSpec(
                    Prefix + "UI/Widget/Hud/PlayerHpBar.js",
                    "0b6e039e1bc7cfc6f0d5383cf1a16414f7d0dbca9bd81dc3b7f8822584d03ace",
                    "exports.playerHPBarMixinInterface = playerHPBarMixinInterface;",
                    "\nrequire(\"../../../PhantomBladeKillingIntent/Runtime\").install(playerHPBarMixinInterface, \"player\");\n",
                    "b8834ef845a173f3ef75dc611a7a876202461f29dfa168bdece9c3db90079c6c"),
                new ScriptHookSpec(
                    Prefix + "UI/Widget/Hud/EnemyHPBar.js",
                    "e190ed1b0ba63900c521b27acd73e0fa4c00e715b00ec08bf90bdcb9e520006a",
                    "exports.enemyHPBarMixinInterface = enemyHPBarMixinInterface;",
                    "\nrequire(\"../../../PhantomBladeKillingIntent/Runtime\").install(enemyHPBarMixinInterface, \"boss\");\n",
                    "fd2381193320e3e4a657b8878d905dc0b203bd0f2b954fd5277b7a5c1ea55f31"),
            }),
        new(
            "quickActions",
            "features/phantom-blade-quick-actions",
            "zzzz_PhantomBladeQuickActions_P",
            "0.2.0",
            "PhantomBladeQuickActions",
            new[] { "Controls.js", "Runtime.js" },
            new[]
            {
                new ScriptHookSpec(
                    Prefix + "UI/Widget/ShopSystemWidget.js",
                    "1cae858c43f3fcf399cbf97fb92af0766ce1246bb38f1f19aa7ece3c3c8d8b6f",
                    "exports.shopSystemMixinInterface = shopSystemMixinInterface;",
                    "\nrequire(\"../../PhantomBladeQuickActions/Runtime\").install(shopSystemMixinInterface, \"buy\");\n",
                    "9024f951326992c9d2a9edcc7847d563240e99e0a9807643678ff7dbd6b95b2c"),
                new ScriptHookSpec(
                    Prefix + "UI/Widget/UpgradeSystemWidget_V1.js",
                    "b7265bfb92aaec254eb5812c306fe5c46b661b379646a0e20a928fa42f94ffd4",
                    "exports.UpgradeSystemWidgetMixin_V1_Interface = UpgradeSystemWidgetMixin_V1_Interface;",
                    "\nrequire(\"../../PhantomBladeQuickActions/Runtime\").install(UpgradeSystemWidgetMixin_V1_Interface, \"upgrade\");\n",
                    "ccc7a9a752d8680e0cd5cd9ea8adb42976616cf94d7f9f6b2240d9ac6e3e861a"),
            },
            ControlsApi: true,
            ExtraOriginals: new Dictionary<string, string[]>
            {
                [Prefix + "Gameplay/UIGameplayLogicComponent.js"] = new[]
                {
                    "c9e1e2796923ed74de9d1d5b238f564a5292a2abc50a0268f26003124f91b479",
                    "2c24c1390c442de2eda786cf44b9ce898d52a333f7be1b93743b0eb7c65bed23",
                },
            }),
        new(
            "equipment",
            "features/phantom-blade-equipment-sync",
            "zzzz_PhantomBladeEquipmentSync_P",
            "0.2.0",
            "PhantomBladeEquipmentSync",
            new[] { "Runtime.js" },
            new[]
            {
                new ScriptHookSpec(
                    Prefix + "Gameplay/UIGameplayLogicComponent.js",
                    "c9e1e2796923ed74de9d1d5b238f564a5292a2abc50a0268f26003124f91b479",
                    "exports.uiGameplayLogicMixinInterface = uiGameplayLogicMixinInterface;",
                    "\nrequire(\"../PhantomBladeEquipmentSync/Runtime\").install(uiGameplayLogicMixinInterface);\n",
                    "2c24c1390c442de2eda786cf44b9ce898d52a333f7be1b93743b0eb7c65bed23"),
            }),
        new(
            "teleport",
            "remedies/phantom-blade-teleport",
            "zzzz_PhantomBladeTeleport_P",
            "0.2.0",
            "PhantomBladeTeleport",
            new[] { "Controls.js", "Runtime.js" },
            new[]
            {
                new ScriptHookSpec(
                    Prefix + "Gameplay/GameModeBuild.js",
                    "ae379b31c462d700b6286c1d2ef4288411ed567e472421142ca1ac94f81b4dce",
                    "exports.GameModeBuildMixinInterface = GameModeBuildMixinInterface;",
                    "\nrequire(\"../PhantomBladeTeleport/Runtime\").install(GameModeBuildMixinInterface);\n",
                    "16d7c48d469562bd6c0037f45843eff80f91bbe3e345c8eb83d5ea44454f65eb"),
            },
            ControlsApi: true),
        new(
            "documentRecovery",
            "remedies/phantom-blade-document-recovery",
            "zzzz_PhantomBladeDocumentRecovery_P",
            "0.1.0",
            "PhantomBladeDocumentRecovery",
            new[] { "Rules.js", "Runtime.js" },
            new[]
            {
                new ScriptHookSpec(
                    Prefix + "UI/Widget/Literature.js",
                    "387c44db36700ceea483284cc62d716e4927fb3c62c8bc11c9fb1d9e71ca6b26",
                    "exports.LiteratureWidgetInteraface = LiteratureWidgetInteraface;",
                    "\ntry { require(\"../../PhantomBladeDocumentRecovery/Runtime\").install(LiteratureWidgetInteraface); } catch (error) { console.warn(\"[PhantomBladeDocumentRecovery] \" + String(error)); }\n",
                    "c81c9261c25547cd437be1bf61d8a3db61b78290f9701fc5c019ba20ea76fd2e"),
            },
            ExtraManifestFields: new Dictionary<string, object?>
            {
                ["recovery"] = new Dictionary<string, object?>
                {
                    ["item"] = "R_070001",
                    ["book"] = "Book_03",
                    ["page"] = 16,
                    ["trigger"] = "LiteratureWidget.BP_InitWidget",
                    ["refresh"] = "cacheOwnBookIDs",
                    ["requiresPriorAcquisition"] = true,
                    ["requiresCompletedHandover"] = true,
                },
            }),
    };

    public static async Task<Dictionary<string, object?>> BuildAsync(string root, string game, string targetId)
    {
        if (targetId.Equals("sprint", StringComparison.OrdinalIgnoreCase))
            return await RuntimePackageBuilder.BuildSprintAsync(root, game);
        if (targetId.Equals("talisman", StringComparison.OrdinalIgnoreCase))
            return await TalismanPackageBuilder.BuildAsync(root, game);
        if (targetId.Equals("targeting", StringComparison.OrdinalIgnoreCase))
            return await TargetingPackageBuilder.BuildAsync(root, game);
        var toolkit = Path.Combine(root, "mods", "features", "phantom-blade-map", ".local-runtime", "pak-tools");
        var repak = Path.Combine(toolkit, "repak.exe");
        var retoc = Path.Combine(toolkit, "retoc.exe");
        var keyFile = Path.Combine(toolkit, "key.local");
        if (!File.Exists(repak) || !File.Exists(retoc) || !File.Exists(keyFile))
            FileSafety.Fail("missing_build_tools", ("target", toolkit));
        var key = (await File.ReadAllTextAsync(keyFile)).Trim();
        var sourcePak = Path.Combine(game, "ProjectPBZ", "Content", "Paks", "ProjectPBZ-Windows.pak");
        if (!File.Exists(sourcePak)) FileSafety.Fail("game_pak_missing", ("target", sourcePak));

        if (string.Equals(targetId, "all", StringComparison.OrdinalIgnoreCase))
        {
            var results = new List<Dictionary<string, object?>>
            {
                await RuntimePackageBuilder.BuildSprintAsync(root, game),
                await TalismanPackageBuilder.BuildAsync(root, game),
                await TargetingPackageBuilder.BuildAsync(root, game),
            };
            foreach (var spec in JsSpecs)
                results.Add(await BuildJsPakSpecAsync(root, game, sourcePak, repak, retoc, key, spec));
            results.Add(await ModNativeAssetPatcher.BuildMapRuntimeAsync(root, game, repak, retoc, key));
            return new Dictionary<string, object?>
            {
                ["built"] = results.Select(r => r["id"]).ToList(),
                ["packages"] = results,
            };
        }

        var match = JsSpecs.FirstOrDefault(s => string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            return await BuildJsPakSpecAsync(root, game, sourcePak, repak, retoc, key, match);

        return targetId.ToLowerInvariant() switch
        {
            "memoryflowers" => await ModNativeAssetPatcher.BuildMemoryFlowersAsync(root, game, repak, retoc, toolkit),
            "cauldronregret" => await ModNativeAssetPatcher.BuildCauldronRegretAsync(root, game, repak, retoc, toolkit),
            "songxiaowuregret" => await ModNativeAssetPatcher.BuildSongXiaoWuRegretAsync(root, game, repak, retoc, toolkit),
            "map" => await ModNativeAssetPatcher.BuildMapRuntimeAsync(root, game, repak, retoc, key),
            "arena" => await ModNativeAssetPatcher.BuildArenaPackageAsync(root, game, repak, retoc),
            "chaos-reset" or "questmanager" => await ModNativeAssetPatcher.BuildChaosResetManagerAsync(root, toolkit),
            _ => throw new ManagerException("unknown_mod", new() { ["id"] = targetId }),
        };
    }

    private static async Task<Dictionary<string, object?>> BuildJsPakSpecAsync(
        string root, string game, string sourcePak, string repak, string retoc, string key, JsPakBuildSpec spec)
    {
        if (spec.Id == "quickActions")
            return await RuntimePackageBuilder.BuildQuickActionsAsync(root, game, sourcePak, repak, key, spec);
        var modRoot = Path.Combine(root, "mods", spec.Subpath.Replace('/', Path.DirectorySeparatorChar));
        var stage = Path.Combine(modRoot, ".local", $"build-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", "stage");
        var output = Path.Combine(modRoot, "dist", "package");
        var paksDir = Path.Combine(output, "Paks");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(paksDir);

        var scripts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);

        if (spec.ExtraOriginals != null)
        {
            foreach (var (asset, expectedDigests) in spec.ExtraOriginals)
            {
                var raw = await FileSafety.RunToolCheckedAsync(repak, new[] { "-a", key, "get", sourcePak, asset });
                var actualDigest = FileSafety.BytesHash(raw);
                if (!expectedDigests.Any(d => string.Equals(d, actualDigest, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"ORIGINAL_MISMATCH: {asset} ({actualDigest})");
                originals[asset] = actualDigest;
            }
        }

        async Task WriteStageScriptAsync(string relPath, byte[] bytes)
        {
            var dst = Path.Combine(stage, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            await File.WriteAllBytesAsync(dst, bytes);
            scripts[relPath] = FileSafety.BytesHash(bytes);
        }

        foreach (var hook in spec.Hooks)
        {
            var raw = await FileSafety.RunToolCheckedAsync(repak, new[] { "-a", key, "get", sourcePak, hook.AssetPath });
            var actualDigest = FileSafety.BytesHash(raw);
            if (!hook.Matches(actualDigest))
                throw new InvalidOperationException($"SOURCE_MISMATCH: {hook.AssetPath} ({actualDigest} != {hook.Sha256})");
            var sourceText = Encoding.UTF8.GetString(raw);
            var parts = sourceText.Split(new[] { hook.Anchor }, StringSplitOptions.None);
            if (parts.Length != 2)
                throw new InvalidOperationException($"ANCHOR_MISMATCH: {hook.AssetPath}");
            var patchedText = parts[0] + hook.Anchor + hook.Injection + parts[1];
            originals[hook.AssetPath] = actualDigest;
            await WriteStageScriptAsync(hook.AssetPath, new UTF8Encoding(false).GetBytes(patchedText));
        }

        foreach (var file in spec.ModuleFiles)
        {
            var srcFile = Path.Combine(modRoot, "puerts", file);
            var bytes = await File.ReadAllBytesAsync(srcFile);
            await WriteStageScriptAsync($"{Prefix}{spec.ModuleNamespace}/{file}", bytes);
        }

        if (spec.ControlsApi)
        {
            var controlsText = await File.ReadAllTextAsync(Path.Combine(root, "controls.json"), Encoding.UTF8);
            using var doc = JsonDocument.Parse(controlsText);
            var compact = JsonSerializer.Serialize(doc.RootElement);
            var generated = $"\"use strict\";\nmodule.exports = {compact};\n";
            await WriteStageScriptAsync($"{Prefix}{spec.ModuleNamespace}/Bindings.generated.js", new UTF8Encoding(false).GetBytes(generated));
        }

        var utocPath = Path.Combine(paksDir, spec.PakBase + ".utoc");
        var pakPath = Path.Combine(paksDir, spec.PakBase + ".pak");
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "to-zen", "--version", "UE5_7", stage, utocPath });
        await ModNativeAssetPatcher.CompressIoStoreContainerAsync(utocPath);
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "verify", utocPath });
        await FileSafety.RunToolCheckedAsync(repak, new[] { "pack", "-q", "--version", "V11", "--compression", "Zlib", "--mount-point", "../../../", stage, pakPath });

        var listOut = Encoding.UTF8.GetString(await FileSafety.RunToolCheckedAsync(repak, new[] { "list", pakPath }));
        var inventory = listOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        if (!inventory.SequenceEqual(scripts.Keys))
            throw new InvalidOperationException($"PAK_INVENTORY_MISMATCH: {spec.Id}");

        foreach (var asset in inventory)
        {
            var readback = await FileSafety.RunToolCheckedAsync(repak, new[] { "get", pakPath, asset });
            if (FileSafety.BytesHash(readback) != scripts[asset])
                throw new InvalidOperationException($"PAK_READBACK_MISMATCH: {asset}");
        }

        var files = new List<FileDigestEntry>();
        foreach (var ext in new[] { "pak", "utoc", "ucas" })
        {
            var rel = $"Paks/{spec.PakBase}.{ext}";
            var full = Path.Combine(output, rel.Replace('/', Path.DirectorySeparatorChar));
            var fi = new FileInfo(full);
            files.Add(new FileDigestEntry(rel, fi.Length, FileSafety.FileHash(full)));
        }

        var detectedBuildId = ManagerEngine.ReadInstalledGameBuildId(game) ?? spec.BuildId;
        var manifest = new Dictionary<string, object?>
        {
            ["id"] = spec.Id,
            ["version"] = spec.Version,
            ["buildId"] = detectedBuildId,
            ["runtimeVerified"] = false,
            ["scriptAssets"] = inventory,
            ["originals"] = originals,
            ["files"] = files,
        };
        if (spec.ControlsApi) manifest["controlsApi"] = 1;
        if (spec.ExtraManifestFields != null)
        {
            foreach (var (k, v) in spec.ExtraManifestFields) manifest[k] = v;
        }

        FileSafety.WriteJsonAtomic(Path.Combine(output, "manifest.json"), manifest, new[] { modRoot });
        var installTxt = Path.Combine(modRoot, "INSTALL.txt");
        if (File.Exists(installTxt))
            File.Copy(installTxt, Path.Combine(output, "INSTALL.txt"), true);

        try { Directory.Delete(Path.GetDirectoryName(stage)!, true); } catch { }

        return new Dictionary<string, object?>
        {
            ["id"] = spec.Id,
            ["version"] = spec.Version,
            ["output"] = output,
            ["scriptCount"] = inventory.Length,
            ["files"] = files,
        };
    }
}
