namespace PhantomBladeModManager;

internal sealed record ScriptSourceSpec(string AssetPath, string Sha256, string? AltSha256 = null);

internal sealed record RuntimeModBuildSpec(
    string Id,
    string Subpath,
    string Version,
    string ModuleNamespace,
    string[] ModuleFiles,
    ScriptSourceSpec[] Sources,
    bool ControlsApi = false,
    Dictionary<string, string[]>? ExtraOriginals = null,
    Dictionary<string, object?>? ExtraManifestFields = null);

internal static class ModPackageBuilder
{
    private const string Prefix = "ProjectPBZ/Content/JavaScript/";

    private static readonly RuntimeModBuildSpec[] RuntimeSpecs =
    {
        new(
            "killingIntent",
            "features/phantom-blade-killing-intent",
            "0.2.0",
            "PhantomBladeKillingIntent",
            new[] { "Config.js", "Meter.js", "Runtime.js" },
            new[]
            {
                new ScriptSourceSpec(
                    Prefix + "UI/Widget/Hud/PlayerHpBar.js",
                    "0b6e039e1bc7cfc6f0d5383cf1a16414f7d0dbca9bd81dc3b7f8822584d03ace",
                    "b8834ef845a173f3ef75dc611a7a876202461f29dfa168bdece9c3db90079c6c"),
            }),
        new(
            "quickActions",
            "features/phantom-blade-quick-actions",
            "0.2.0",
            "PhantomBladeQuickActions",
            new[] { "Controls.js", "Runtime.js" },
            new[]
            {
                new ScriptSourceSpec(
                    Prefix + "UI/Widget/ShopSystemWidget.js",
                    "1cae858c43f3fcf399cbf97fb92af0766ce1246bb38f1f19aa7ece3c3c8d8b6f",
                    "9024f951326992c9d2a9edcc7847d563240e99e0a9807643678ff7dbd6b95b2c"),
                new ScriptSourceSpec(
                    Prefix + "UI/Widget/UpgradeSystemWidget_V1.js",
                    "b7265bfb92aaec254eb5812c306fe5c46b661b379646a0e20a928fa42f94ffd4",
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
            "0.2.0",
            "PhantomBladeEquipmentSync",
            new[] { "Runtime.js" },
            new[]
            {
                new ScriptSourceSpec(
                    Prefix + "Gameplay/UIGameplayLogicComponent.js",
                    "c9e1e2796923ed74de9d1d5b238f564a5292a2abc50a0268f26003124f91b479",
                    "2c24c1390c442de2eda786cf44b9ce898d52a333f7be1b93743b0eb7c65bed23"),
            }),
        new(
            "teleport",
            "remedies/phantom-blade-teleport",
            "0.2.0",
            "PhantomBladeTeleport",
            new[] { "Controls.js", "Runtime.js" },
            new[]
            {
                new ScriptSourceSpec(
                    Prefix + "Gameplay/GameModeBuild.js",
                    "ae379b31c462d700b6286c1d2ef4288411ed567e472421142ca1ac94f81b4dce",
                    "16d7c48d469562bd6c0037f45843eff80f91bbe3e345c8eb83d5ea44454f65eb"),
            },
            ControlsApi: true),
        new(
            "documentRecovery",
            "remedies/phantom-blade-document-recovery",
            "0.1.0",
            "PhantomBladeDocumentRecovery",
            new[] { "Rules.js", "Runtime.js" },
            new[]
            {
                new ScriptSourceSpec(
                    Prefix + "UI/Widget/Literature.js",
                    "387c44db36700ceea483284cc62d716e4927fb3c62c8bc11c9fb1d9e71ca6b26",
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
            foreach (var spec in RuntimeSpecs)
                results.Add(await RuntimePackageBuilder.BuildScriptModAsync(root, game, sourcePak, repak, key, spec));
            results.Add(await ModNativeAssetPatcher.BuildMapRuntimeAsync(root, game, repak, retoc, key));
            return new Dictionary<string, object?>
            {
                ["built"] = results.Select(r => r["id"]).ToList(),
                ["packages"] = results,
            };
        }

        var match = RuntimeSpecs.FirstOrDefault(s => string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            return await RuntimePackageBuilder.BuildScriptModAsync(root, game, sourcePak, repak, key, match);

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

}
