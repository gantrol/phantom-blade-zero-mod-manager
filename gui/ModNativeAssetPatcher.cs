using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhantomBladeModManager;

internal sealed record ExportMetadata(string? Type, long Offset, long Size);
internal sealed record OriginalPackageMetadata(
    string Path, string ChunkId, string Sha256, int Bytes, string[] Names, int HeaderSize, int ExportOffset, ExportMetadata[] Exports);

internal static class ModNativeAssetPatcher
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<Dictionary<string, object?>> BuildMemoryFlowersAsync(
        string root, string game, string repak, string retoc, string toolkit)
    {
        var modRoot = Path.Combine(root, "mods", "remedies", "phantom-blade-memory-flowers");
        var baseName = "zzzz_PhantomBladeMemoryFlowers_P";
        var work = Path.Combine(modRoot, ".local", $"build-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        var empty = Path.Combine(work, "empty");
        var template = Path.Combine(work, "template");
        var raw = Path.Combine(work, "patched");
        var stage = Path.Combine(work, "stage");
        var output = Path.Combine(modRoot, "dist", "package");
        var paks = Path.Combine(output, "Paks");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(template);
        Directory.CreateDirectory(paks);

        const string mapPath = "ProjectPBZ/Content/JavaScript/PhantomBladeMap/CollectionAvailability.js";
        const string mapDigest = "d37763553d43c4e1e7329c7c9f25e695a35ee396e0e5066ff95ea925e99618da";
        var localMapPath = Path.Combine(root, "mods", "features", "phantom-blade-map", "puerts", "PhantomBladeMap", "CollectionAvailability.js");
        var mapBytes = await File.ReadAllBytesAsync(localMapPath);
        if (FileSafety.BytesHash(mapBytes) != mapDigest)
            throw new InvalidOperationException("MAP_AVAILABILITY_VERSION_MISMATCH");
        var mapSource = Encoding.UTF8.GetString(mapBytes);

        await FileSafety.RunToolCheckedAsync(retoc, new[] { "to-zen", "--version", "UE5_7", empty, Path.Combine(template, baseName + ".utoc") });
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "unpack-raw", Path.Combine(template, baseName + ".utoc"), Path.Combine(template, "raw") });
        var headerFolder = Path.Combine(template, "raw", "chunks");
        var headers = Directory.GetFiles(headerFolder);
        if (headers.Length != 1 || !headers[0].EndsWith("00000006", StringComparison.Ordinal))
            throw new InvalidOperationException("EMPTY_HEADER_MISMATCH");

        await FileSafety.RunToolCheckedAsync("dotnet", new[]
        {
            "run", "--project", Path.Combine(modRoot, "tools", "FlowerPatch"), "--configuration", "Release", "--",
            Path.Combine(game, "ProjectPBZ", "Content", "Paks"),
            Path.Combine(toolkit, "key.local"),
            Path.Combine(toolkit, "oo2core_9_win64.dll"),
            raw,
            headers[0]
        });

        using var changesDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(raw, "changes.json"), Encoding.UTF8));
        var changesList = changesDoc.RootElement.EnumerateArray().ToList();
        const string rescueGate = "tags.has(\"pz.不灭侠胆.帮助正道解脱\"+index)";
        const string readAnchor = "function read(point,tags) {";
        if (mapSource.Split(new[] { rescueGate }, StringSplitOptions.None).Length != 2 ||
            mapSource.Split(new[] { readAnchor }, StringSplitOptions.None).Length != 2)
            throw new InvalidOperationException("MAP_AVAILABILITY_ANCHOR_MISMATCH");

        var deathTags = new Dictionary<string, string>();
        foreach (var item in changesList)
            deathTags[item.GetProperty("stage").GetInt32().ToString()] = item.GetProperty("toTag").GetString()!;

        var deathTagsJson = JsonSerializer.Serialize(deathTags, new JsonSerializerOptions { WriteIndented = true });
        var mapPatched = mapSource
            .Replace("the rescue tag enables", "the NPC death tag enables", StringComparison.Ordinal)
            .Replace(readAnchor, $"const deathTags = Object.freeze({deathTagsJson});\n{readAnchor}", StringComparison.Ordinal)
            .Replace(rescueGate, "tags.has(deathTags[index])", StringComparison.Ordinal);

        var mapFile = Path.Combine(stage, mapPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(mapFile)!);
        var mapPatchedBytes = new UTF8Encoding(false).GetBytes(mapPatched);
        await File.WriteAllBytesAsync(mapFile, mapPatchedBytes);

        var utocPath = Path.Combine(paks, baseName + ".utoc");
        var pakPath = Path.Combine(paks, baseName + ".pak");
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "pack-raw", raw, utocPath });
        await CompressIoStoreContainerAsync(utocPath);
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "verify", utocPath });
        await FileSafety.RunToolCheckedAsync(repak, new[] { "pack", "-q", "--version", "V11", "--compression", "Zlib", "--mount-point", "../../../", stage, pakPath });

        var files = CollectPakTriplet(output, baseName);
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        var overridden = new List<string>();
        foreach (var c in changesList)
        {
            var p = c.GetProperty("path").GetString()!;
            originals[p] = c.GetProperty("originalSha256").GetString()!;
            overridden.Add(p);
        }
        originals[mapPath] = mapDigest;
        overridden.Add(mapPath);

        var detectedBuildId = ManagerEngine.ReadInstalledGameBuildId(game) ?? "26266538";
        FileSafety.WriteJsonAtomic(Path.Combine(output, "manifest.json"), new Dictionary<string, object?>
        {
            ["id"] = "memoryFlowers",
            ["version"] = "0.1.0",
            ["buildId"] = detectedBuildId,
            ["runtimeVerified"] = false,
            ["originals"] = originals,
            ["overriddenAssets"] = overridden,
            ["files"] = files,
        }, new[] { modRoot });

        File.Copy(Path.Combine(modRoot, "INSTALL.txt"), Path.Combine(output, "INSTALL.txt"), true);
        try { Directory.Delete(work, true); } catch { }

        return new Dictionary<string, object?>
        {
            ["id"] = "memoryFlowers",
            ["version"] = "0.1.0",
            ["output"] = output,
            ["files"] = files,
        };
    }

    public static async Task<Dictionary<string, object?>> BuildCauldronRegretAsync(
        string root, string game, string repak, string retoc, string toolkit)
    {
        return await BuildFlowGraphRegretAsync(
            root, game, repak, retoc, toolkit,
            id: "cauldronRegret",
            subpath: Path.Combine("remedies", "phantom-blade-cauldron-regret"),
            patchProject: Path.Combine("tools", "CauldronPatch"),
            baseName: "zzzz_PhantomBladeCauldronRegret_P",
            patcher: PatchCauldronPackages);
    }

    public static async Task<Dictionary<string, object?>> BuildSongXiaoWuRegretAsync(
        string root, string game, string repak, string retoc, string toolkit)
    {
        return await BuildFlowGraphRegretAsync(
            root, game, repak, retoc, toolkit,
            id: "songXiaoWuRegret",
            subpath: Path.Combine("remedies", "phantom-blade-songxiaowu-regret"),
            patchProject: Path.Combine("tools", "SongXiaoWuPatch"),
            baseName: "zzzz_PhantomBladeSongXiaoWuRegret_P",
            patcher: PatchSongXiaoWuPackages);
    }

    private static async Task<Dictionary<string, object?>> BuildFlowGraphRegretAsync(
        string root, string game, string repak, string retoc, string toolkit,
        string id, string subpath, string patchProject, string baseName,
        Func<OriginalPackageMetadata[], byte[][], (byte[][] Results, List<Dictionary<string, object?>> Changes)> patcher)
    {
        var modRoot = Path.Combine(root, "mods", subpath);
        var work = Path.Combine(modRoot, ".local", $"build-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        var empty = Path.Combine(work, "empty");
        var template = Path.Combine(work, "template");
        var raw = Path.Combine(work, "raw");
        var output = Path.Combine(modRoot, "dist", "package");
        var paks = Path.Combine(output, "Paks");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(template);
        Directory.CreateDirectory(paks);

        await FileSafety.RunToolCheckedAsync(retoc, new[] { "to-zen", "--version", "UE5_7", empty, Path.Combine(template, baseName + ".utoc") });
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "unpack-raw", Path.Combine(template, baseName + ".utoc"), Path.Combine(template, "raw") });
        var headerFolder = Path.Combine(template, "raw", "chunks");
        var headers = Directory.GetFiles(headerFolder);
        if (headers.Length != 1 || !headers[0].EndsWith("00000006", StringComparison.Ordinal))
            throw new InvalidOperationException("EMPTY_HEADER_MISMATCH");

        var archiveArgs = new[]
        {
            "run", "--project", Path.Combine(modRoot, patchProject), "--configuration", "Release", "--",
            Path.Combine(game, "ProjectPBZ", "Content", "Paks"),
            Path.Combine(toolkit, "key.local"),
            Path.Combine(toolkit, "oo2core_9_win64.dll"),
            raw,
            headers[0]
        };
        await FileSafety.RunToolCheckedAsync("dotnet", archiveArgs);

        var originals = JsonSerializer.Deserialize<OriginalPackageMetadata[]>(
            await File.ReadAllTextAsync(Path.Combine(raw, "originals.json"), Encoding.UTF8), ReadJsonOptions)!;
        var raws = new byte[originals.Length][];
        for (int i = 0; i < originals.Length; i++)
            raws[i] = await File.ReadAllBytesAsync(Path.Combine(raw, "chunks", originals[i].ChunkId));

        var (results, changes) = patcher(originals, raws);
        for (int i = 0; i < originals.Length; i++)
            await File.WriteAllBytesAsync(Path.Combine(raw, "chunks", originals[i].ChunkId), results[i]);

        await FileSafety.RunToolCheckedAsync("dotnet", archiveArgs.Concat(new[] { "verify" }).ToArray());
        var verified = JsonSerializer.Deserialize<OriginalPackageMetadata[]>(
            await File.ReadAllTextAsync(Path.Combine(raw, "verified.json"), Encoding.UTF8), ReadJsonOptions)!;
        for (int i = 0; i < verified.Length; i++)
        {
            if (verified[i].Sha256 != (string)changes[i]["patchedSha256"]! || verified[i].Exports.Length != originals[i].Exports.Length)
                throw new InvalidOperationException("NATIVE_HEADER_READBACK_MISMATCH");
        }

        var utocPath = Path.Combine(paks, baseName + ".utoc");
        var pakPath = Path.Combine(paks, baseName + ".pak");
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "pack-raw", raw, utocPath });
        await CompressIoStoreContainerAsync(utocPath);
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "verify", utocPath });
        await FileSafety.RunToolCheckedAsync(repak, new[] { "pack", "-q", "--version", "V11", "--compression", "Zlib", "--mount-point", "../../../", empty, pakPath });

        var files = CollectPakTriplet(output, baseName);
        var detectedBuildId = ManagerEngine.ReadInstalledGameBuildId(game) ?? "26266538";
        FileSafety.WriteJsonAtomic(Path.Combine(output, "manifest.json"), new Dictionary<string, object?>
        {
            ["id"] = id,
            ["version"] = "0.1.0",
            ["buildId"] = detectedBuildId,
            ["runtimeVerified"] = false,
            ["originals"] = changes.ToDictionary(c => (string)c["path"]!, c => (string)c["originalSha256"]!),
            ["overriddenAssets"] = changes.Select(c => (string)c["path"]!).ToList(),
            ["files"] = files,
        }, new[] { modRoot });
        FileSafety.WriteJsonAtomic(Path.Combine(output, "patch-report.json"), new Dictionary<string, object?>
        {
            ["runtimeVerified"] = false,
            ["changes"] = changes,
        }, new[] { modRoot });
        File.Copy(Path.Combine(raw, "originals.json"), Path.Combine(output, "original-headers.json"), true);
        File.Copy(Path.Combine(raw, "verified.json"), Path.Combine(output, "patched-headers.json"), true);
        File.Copy(Path.Combine(modRoot, "INSTALL.txt"), Path.Combine(output, "INSTALL.txt"), true);
        try { Directory.Delete(work, true); } catch { }

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["version"] = "0.1.0",
            ["output"] = output,
            ["files"] = files,
        };
    }

    public static async Task<Dictionary<string, object?>> BuildArenaPackageAsync(
        string root, string game, string repak, string retoc)
    {
        var modRoot = Path.Combine(root, "mods", "features", "phantom-blade-training");
        var detectedBuildId = ManagerEngine.ReadInstalledGameBuildId(game)
            ?? throw new InvalidOperationException("Cannot identify the installed game build.");
        var loaderFiles = await ArenaLoaderPackage.ReadAsync(modRoot, detectedBuildId);
        await SyncArenaEncounterCatalogAsync(modRoot, detectedBuildId);
        await GenerateArenaResourcesHeaderAsync(modRoot);
        await BuildArenaNativeDllAsync(modRoot, game);
        await BuildArenaUiAssetsAsync(modRoot, repak, retoc);

        var stage = Path.Combine(modRoot, "dist", "native");
        using var nativeDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, ".local", "native-build", "receipt.json"), Encoding.UTF8));
        using var uiDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, ".local", "ui-build", "receipt.json"), Encoding.UTF8));
        var nativeDll = Path.Combine(modRoot, ".local", "native-build", "cmake", "Release", "main.dll");
        var dllSha = nativeDoc.RootElement.GetProperty("dllSha256").GetString()!;
        var loaderSha = nativeDoc.RootElement.GetProperty("loaderSha256").GetString()!;
        if (FileSafety.FileHash(nativeDll) != dllSha)
            throw new InvalidOperationException("Rebuild native DLL before packaging.");

        var targets = new List<(string Source, string Target)>
        {
            (nativeDll, "platform/steam/ue4ss/Mods/PhantomBladeArena/dlls/main.dll")
        };
        targets.AddRange(loaderFiles);
        foreach (var ext in new[] { "pak", "utoc", "ucas" })
        {
            targets.Add((
                Path.Combine(modRoot, ".local", "ui-build", "packed", $"zzz_PhantomBladeArena_P.{ext}"),
                $"ProjectPBZ/Content/Paks/~mods/zzz_PhantomBladeArena_P.{ext}"));
        }

        var files = new List<FileDigestEntry>();
        foreach (var (src, relTarget) in targets)
        {
            var dst = Path.Combine(stage, "payload", relTarget.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            await RuntimePackageBuilder.CopyFileAsync(src, dst);
            var fi = new FileInfo(dst);
            files.Add(new FileDigestEntry(relTarget, fi.Length, FileSafety.FileHash(dst)));
        }

        const string enableRel = "platform/steam/ue4ss/Mods/PhantomBladeArena/enabled.txt";
        var enablePath = Path.Combine(stage, "payload", enableRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(enablePath)!);
        await File.WriteAllBytesAsync(enablePath, Array.Empty<byte>());
        files.Add(new FileDigestEntry(enableRel, 0, FileSafety.BytesHash(Array.Empty<byte>())));

        FileSafety.WriteJsonAtomic(Path.Combine(stage, "manifest.json"), new Dictionary<string, object?>
        {
            ["id"] = "PhantomBladeArena",
            ["version"] = "0.1.0",
            ["kind"] = "native-menu-integration",
            ["loaderBundle"] = true,
            ["status"] = "build-26367565-single-encounter-awaiting-runtime-verification",
            ["gameBuild"] = detectedBuildId,
            ["loaderSha256"] = loaderSha,
            ["runtimeVerified"] = false,
            ["features"] = new Dictionary<string, string>
            {
                ["menu"] = "fluid-columns-wrap-controls-and-scrollable-settings-compiled",
                ["encounters"] = "chapter-ordered-catalog-awaiting-runtime-verification",
                ["entryLoadout"] = "owned-per-slot-custom-and-stage-copy-awaiting-runtime-verification",
                ["bossTuning"] = "difficulty-cycle-and-transient-attribute-scales-awaiting-runtime-verification",
                ["adaptiveDifficulty"] = "unavailable-awaiting-calibrated-growth-baseline",
                ["loader"] = "pinned-proxy-and-ue4ss-bundled",
                ["saveIsolation"] = "full-container-cache-restore-awaiting-runtime-verification",
                ["reset"] = "save-drain-and-ground-readiness-awaiting-runtime-verification",
                ["healthLock"] = "damage-verified-lethal-transitions-unverified",
                ["practiceTargets"] = "independent-stable-targets-current-and-draft-editors-awaiting-runtime-verification",
                ["practiceMotions"] = "catalog-motions-per-target-freeze-recovery-and-conversion-awaiting-runtime-verification",
                ["practiceStatistics"] = "per-target-received-and-converted-output-with-world-labels-awaiting-runtime-verification",
            },
            ["ui"] = JsonSerializer.Deserialize<object>(uiDoc.RootElement.GetRawText()),
            ["files"] = files,
        }, new[] { modRoot });

        return new Dictionary<string, object?>
        {
            ["id"] = "arena",
            ["version"] = "0.1.0",
            ["output"] = stage,
            ["files"] = files,
        };
    }

    private static async Task SyncArenaEncounterCatalogAsync(string modRoot, string buildId)
    {
        var chapterRules = new Dictionary<string, (int Order, int SubOrder, string ChapterKey, int BaseHp, int BaseWpn)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ZhuLinGuwu"] = (0, 0, "UI_LoadGame_08", 500, 0),
            ["XiGu"] = (1, 0, "Chapter_01", 550, 4),
            ["FuJiZhiLin"] = (2, 0, "Chapter_02", 650, 9),
            ["LuJiaCun"] = (3, 0, "Chapter_05", 700, 13),
            ["PangZhen"] = (4, 0, "Chapter_06", 750, 16),
            ["HouJie"] = (4, 1, "Chapter_06", 800, 18),
            ["ZhengDaoDaYing"] = (5, 0, "Chapter_03", 800, 21),
            ["DengDingShanDao"] = (6, 0, "Other_01-DDSD_Chapter_001", 850, 24),
            ["MeiGuHu"] = (7, 0, "Chapter_07", 850, 26),
            ["HuanShuShi"] = (7, 1, "Chapter_07", 850, 27),
            ["XiMeng"] = (10, 0, "Chapter_14", 900, 28),
            ["DuanHunYa"] = (12, 0, "Chapter_17", 900, 30),
            ["DuanHunShan"] = (12, 1, "Chapter_17", 900, 30),
        };

        using var assetsDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "data", "assets.json"), Encoding.UTF8));
        var mapByRow = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in assetsDoc.RootElement.GetProperty("maps").EnumerateArray())
        {
            var rowId = m.GetProperty("rowId").GetString();
            if (!string.IsNullOrEmpty(rowId)) mapByRow[rowId] = m.Clone();
        }

        var rawDir = Path.Combine(modRoot, ".local", "encounters-raw");
        var groundRecords = new List<JsonElement>();
        foreach (var rawFile in Directory.GetFiles(rawDir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            using var rawDoc = JsonDocument.Parse(await File.ReadAllTextAsync(rawFile, Encoding.UTF8));
            if (!rawDoc.RootElement.TryGetProperty("records", out var recs) || recs.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var r in recs.EnumerateArray())
            {
                if (r.TryGetProperty("type", out var t) && t.GetString() == "BP_AISpawner_C" &&
                    r.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() == 3)
                {
                    var z = pos[2].GetDouble();
                    if (Math.Abs(z) < 100000.0)
                        groundRecords.Add(r.Clone());
                }
            }
        }

        var matched = new List<(int ChapterOrder, int SubOrder, int BossOrder, string BossId, JsonElement Boss, JsonElement Record, string ChapterKey, int RecHp, int RecWpn)>();
        foreach (var b in assetsDoc.RootElement.GetProperty("bossForms").EnumerateArray())
        {
            var bossId = b.GetProperty("id").GetString()!;
            var classPath = b.GetProperty("classPath").GetString()!;
            var mapRowId = b.GetProperty("mapRowId").GetString() ?? "";
            var bossOrder = b.GetProperty("order").GetInt32();

            JsonElement? found = null;
            foreach (var r in groundRecords)
            {
                bool hit = false;
                if (r.TryGetProperty("bosses", out var bs) && bs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in bs.EnumerateArray())
                    {
                        if (string.Equals(item.GetString(), bossId, StringComparison.Ordinal)) { hit = true; break; }
                    }
                }
                if (!hit && r.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("Class", out var cls) && cls.ValueKind == JsonValueKind.Object &&
                    cls.TryGetProperty("AssetPathName", out var ap) &&
                    string.Equals(ap.GetString(), classPath, StringComparison.Ordinal))
                {
                    hit = true;
                }
                if (hit) { found = r; break; }
            }
            if (found == null) continue;

            (int Order, int SubOrder, string ChapterKey, int BaseHp, int BaseWpn) rule =
                chapterRules.TryGetValue(mapRowId, out var cr) ? cr : (99, 0, "Chapter_01", 700, 15);
            matched.Add((rule.Order, rule.SubOrder, bossOrder, bossId, b.Clone(), found.Value, rule.ChapterKey, rule.BaseHp, rule.BaseWpn));
        }

        var ordered = matched
            .OrderBy(x => x.ChapterOrder)
            .ThenBy(x => x.SubOrder)
            .ThenBy(x => x.BossOrder)
            .ThenBy(x => x.BossId, StringComparer.Ordinal)
            .Take(64)
            .ToList();

        var bossesOut = new List<Dictionary<string, object?>>();
        var scenesOut = new List<Dictionary<string, object?>>();
        var evidenceOut = new List<Dictionary<string, object?>>();

        foreach (var item in ordered)
        {
            var b = item.Boss;
            var r = item.Record;
            var bossId = item.BossId;
            var sceneId = "arena_" + bossId;
            var mapRowId = b.GetProperty("mapRowId").GetString() ?? "";
            var mapUmap = r.GetProperty("map").GetString()!;
            var levelPath = "/Game/" + mapUmap["ProjectPBZ/Content/".Length..^".umap".Length];
            var world = r.GetProperty("world").GetString()!;

            string regionNs = "Default";
            string regionKey = item.ChapterKey;
            string preview = "/Game/PBZContent/UI/InGame/SaveLoad/BellTexture/T_Map_XiGu.T_Map_XiGu";
            if (mapByRow.TryGetValue(mapRowId, out var mapElem))
            {
                if (mapElem.TryGetProperty("name", out var nObj) && nObj.ValueKind == JsonValueKind.Object)
                {
                    regionNs = nObj.GetProperty("namespace").GetString() ?? "Default";
                    regionKey = nObj.GetProperty("key").GetString() ?? regionKey;
                }
                if (mapElem.TryGetProperty("preview", out var prevElem) && prevElem.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(prevElem.GetString()))
                {
                    preview = prevElem.GetString()!;
                }
            }

            var posArr = r.GetProperty("position").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var actorName = r.GetProperty("name").GetString()!;
            string? spawnerId = null;
            if (r.TryGetProperty("properties", out var props) && props.TryGetProperty("SpawnerID", out var sid) && sid.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sid.GetString()))
            {
                spawnerId = sid.GetString()!;
            }

            var nameObj = b.GetProperty("name");
            bossesOut.Add(new Dictionary<string, object?>
            {
                ["id"] = bossId,
                ["classPath"] = b.GetProperty("classPath").GetString()!,
                ["name"] = new Dictionary<string, string>
                {
                    ["namespace"] = nameObj.GetProperty("namespace").GetString() ?? "Default",
                    ["key"] = nameObj.GetProperty("key").GetString()!,
                },
                ["chapter"] = new Dictionary<string, string>
                {
                    ["namespace"] = "Default",
                    ["key"] = item.ChapterKey,
                },
                ["chapterOrder"] = item.ChapterOrder,
                ["recommendedMaxHealth"] = item.RecHp,
                ["recommendedWeaponLevel"] = item.RecWpn,
                ["portrait"] = b.GetProperty("portrait").GetString()!,
                ["defaultSceneId"] = sceneId,
            });

            scenesOut.Add(new Dictionary<string, object?>
            {
                ["id"] = sceneId,
                ["bossId"] = bossId,
                ["level"] = levelPath,
                ["world"] = world,
                ["region"] = new Dictionary<string, string>
                {
                    ["namespace"] = regionNs,
                    ["key"] = regionKey,
                },
                ["position"] = posArr,
                ["preview"] = preview,
            });

            var ev = new Dictionary<string, object?>
            {
                ["bossId"] = bossId,
                ["source"] = r.GetProperty("package").GetString()!,
                ["actor"] = actorName,
            };
            if (!string.IsNullOrEmpty(spawnerId)) ev["spawner"] = spawnerId;
            ev["position"] = posArr;
            evidenceOut.Add(ev);
        }

        FileSafety.WriteJsonAtomic(Path.Combine(modRoot, "data", "encounter-candidates.json"), new Dictionary<string, object?>
        {
            ["status"] = "chapter-ordered-candidate-spawners-awaiting-full-runtime-verification",
            ["bosses"] = bossesOut,
            ["scenes"] = scenesOut,
        }, new[] { modRoot });

        FileSafety.WriteJsonAtomic(Path.Combine(modRoot, "data", "encounter-sources.json"), new Dictionary<string, object?>
        {
            ["buildId"] = buildId,
            ["evidence"] = evidenceOut,
        }, new[] { modRoot });
    }

    private static async Task GenerateArenaResourcesHeaderAsync(string modRoot)
    {
        using var candDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "data", "encounter-candidates.json"), Encoding.UTF8));
        using var srcDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "data", "encounter-sources.json"), Encoding.UTF8));
        using var zhDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "locales", "zh-Hans.json"), Encoding.UTF8));
        using var enDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "locales", "en.json"), Encoding.UTF8));
        using var equipmentDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "data", "equipment-catalog.json"), Encoding.UTF8));
        using var progressionDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "data", "progression-presets.draft.json"), Encoding.UTF8));
        using var practiceDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(modRoot, "data", "practice-catalog.json"), Encoding.UTF8));

        var bosses = candDoc.RootElement.GetProperty("bosses").EnumerateArray().ToList();
        var scenes = candDoc.RootElement.GetProperty("scenes").EnumerateArray().ToList();
        var evidenceByBoss = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var e in srcDoc.RootElement.GetProperty("evidence").EnumerateArray())
            evidenceByBoss[e.GetProperty("bossId").GetString()!] = e.Clone();

        var sceneIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < scenes.Count; i++)
            sceneIndexById[scenes[i].GetProperty("id").GetString()!] = i;

        static string CppWString(string value)
            => "L\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        static string CppString(string value)
            => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        var sb = new StringBuilder();
        sb.AppendLine("#pragma once");
        sb.AppendLine("#include \"Catalog.hpp\"");
        sb.AppendLine("#include <array>");
        sb.AppendLine("namespace arena::resources {");
        var initialBoss = bosses.FindIndex(b => b.GetProperty("id").GetString() == "BP_Monster_PengMeng_Axe");
        if (initialBoss < 0) throw new InvalidOperationException("The baseline Peng Meng encounter is missing.");
        sb.AppendLine($"inline constexpr size_t initial_boss = {initialBoss};");

        sb.AppendLine($"inline constexpr std::array<Scene, {scenes.Count}> scenes = {{{{");
        foreach (var s in scenes)
        {
            var sceneId = s.GetProperty("id").GetString()!;
            var bossId = s.GetProperty("bossId").GetString()!;
            var ev = evidenceByBoss[bossId];
            var spawner = ev.TryGetProperty("spawner", out var sp) && sp.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(sp.GetString())
                ? sp.GetString()!
                : ev.GetProperty("actor").GetString()!;
            var reg = s.GetProperty("region");
            var pos = s.GetProperty("position").EnumerateArray().Select(x => x.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var level = s.GetProperty("level").GetString()!;
            var preview = s.GetProperty("preview").GetString()!;
            sb.AppendLine($"    {{{CppString(sceneId)}, {CppWString(level)}, {CppWString(spawner)}, {{{CppWString(reg.GetProperty("namespace").GetString()!)}, {CppWString(reg.GetProperty("key").GetString()!)} }}, {{{pos[0]}, {pos[1]}, {pos[2]}}}, {CppWString(preview)}}},");
        }
        sb.AppendLine("}};");

        sb.AppendLine($"inline constexpr std::array<Boss, {bosses.Count}> bosses = {{{{");
        foreach (var b in bosses)
        {
            var bossId = b.GetProperty("id").GetString()!;
            var name = b.GetProperty("name");
            var chap = b.GetProperty("chapter");
            var cls = b.GetProperty("classPath").GetString()!;
            var portrait = b.GetProperty("portrait").GetString()!;
            var defScene = sceneIndexById[b.GetProperty("defaultSceneId").GetString()!];
            var recHp = b.GetProperty("recommendedMaxHealth").GetInt32();
            var recWpn = b.GetProperty("recommendedWeaponLevel").GetInt32();
            sb.AppendLine($"    {{{CppString(bossId)}, {CppWString(cls)}, {{{CppWString(name.GetProperty("namespace").GetString()!)}, {CppWString(name.GetProperty("key").GetString()!)} }}, {{{CppWString(chap.GetProperty("namespace").GetString()!)}, {CppWString(chap.GetProperty("key").GetString()!)} }}, {CppWString(portrait)}, {defScene}, {recHp}, {recWpn}}},");
        }
        sb.AppendLine("}};");

        var equipment = equipmentDoc.RootElement.GetProperty("weapons").EnumerateArray().ToList();
        sb.AppendLine($"inline constexpr std::array<Equipment, {equipment.Count}> equipment = {{{{");
        foreach (var item in equipment)
        {
            var name = item.GetProperty("name");
            var main = item.GetProperty("kind").GetString() == "main" ? "true" : "false";
            sb.AppendLine($"    {{{CppString(item.GetProperty("id").GetString()!)}, {main}, {{{CppWString(name.GetProperty("namespace").GetString()!)}, {CppWString(name.GetProperty("key").GetString()!)} }}, {CppWString(item.GetProperty("icon").GetString()!)}}},");
        }
        sb.AppendLine("}};");

        // Drafts are display-only. They cannot enable a runtime loadout via data flags.
        var presets = progressionDoc.RootElement.GetProperty("profiles").EnumerateArray().ToList();
        sb.AppendLine($"inline constexpr std::array<StagePreset, {presets.Count}> stages = {{{{");
        foreach (var preset in presets)
        {
            var slots = preset.GetProperty("player").GetProperty("equipment").EnumerateArray().Select(slot =>
            {
                var id = slot.GetProperty("itemId").GetString() ?? "";
                var level = slot.GetProperty("level").ValueKind == JsonValueKind.Null ? 1 : slot.GetProperty("level").GetInt32();
                return $"{{{CppString(id)}, {level}}}";
            }).ToArray();
            if (slots.Length != 4) throw new InvalidDataException("Arena stage must define four weapon slots.");
            sb.AppendLine($"    {{{CppString(preset.GetProperty("id").GetString()!)}, {CppString(preset.GetProperty("labelKey").GetString()!)}, {{{{{string.Join(", ", slots)}}}}}}},");
        }
        sb.AppendLine("}};");
        var availableBossIds = bosses.Select(b => b.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var bindings = presets.SelectMany((preset, index) => preset.GetProperty("encounterBindings").GetProperty("bossFormIds")
            .EnumerateArray().Select(boss => (Id: boss.GetString()!, Stage: index)))
            .Where(binding => availableBossIds.Contains(binding.Id)).ToList();
        sb.AppendLine($"inline constexpr std::array<StageBinding, {bindings.Count}> stage_bindings = {{{{");
        foreach (var binding in bindings)
            sb.AppendLine($"    {{{CppString(binding.Id)}, {binding.Stage}}},");
        sb.AppendLine("}};");

        var practiceKinds = practiceDoc.RootElement.GetProperty("kinds").EnumerateArray().ToList();
        var practiceMotions = new List<JsonElement>();
        var kindIds = new HashSet<string>(StringComparer.Ordinal);
        void ValidateLabel(JsonElement item)
        {
            var key = item.GetProperty("labelKey").GetString()!;
            if (!zhDoc.RootElement.TryGetProperty(key, out _) || !enDoc.RootElement.TryGetProperty(key, out _))
                throw new InvalidDataException($"Missing practice label: {key}");
        }
        sb.AppendLine($"inline constexpr std::array<PracticeKind, {practiceKinds.Count}> practice_kinds = {{{{");
        foreach (var kind in practiceKinds)
        {
            var id = kind.GetProperty("id").GetString()!;
            if (!kindIds.Add(id)) throw new InvalidDataException($"Duplicate practice kind: {id}");
            ValidateLabel(kind);
            var motions = kind.GetProperty("motions").EnumerateArray().ToList();
            if (motions.Count == 0 || motions.Select(m => m.GetProperty("id").GetString()).Distinct().Count() != motions.Count
                || !motions.Any(m => m.GetProperty("id").GetString() == "idle"))
                throw new InvalidDataException($"Invalid practice motions: {id}");
            foreach (var motion in motions) ValidateLabel(motion);
            var control = kind.GetProperty("luteControllable").GetBoolean() ? "true" : "false";
            sb.AppendLine($"    {{{CppString(id)}, {CppString(kind.GetProperty("labelKey").GetString()!)}, {CppWString(kind.GetProperty("actorClass").GetString()!)}, {control}, {practiceMotions.Count}, {motions.Count}}},");
            practiceMotions.AddRange(motions);
        }
        sb.AppendLine("}};");
        if (!kindIds.Contains("sword")) throw new InvalidDataException("Default practice kind is missing.");
        sb.AppendLine($"inline constexpr std::array<PracticeMotion, {practiceMotions.Count}> practice_motions = {{{{");
        foreach (var motion in practiceMotions)
            sb.AppendLine($"    {{{CppString(motion.GetProperty("id").GetString()!)}, {CppString(motion.GetProperty("labelKey").GetString()!)}, {CppWString(motion.GetProperty("sequence").GetString()!)}}},");
        sb.AppendLine("}};");

        void AppendLocale(string arrayName, JsonElement obj)
        {
            var props = obj.EnumerateObject().ToList();
            sb.AppendLine($"inline constexpr std::array<Message, {props.Count}> {arrayName} = {{{{");
            foreach (var p in props)
                sb.AppendLine($"    {{{CppString(p.Name)}, {CppWString(p.Value.GetString() ?? "")}}},");
            sb.AppendLine("}};");
        }

        AppendLocale("zh_hans", zhDoc.RootElement);
        AppendLocale("en", enDoc.RootElement);
        sb.AppendLine("}");

        var includeDir = Path.Combine(modRoot, ".local", "native-build", "include");
        Directory.CreateDirectory(includeDir);
        await File.WriteAllTextAsync(Path.Combine(includeDir, "Resources.hpp"), sb.ToString(), new UTF8Encoding(false));
    }

    private static async Task BuildArenaNativeDllAsync(string modRoot, string game)
    {
        var nativeBuildDir = Path.Combine(modRoot, ".local", "native-build");
        var cmakeDir = Path.Combine(nativeBuildDir, "cmake");
        var includeDir = Path.Combine(nativeBuildDir, "include");
        var ue4ssLib = Path.Combine(nativeBuildDir, "UE4SS.lib");
        var cmakeExe = BuildTools.CMake();

        var cacheFile = Path.Combine(cmakeDir, "CMakeCache.txt");
        if (File.Exists(cacheFile))
        {
            var cacheText = await File.ReadAllTextAsync(cacheFile, Encoding.UTF8);
            var normalizedInclude = includeDir.Replace('\\', '/');
            if (!cacheText.Replace('\\', '/').Contains(normalizedInclude, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(cmakeDir, true);
            }
        }

        if (!File.Exists(cacheFile))
        {
            Directory.CreateDirectory(cmakeDir);
            await FileSafety.RunToolCheckedAsync(cmakeExe, new[]
            {
                "-S", Path.Combine(modRoot, "native"),
                "-B", cmakeDir,
                "-G", "Visual Studio 17 2022",
                "-A", "x64",
                $"-DARENA_GENERATED_INCLUDE={includeDir}",
                $"-DARENA_UE4SS_LIBRARY={ue4ssLib}",
                "-DARENA_DIAGNOSTICS=ON"
            });
        }

        await FileSafety.RunToolCheckedAsync(cmakeExe, new[]
        {
            "--build", cmakeDir, "--config", "Release", "--target", "PhantomBladeArena"
        });

        var dllPath = Path.Combine(cmakeDir, "Release", "main.dll");
        var loaderPath = Path.Combine(modRoot, ".local", "loader", "ue4ss", "UE4SS.dll");
        var loaderSha = await RuntimePackageBuilder.HashAsync(loaderPath);

        FileSafety.WriteJsonAtomic(Path.Combine(nativeBuildDir, "receipt.json"), new Dictionary<string, object?>
        {
            ["kind"] = "native-menu-integration",
            ["diagnostics"] = true,
            ["runtimeVerified"] = false,
            ["combatAvailable"] = false,
            ["loaderSha256"] = loaderSha,
            ["dllSha256"] = FileSafety.FileHash(dllPath),
            ["builtAt"] = DateTimeOffset.UtcNow.ToString("O"),
        }, new[] { modRoot });
    }

    private static async Task BuildArenaUiAssetsAsync(string modRoot, string repak, string retoc)
    {
        var uiDir = Path.Combine(modRoot, "ui");
        var uproject = Path.Combine(uiDir, "ProjectPBZ.uproject");
        var uiBuildDir = Path.Combine(modRoot, ".local", "ui-build");
        Directory.CreateDirectory(uiBuildDir);

        var cookedDir = Path.Combine(uiDir, "Saved", "Cooked", "Windows", "ProjectPBZ", "Content", "PhantomBladeArena");
        var requiredCooked = new[] { "W_Arena.uasset", "W_Arena.uexp", "W_ArenaStats.uasset", "W_ArenaStats.uexp", "W_ArenaTargetLabel.uasset", "W_ArenaTargetLabel.uexp", "GE_HealthLock.uasset", "GE_HealthLock.uexp", "GE_MaxHealth.uasset", "GE_MaxHealth.uexp", "GE_BossTuning.uasset", "GE_BossTuning.uexp" };
        var sourceFiles = new[] { "Source", "Config" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(uiDir, folder), "*", SearchOption.AllDirectories))
            .Append(uproject).Where(path => !path.Contains("trash", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        var sourceHashes = new List<string>();
        foreach (var file in sourceFiles)
            sourceHashes.Add(Path.GetRelativePath(uiDir, file) + "\t" + await RuntimePackageBuilder.HashAsync(file));
        var sourceDigest = FileSafety.StringHash(string.Join("\n", sourceHashes));
        var sourceStamp = Path.Combine(uiBuildDir, "sources.sha256");
        if (!requiredCooked.All(f => File.Exists(Path.Combine(cookedDir, f)))
            || !File.Exists(sourceStamp) || await File.ReadAllTextAsync(sourceStamp) != sourceDigest)
        {
            var engine = BuildTools.UnrealEngine();
            var buildBat = Path.Combine(engine, "Engine", "Build", "BatchFiles", "Build.bat");
            var editorCmd = Path.Combine(engine, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");

            await FileSafety.RunToolCheckedAsync(buildBat, new[]
            {
                "ProjectPBZEditor", "Win64", "Development",
                $"-Project={uproject}", "-WaitMutex", "-NoHotReloadFromIDE"
            });

            var genLog = Path.Combine(uiBuildDir, "generate-engine.log");
            await FileSafety.RunToolCheckedAsync(editorCmd, new[]
            {
                uproject, "-unattended", "-nop4", "-nullrhi", "-nosplash", "-nosound",
                "-run=ArenaAssets", $"-abslog={genLog}"
            });

            var cookLog = Path.Combine(uiBuildDir, "cook-engine.log");
            await FileSafety.RunToolCheckedAsync(editorCmd, new[]
            {
                uproject, "-unattended", "-nop4", "-nullrhi", "-nosplash", "-nosound",
                "-run=Cook", "-TargetPlatform=Windows",
                "-Package=/Game/PhantomBladeArena/W_Arena+/Game/PhantomBladeArena/W_ArenaStats+/Game/PhantomBladeArena/W_ArenaTargetLabel+/Game/PhantomBladeArena/GE_HealthLock+/Game/PhantomBladeArena/GE_MaxHealth+/Game/PhantomBladeArena/GE_BossTuning",
                "-NoDefaultMaps", $"-abslog={cookLog}"
            });
            await File.WriteAllTextAsync(sourceStamp, sourceDigest);
        }

        var inputArenaDir = Path.Combine(uiBuildDir, "input", "ProjectPBZ", "Content", "PhantomBladeArena");
        if (Directory.Exists(Path.Combine(uiBuildDir, "input")))
            Directory.Delete(Path.Combine(uiBuildDir, "input"), true);
        Directory.CreateDirectory(inputArenaDir);

        var assetEntries = new List<Dictionary<string, object?>>();
        foreach (var fileName in requiredCooked)
        {
            var src = Path.Combine(cookedDir, fileName);
            var dst = Path.Combine(inputArenaDir, fileName);
            File.Copy(src, dst, true);
            var fi = new FileInfo(dst);
            assetEntries.Add(new Dictionary<string, object?>
            {
                ["file"] = fileName,
                ["bytes"] = fi.Length,
                ["sha256"] = FileSafety.FileHash(dst),
            });
        }

        var packedDir = Path.Combine(uiBuildDir, "packed");
        if (Directory.Exists(packedDir)) Directory.Delete(packedDir, true);
        Directory.CreateDirectory(packedDir);
        var utocPath = Path.Combine(packedDir, "zzz_PhantomBladeArena_P.utoc");
        var pakPath = Path.Combine(packedDir, "zzz_PhantomBladeArena_P.pak");
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "to-zen", "--version", "UE5_7", Path.Combine(uiBuildDir, "input"), utocPath });
        await CompressIoStoreContainerAsync(utocPath);
        await FileSafety.RunToolCheckedAsync(retoc, new[] { "verify", utocPath });

        var emptyDir = Path.Combine(uiBuildDir, "empty-pak");
        Directory.CreateDirectory(emptyDir);
        await FileSafety.RunToolCheckedAsync(repak, new[] { "pack", "-q", "--version", "V11", "--compression", "Zlib", "--mount-point", "../../../", emptyDir, pakPath });
        try { Directory.Delete(emptyDir, true); } catch { }

        FileSafety.WriteJsonAtomic(Path.Combine(uiBuildDir, "receipt.json"), new Dictionary<string, object?>
        {
            ["status"] = "cooked-not-game-verified",
            ["engine"] = "5.6",
            ["containerVersion"] = "UE5_7",
            ["assets"] = assetEntries,
            ["runtimeModulesIncluded"] = false,
        }, new[] { modRoot });
    }

    public static async Task<Dictionary<string, object?>> BuildChaosResetManagerAsync(string root, string toolkit)
    {
        var modRoot = Path.Combine(root, "mods", "remedies", "phantom-blade-chaos-reset");
        var output = Path.Combine(modRoot, "dist", "PhantomBladeQuestManager-current");
        Directory.CreateDirectory(output);
        await FileSafety.RunToolCheckedAsync("dotnet", new[]
        {
            "publish", Path.Combine(modRoot, "manager", "PhantomBladeQuestManager.csproj"),
            "-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true", "-p:PublishTrimmed=true",
            "-p:_SuppressWPFTrimError=true", "-p:TrimMode=partial",
            "-p:DebugType=none", "-p:DebugSymbols=false",
            "-o", output
        });
        File.Copy(Path.Combine(toolkit, "oo2core_9_win64.dll"), Path.Combine(output, "oo2core_9_win64.dll"), true);
        File.Copy(Path.Combine(modRoot, "MANAGER.txt"), Path.Combine(output, "使用说明.txt"), true);
        FileSafety.WriteJsonAtomic(Path.Combine(output, "manager.json"), new Dictionary<string, object?>
        {
            ["id"] = "PhantomBladeQuestManager",
            ["version"] = "0.1.0",
            ["language"] = "zh-CN",
            ["buildId"] = "26367565",
            ["saveFormat"] = "PBZS v3",
            ["runtimeVerified"] = false,
            ["resetMode"] = "new-manual-slot-with-backup",
        }, new[] { modRoot });

        return new Dictionary<string, object?>
        {
            ["id"] = "chaos-reset",
            ["version"] = "0.1.0",
            ["output"] = output,
        };
    }

    public static async Task<Dictionary<string, object?>> BuildMapRuntimeAsync(
        string root, string game, string repak, string retoc, string key)
    {
        var modRoot = Path.Combine(root, "mods", "features", "phantom-blade-map");
        const string version = "0.4.0";
        const string mapPath = "ProjectPBZ/Content/JavaScript/UI/Widget/Map.js";
        const string anchor = "exports.MapInterface = MapInterface;";
        const string name = "zzz_PhantomBladeMap_P";

        var sourcePak = Path.Combine(game, "ProjectPBZ", "Content", "Paks", "ProjectPBZ-Windows.pak");
        var source = await FileSafety.RunToolCheckedAsync(repak, new[] { "-a", key, "get", sourcePak, mapPath });
        var sourceText = Encoding.UTF8.GetString(source);
        var mapSha = FileSafety.BytesHash(source);
        // Reviewed script variants: 26367565 removes source maps and changes
        // function-export placement; the map/fog behavior is unchanged.
        var (buildId, fogModeSha) = mapSha switch
        {
            "b54c922d38b573f97fa3ce97322441812293d270a0018a9c6d45d5cf61a92c70" =>
                ("26266538", "4145b0a1de7a49af153273cb20a5ae5a50af28df3c28b0e103c2e855b1bcf760"),
            "44e6a10741403af0cc5dee63ec816dd2096a71d08668ec8f864994ceb945dc70" =>
                ("26367565", "06bb8a6e6296a93d7eab2b970452fe5bf84aded2d786790753df0e4008206788"),
            _ => throw new InvalidOperationException("Unsupported original Map.js; review this game build before patching")
        };
        if (sourceText.Split(new[] { anchor }, StringSplitOptions.None).Length != 2)
            throw new InvalidOperationException("Unsupported original Map.js; review this game build before patching");
        var fogMode = await FileSafety.RunToolCheckedAsync(repak, new[] { "-a", key, "get", sourcePak, "ProjectPBZ/Content/JavaScript/Library/MergedFogLayout.js" });
        if (FileSafety.BytesHash(fogMode) != fogModeSha)
            throw new InvalidOperationException("Native fog mode changed; rebuild the exploration layout for this game");

        RuntimePackageBuilder.RequireCurrentBuild(game);
        var workspace = Path.Combine(modRoot, ".local-runtime");
        Directory.CreateDirectory(workspace);
        var temp = Path.Combine(workspace, $"puerts-build-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        var stage = Path.Combine(temp, "stage");
        var packageDir = Path.Combine(temp, "package");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(packageDir);

        try
        {
            var modules = Path.Combine(packageDir, "Scripts", "PhantomBladeMap");
            var legacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "NavSurvey.js", "Survey.js", "SurveyStore.js", "SurveyCanvas.js",
                "HudDirection.js", "OfflineRoute.js", "FloorOverlay.js", "corridors"
            };
            await RuntimePackageBuilder.CopyScriptsAsync(Path.Combine(modRoot, "puerts", "PhantomBladeMap"), modules, legacy);

            var receiptPath = Path.Combine(workspace, "atlas-cook-v7", "receipt.json");
            using var receiptDoc = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, Encoding.UTF8));
            var atlasDir = Path.Combine(workspace, "atlas-v7");
            await ValidateAtlasCookAsync(atlasDir, receiptDoc.RootElement);
            var offline = Path.Combine(modules, "offline");
            Directory.CreateDirectory(offline);
            foreach (var f in Directory.GetFiles(atlasDir, "*.js"))
            {
                var fileName = Path.GetFileName(f);
                var dstJs = Path.Combine(offline, fileName);
                if (string.Equals(fileName, "manifest.js", StringComparison.OrdinalIgnoreCase))
                    await RuntimePackageBuilder.CopyFileAsync(f, dstJs);
                else
                    await File.WriteAllTextAsync(dstJs, StripUnusedOfflineSceneFields(await File.ReadAllTextAsync(f, Encoding.UTF8)), new UTF8Encoding(false));
            }
            await CopySurfaceLookupAsync(atlasDir, Path.Combine(workspace, "surfaces-v2"), Path.Combine(modules, "surfaces"));

            var cooked = Path.Combine(workspace, "atlas-cook-v7", Path.GetFileName(receiptDoc.RootElement.GetProperty("payload").GetString()!));
            var destination = Path.Combine(stage, "ProjectPBZ", "Content", "PhantomBladeMap", "Atlas");
            await Parallel.ForEachAsync(Directory.EnumerateFiles(cooked, "*", SearchOption.AllDirectories),
                new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (tex, _) =>
            {
                var ext = Path.GetExtension(tex);
                if (ext is ".uasset" or ".uexp" or ".ubulk")
                {
                    var rel = Path.GetRelativePath(cooked, tex);
                    var dstTex = Path.Combine(destination, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dstTex)!);
                    await RuntimePackageBuilder.CopyFileAsync(tex, dstTex);
                }
            });

            var atlas = Path.Combine(modules, "atlas");
            Directory.CreateDirectory(atlas);
            var regionsFile = Path.Combine(modRoot, "PhantomBladeMap", "Data", "regions.json");
            if (File.Exists(regionsFile))
            {
                using var regionsDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(regionsFile));
                await File.WriteAllTextAsync(
                    Path.Combine(atlas, "regions.js"),
                    "\"use strict\";\nmodule.exports=" + JsonSerializer.Serialize(regionsDoc.RootElement) + ";\n",
                    new UTF8Encoding(false));
            }

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var f in Directory.EnumerateFiles(modules, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var rel = Path.GetRelativePath(modules, f).Replace('\\', '/');
                digest.AppendData(Encoding.UTF8.GetBytes(rel + "\0"));
                digest.AppendData(Convert.FromHexString(await RuntimePackageBuilder.HashAsync(f)));
            }
            var revision = Convert.ToHexStringLower(digest.GetHashAndReset());
            var builtAtUtc = DateTimeOffset.UtcNow.ToString("O");
            var buildInfo = JsonSerializer.Serialize(new { version, revision, builtAtUtc });
            await File.WriteAllTextAsync(Path.Combine(modules, "BuildInfo.js"), $"\"use strict\";\nmodule.exports = {buildInfo};\n", new UTF8Encoding(false));

            var payload = Path.Combine(packageDir, "Paks");
            Directory.CreateDirectory(payload);
            var utocPath = Path.Combine(payload, name + ".utoc");
            var pakPath = Path.Combine(payload, name + ".pak");
            await FileSafety.RunToolCheckedAsync(retoc, new[] { "to-zen", "--version", "UE5_7", stage, utocPath });
            await CompressIoStoreContainerAsync(utocPath);
            await FileSafety.RunToolCheckedAsync(retoc, new[] { "verify", utocPath });

            var loose = Path.Combine(temp, "loose");
            Directory.CreateDirectory(loose);
            // Keep a content entry in the pak alongside the IoStore texture assets.
            var atlasIndex = Path.Combine(loose, "ProjectPBZ", "Content", "PhantomBladeMap", "atlas.json");
            Directory.CreateDirectory(Path.GetDirectoryName(atlasIndex)!);
            await File.WriteAllTextAsync(atlasIndex, JsonSerializer.Serialize(new { schema = 1, revision, textures = receiptDoc.RootElement.GetProperty("textures").GetInt32() }));
            await FileSafety.RunToolCheckedAsync(repak, new[] { "pack", "-q", "--version", "V11", "--compression", "Zlib", "--mount-point", "../../../", loose, pakPath });

            await RuntimePackageBuilder.AddLoaderAsync(root, packageDir);
            File.Copy(Path.Combine(modRoot, "puerts", "PhantomBladeMap", "provider", "LICENSE"), Path.Combine(packageDir, "PbzModConfig-LICENSE"), true);

            var existingDist = Path.Combine(modRoot, "dist", "runtime");
            foreach (var metaFile in new[] { "CollectionCoverage.json", "TraversalCoverage.json" })
            {
                var srcMeta = Path.Combine(modRoot, "dist", "puerts", metaFile);
                if (File.Exists(srcMeta))
                    File.Copy(srcMeta, Path.Combine(packageDir, metaFile), true);
            }

            var files = await RuntimePackageBuilder.InventoryAsync(packageDir);

            var editor = receiptDoc.RootElement.GetProperty("editor");
            var editorVer = $"{editor.GetProperty("MajorVersion").GetInt32()}.{editor.GetProperty("MinorVersion").GetInt32()}.{editor.GetProperty("PatchVersion").GetInt32()}";
            var manifest = new Dictionary<string, object?>
            {
                ["schema"] = 1,
                ["id"] = "map",
                ["version"] = version,
                ["backend"] = "nativeRuntime",
                ["runtimeVersion"] = RuntimePackageBuilder.RuntimeVersion,
                ["buildId"] = buildId,
                ["originalMapSha256"] = mapSha,
                ["nativeDll"] = true,
                ["runtimeVerified"] = false,
                ["pendingValidation"] = new[] { "floorBells", "questChains", "questGuidance", "questMapLinking", "conditionHints", "questPrerequisiteLinks", "questTimeline", "mapStateRefresh", "floorBackdrop" },
                ["onlyWhileMapOpen"] = false,
                ["discoveryOnlyWhileMapOpen"] = true,
                ["activation"] = "first-native-map-open",
                ["optionalMinimap"] = true,
                ["optionalAutoCollect"] = true,
                ["runtimeRoutes"] = false,
                ["hudWithoutMapActivation"] = false,
                ["sourceRevision"] = revision,
                ["builtAtUtc"] = builtAtUtc,
                ["optionalControlsApi"] = 1,
                ["textureCook"] = new Dictionary<string, object?>
                {
                    ["editorVersion"] = editorVer,
                    ["atlasRevision"] = receiptDoc.RootElement.GetProperty("atlasRevision").GetString(),
                    ["textures"] = receiptDoc.RootElement.GetProperty("textures").GetInt32(),
                    ["containerVersion"] = "UE5_7",
                    ["runtimeValidated"] = receiptDoc.RootElement.GetProperty("runtimeValidated").GetBoolean(),
                },
                ["files"] = files,
            };
            FileSafety.WriteJsonAtomic(Path.Combine(packageDir, "manifest.json"), manifest, new[] { modRoot });

            FileSafety.RemoveTreeIfExists(existingDist, new[] { Path.Combine(modRoot, "dist") });
            Directory.Move(FileSafety.AllowedPath(packageDir, new[] { workspace }), FileSafety.AllowedPath(existingDist, new[] { modRoot }));

            return new Dictionary<string, object?>
            {
                ["id"] = "map",
                ["version"] = version,
                ["output"] = existingDist,
                ["files"] = files.Count,
            };
        }
        finally
        {
            FileSafety.RemoveTreeIfExists(temp, new[] { workspace });
        }
    }

    private static async Task ValidateAtlasCookAsync(string atlas, JsonElement receipt)
    {
        if (await RuntimePackageBuilder.HashAsync(Path.Combine(atlas, "index.js")) != receipt.GetProperty("indexSha256").GetString())
            throw new InvalidDataException("Atlas cook index does not match the map data");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string Relative(string file) => Path.GetRelativePath(atlas, file).Replace('\\', '/');
        var tiles = Directory.EnumerateFiles(Path.Combine(atlas, "tiles"), "*.png", SearchOption.AllDirectories).OrderBy(Relative, StringComparer.Ordinal).ToArray();
        foreach (var file in Directory.EnumerateFiles(atlas, "*.js").OrderBy(Relative, StringComparer.Ordinal).Concat(tiles))
        {
            digest.AppendData(Encoding.UTF8.GetBytes(Relative(file) + "\0"));
            digest.AppendData(Convert.FromHexString(await RuntimePackageBuilder.HashAsync(file)));
        }
        if (Convert.ToHexStringLower(digest.GetHashAndReset()) != receipt.GetProperty("atlasRevision").GetString()
            || tiles.Length != receipt.GetProperty("textures").GetInt32())
            throw new InvalidDataException("Atlas cook geometry does not match the map data");
    }

    private static async Task CopySurfaceLookupAsync(string atlas, string source, string destination)
    {
        static JsonDocument ReadModule(string content)
        {
            const string marker = "module.exports=";
            var start = content.IndexOf(marker, StringComparison.Ordinal);
            var end = content.LastIndexOf(';');
            if (start < 0 || end <= start + marker.Length) throw new InvalidDataException("Invalid surface lookup module");
            return JsonDocument.Parse(content[(start + marker.Length)..end]);
        }

        using var index = ReadModule(await File.ReadAllTextAsync(Path.Combine(atlas, "index.js")));
        foreach (var entry in index.RootElement.GetProperty("worlds").EnumerateArray())
        {
            var world = entry.GetProperty("world").GetString()!;
            if (Path.GetFileName(world) != world) throw new InvalidDataException("Invalid surface world");
            using var map = ReadModule(await File.ReadAllTextAsync(Path.Combine(atlas, world + ".js")));
            var module = Path.Combine(source, world + ".js");
            using var surfaces = ReadModule(await File.ReadAllTextAsync(module));
            var data = surfaces.RootElement;
            if (data.GetProperty("schema").GetInt32() != 1 ||
                data.GetProperty("navigationRevision").GetString() != map.RootElement.GetProperty("navigationRevision").GetString())
                throw new InvalidDataException("Surface lookup revision mismatch: " + world);
            var tiles = data.GetProperty("tiles").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            if (tiles.Length % 2 != 0) throw new InvalidDataException("Invalid surface tile index: " + world);
            Directory.CreateDirectory(Path.Combine(destination, world));
            await RuntimePackageBuilder.CopyFileAsync(module, Path.Combine(destination, world + ".js"));
            await Parallel.ForEachAsync(Enumerable.Range(0, tiles.Length / 2), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, _) =>
            {
                var name = $"{tiles[i * 2]}_{tiles[i * 2 + 1]}.js";
                await RuntimePackageBuilder.CopyFileAsync(Path.Combine(source, world, name), Path.Combine(destination, world, name));
            });
        }
    }

    private static string StripUnusedOfflineSceneFields(string content)
    {
        const string prefix = "\"use strict\";\nmodule.exports=";
        var idx = content.IndexOf("module.exports=", StringComparison.Ordinal);
        if (idx < 0) return content;
        var jsonStart = idx + "module.exports=".Length;
        var jsonEnd = content.LastIndexOf(';');
        if (jsonEnd <= jsonStart) return content;
        var node = JsonNode.Parse(content.AsSpan(jsonStart, jsonEnd - jsonStart).ToString()) as JsonObject;
        if (node == null) return content;
        node.Remove("edges");
        node.Remove("portals");
        if (node["floors"] is JsonArray floors)
        {
            foreach (var fl in floors.OfType<JsonObject>())
            {
                fl.Remove("edges");
                fl.Remove("portals");
            }
        }
        return prefix + node.ToJsonString() + ";\n";
    }

    public static async Task CompressIoStoreContainerAsync(string utocPath)
    {
        var ucasPath = Path.ChangeExtension(utocPath, ".ucas");
        if (!File.Exists(utocPath) || !File.Exists(ucasPath)) return;
        var utoc = await File.ReadAllBytesAsync(utocPath);
        if (utoc.Length < 144) return;

        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(utoc.AsSpan(24, 4));
        var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(utoc.AsSpan(28, 4));
        var blockEntrySize = BinaryPrimitives.ReadUInt32LittleEndian(utoc.AsSpan(32, 4));
        var methodCount = BinaryPrimitives.ReadUInt32LittleEndian(utoc.AsSpan(36, 4));
        var methodLen = BinaryPrimitives.ReadUInt32LittleEndian(utoc.AsSpan(40, 4));
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(utoc.AsSpan(44, 4));
        var dirIndexSize = BinaryPrimitives.ReadUInt32LittleEndian(utoc.AsSpan(48, 4));
        var perfectSeedCount = BinaryPrimitives.ReadUInt32LittleEndian(utoc.AsSpan(84, 4));
        var withoutPerfectCount = BinaryPrimitives.ReadUInt32LittleEndian(utoc.AsSpan(96, 4));

        if (blockCount == 0 || blockEntrySize != 12 || methodCount != 0 || blockSize == 0) return;

        var offsetLengthsOffset = 144 + (int)(entryCount * 12);
        var blocksOffset = offsetLengthsOffset + (int)(entryCount * 10) + (int)(perfectSeedCount * 4) + (int)(withoutPerfectCount * 4);
        var methodsOffset = blocksOffset + (int)(blockCount * 12);
        var metasOffsetOld = methodsOffset + (int)dirIndexSize;
        if (metasOffsetOld + (int)(entryCount * 24) != utoc.Length) return;

        // FFileIoStoreReader reads the container header directly, without block
        // decompression. Keep all its blocks contiguous and uncompressed.
        var rawBlocks = new bool[blockCount];
        for (int e = 0; e < entryCount; e++)
        {
            var chunkType = utoc[144 + e * 12 + 11];
            // Memory-mapped chunks have alignment requirements this repacker
            // does not change; retain their original container instead.
            if (chunkType == 4) return;
            if (chunkType != 6) continue;
            var ol = utoc.AsSpan(offsetLengthsOffset + e * 10, 10);
            var start = ((ulong)ol[0] << 32) | ((ulong)ol[1] << 24) | ((ulong)ol[2] << 16) | ((ulong)ol[3] << 8) | ol[4];
            var length = ((ulong)ol[5] << 32) | ((ulong)ol[6] << 24) | ((ulong)ol[7] << 16) | ((ulong)ol[8] << 8) | ol[9];
            if (length == 0) continue;
            for (var b = start / blockSize; b <= (start + length - 1) / blockSize; b++) rawBlocks[b] = true;
        }

        var newBlocks = new byte[blockCount * 12];
        var blockMethods = new byte[blockCount];
        var tempUcasPath = ucasPath + ".tmp";

        await using (var input = new FileStream(ucasPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var outUcas = new FileStream(tempUcasPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            var block = new byte[blockSize];
            var padding = new byte[15];
            for (int i = 0; i < blockCount; i++)
            {
                var blk = utoc.AsSpan(blocksOffset + i * 12, 12);
                var raw0 = BinaryPrimitives.ReadUInt64LittleEndian(blk[..8]);
                var offset = raw0 & 0xFFFFFFFFFFUL;
                var raw1 = BinaryPrimitives.ReadUInt32LittleEndian(blk.Slice(8, 4));
                var uncompSize = raw1 & 0xFFFFFFU;
                input.Position = (long)offset;
                await input.ReadExactlyAsync(block.AsMemory(0, (int)uncompSize));

                byte methodIdx = 0;
                ReadOnlyMemory<byte> toWrite = block.AsMemory(0, (int)uncompSize);
                if (!rawBlocks[i] && uncompSize > 64)
                {
                    using var ms = new MemoryStream();
                    using (var zlib = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                    {
                        zlib.Write(block, 0, (int)uncompSize);
                    }
                    var compressed = ms.ToArray();
                    if (compressed.Length < (int)uncompSize)
                    {
                        toWrite = compressed;
                        methodIdx = 1;
                    }
                }

                blockMethods[i] = methodIdx;
                var newOffset = (ulong)outUcas.Position;
                await outUcas.WriteAsync(toWrite);
                var pad = (16 - (outUcas.Position % 16)) % 16;
                if (pad > 0) await outUcas.WriteAsync(padding.AsMemory(0, (int)pad));

                var newCompSize = (ulong)toWrite.Length;
                var newRaw0 = (newOffset & 0xFFFFFFFFFFUL) | ((newCompSize & 0xFFFFFFUL) << 40);
                var newRaw1 = (uncompSize & 0xFFFFFFU) | ((uint)methodIdx << 24);
                BinaryPrimitives.WriteUInt64LittleEndian(newBlocks.AsSpan(i * 12, 8), newRaw0);
                BinaryPrimitives.WriteUInt32LittleEndian(newBlocks.AsSpan(i * 12 + 8, 4), newRaw1);
            }
        }

        if (!blockMethods.Any(m => m != 0))
        {
            File.Delete(tempUcasPath);
            return;
        }

        var newMetas = utoc.AsSpan(metasOffsetOld, (int)(entryCount * 24)).ToArray();
        for (int e = 0; e < entryCount; e++)
        {
            var ol = utoc.AsSpan(offsetLengthsOffset + e * 10, 10);
            var vOff = ((ulong)ol[0] << 32) | ((ulong)ol[1] << 24) | ((ulong)ol[2] << 16) | ((ulong)ol[3] << 8) | ol[4];
            var vLen = ((ulong)ol[5] << 32) | ((ulong)ol[6] << 24) | ((ulong)ol[7] << 16) | ((ulong)ol[8] << 8) | ol[9];
            if (vLen == 0) continue;
            var firstBlk = (int)(vOff / blockSize);
            var lastBlk = (int)((vOff + vLen - 1) / blockSize);
            for (int b = firstBlk; b <= lastBlk && b < blockCount; b++)
            {
                if (blockMethods[b] != 0)
                {
                    newMetas[e * 24 + 20] |= 1;
                    break;
                }
            }
        }

        using var outUtoc = new MemoryStream();
        outUtoc.Write(utoc, 0, blocksOffset);
        outUtoc.Write(newBlocks, 0, newBlocks.Length);
        var methodBytes = new byte[methodLen];
        Encoding.ASCII.GetBytes("Zlib", methodBytes);
        outUtoc.Write(methodBytes, 0, methodBytes.Length);
        outUtoc.Write(utoc, methodsOffset, (int)dirIndexSize);
        outUtoc.Write(newMetas, 0, newMetas.Length);

        var outUtocBytes = outUtoc.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(outUtocBytes.AsSpan(36, 4), 1);
        outUtocBytes[80] |= 1; // EIoContainerFlags::Compressed
        File.Move(tempUcasPath, ucasPath, true);
        await File.WriteAllBytesAsync(utocPath, outUtocBytes);
    }

    private static void CopyFilteredDirectory(string src, string dst, HashSet<string> excluded)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (excluded.Contains(name)) continue;
            CopyFilteredDirectory(dir, Path.Combine(dst, name), excluded);
        }
        foreach (var file in Directory.GetFiles(src))
        {
            var name = Path.GetFileName(file);
            if (excluded.Contains(name)) continue;
            File.Copy(file, Path.Combine(dst, name), true);
        }
    }

    private static List<FileDigestEntry> CollectPakTriplet(string output, string baseName)
    {
        var files = new List<FileDigestEntry>();
        foreach (var ext in new[] { "pak", "utoc", "ucas" })
        {
            var rel = $"Paks/{baseName}.{ext}";
            var full = Path.Combine(output, rel.Replace('/', Path.DirectorySeparatorChar));
            var fi = new FileInfo(full);
            files.Add(new FileDigestEntry(rel, fi.Length, FileSafety.FileHash(full)));
        }
        return files;
    }

    private static (byte[][] Results, List<Dictionary<string, object?>> Changes) PatchCauldronPackages(
        OriginalPackageMetadata[] originals, byte[][] raws)
    {
        string[] expected =
        {
            "8e999e2c6f993f0cd165393bfe204096b2c6722fda22b89a83d0b182c2bc9a20",
            "e5b0fe3e423b2db89b3a29f52fb4d3d46c9d2516c24f185ba89dba91030ccbc4",
            "5605fd0b80dff0079ceddcacb941f30c840eb71bef0c78a2339cef72bd1100d1",
        };
        const string done = "ddsd.江湖人士.完成任务";
        Require(originals.Length is 2 or 3 && raws.Length == originals.Length, "CAULDRON_PACKAGE_COUNT_MISMATCH");
        for (int i = 0; i < originals.Length; i++)
            Require(FileSafety.BytesHash(raws[i]) == expected[i], "SOURCE_VERSION_MISMATCH: " + originals[i].Path);

        var changes = new List<Dictionary<string, object?>>();
        var results = new byte[originals.Length][];
        for (int asset = 0; asset < originals.Length; asset++)
        {
            var metadata = originals[asset];
            var raw = raws[asset];
            var header = raw.AsSpan(0, metadata.HeaderSize).ToArray();
            var shift = 0;
            var exports = metadata.Exports.Select(e => raw.AsSpan(metadata.HeaderSize + (int)e.Offset, (int)e.Size).ToArray()).ToArray();
            var guidChanges = new List<Dictionary<string, object?>>();

            if (asset == 0)
            {
                var appended = AppendName(header, metadata, raws[1], originals[1], done);
                header = appended.Header;
                shift = appended.Delta;
                var guids = GraphGuids(exports[6]);
                var gate17 = TagArrays(exports[17]);
                Require(gate17.Observe.SequenceEqual(new uint[] { 15 }) && gate17.Block.SequenceEqual(new uint[] { 22 }), "RECOVERY_GATE_MISMATCH");
                exports[17] = Gate(exports[17], new uint[] { 15, 16, 24 }, new uint[] { 23, (uint)appended.Index });
                var gate13 = TagArrays(exports[13]);
                Require(gate13.Block.SequenceEqual(new uint[] { 24 }), "CAGE_HIDE_GATE_MISMATCH");
                exports[13] = Gate(exports[13], new uint[] { 23, 15 }, Array.Empty<uint>());
                Require(ReadU32(exports[51], 12) == 24, "RECOVERY_SET_TAG_MISMATCH");
                WriteU32(exports[51], 12, 22);
                exports[51] = ReplaceOnce(exports[51], Concat(new byte[] { 5 }, guids[53]), Concat(new byte[] { 5 }, guids[1]));
                exports[0] = ReplaceOnce(exports[0], Concat(U32(1), guids[61]), Concat(U32(2), guids[61], guids[52]));
                exports[52] = ReplaceOnce(exports[52], Concat(U32(1), guids[52]), U32(0));

                var sequence = exports[7];
                var first = IndexOfBytes(sequence, Concat(new byte[] { 5 }, guids[16])) - 9;
                Require(first > 4 && ReadU32(sequence, first - 4) == 9, "SEQUENCE_MAP_MISMATCH");
                var cut = first + 7 * 34;
                Require(
                    sequence.AsSpan(cut + 10, 16).SequenceEqual(guids[20]) &&
                    sequence.AsSpan(cut + 44, 16).SequenceEqual(guids[21]),
                    "CORPSE_BRANCH_MISMATCH");
                exports[7] = Concat(sequence.AsSpan(0, cut).ToArray(), sequence.AsSpan(cut + 68).ToArray());
                WriteU32(exports[7], first - 4, 7);
                foreach (var id in new[] { 20, 21 })
                    exports[id - 1] = ReplaceOnce(exports[id - 1], Concat(U32(1), guids[8]), U32(0));

                foreach (var id in new uint[] { 18, 37, 52 })
                {
                    var before = guids[id];
                    var after = SHA256.HashData(Encoding.UTF8.GetBytes($"cauldronRegret/0.1.0/{id}")).AsSpan(0, 16).ToArray();
                    var occurrences = 0;
                    for (int eIdx = 0; eIdx < exports.Length; eIdx++)
                    {
                        var copy = exports[eIdx].ToArray();
                        var at = IndexOfBytes(copy, before, 0);
                        while (at >= 0)
                        {
                            Buffer.BlockCopy(after, 0, copy, at, 16);
                            occurrences++;
                            at = IndexOfBytes(copy, before, at + 16);
                        }
                        exports[eIdx] = copy;
                    }
                    Require(occurrences >= 3, "GUID_REFERENCE_MISMATCH");
                    guidChanges.Add(new Dictionary<string, object?>
                    {
                        ["export"] = id,
                        ["original"] = Convert.ToHexStringLower(before),
                        ["patched"] = Convert.ToHexStringLower(after),
                        ["occurrences"] = occurrences,
                    });
                }
            }
            else if (asset == 1)
            {
                Require(TagArrays(exports[5]).Block.SequenceEqual(new uint[] { 9 }) &&
                        TagArrays(exports[6]).Block.SequenceEqual(new uint[] { 9, 7 }), "DELIVERY_GATE_MISMATCH");
                exports[5] = Gate(exports[5], new uint[] { 8, 6 }, new uint[] { 7 });
                exports[6] = Gate(exports[6], new uint[] { 8, 6 }, new uint[] { 7 });
            }
            else
            {
                Require(metadata.Names[607] == "ddsd.江湖人士.死亡" && metadata.Names[604] == done &&
                        exports[0].AsSpan(98558, 8).SequenceEqual(FName(607)), "QUEST_STATE_ROW_MISMATCH");
                WriteU32(exports[0], 98558, 604);
            }

            var changed = new List<Dictionary<string, object?>>();
            for (int index = 0; index < exports.Length; index++)
            {
                var e = metadata.Exports[index];
                var original = raw.AsSpan(metadata.HeaderSize + (int)e.Offset, (int)e.Size).ToArray();
                if (!exports[index].AsSpan().SequenceEqual(original))
                {
                    changed.Add(new Dictionary<string, object?>
                    {
                        ["export"] = index + 1,
                        ["type"] = e.Type,
                        ["beforeBytes"] = original.Length,
                        ["afterBytes"] = exports[index].Length,
                        ["originalSha256"] = FileSafety.BytesHash(original),
                        ["patchedSha256"] = FileSafety.BytesHash(exports[index]),
                    });
                }
            }
            var patched = Assemble(raw, metadata, header, exports, shift);
            changes.Add(new Dictionary<string, object?>
            {
                ["path"] = metadata.Path,
                ["originalSha256"] = expected[asset],
                ["patchedSha256"] = FileSafety.BytesHash(patched),
                ["changedExports"] = changed,
                ["guidChanges"] = guidChanges,
                ["headerBytesAdded"] = shift,
            });
            results[asset] = patched;
        }
        return (results, changes);
    }

    private static (byte[][] Results, List<Dictionary<string, object?>> Changes) PatchSongXiaoWuPackages(
        OriginalPackageMetadata[] originals, byte[][] raws)
    {
        const string expected = "41a99428a36c6e2d040fca5a5bc812eec0ca57559a3f6d9cc6b01e4f3b3df3f2";
        Require(originals.Length == 1 && raws.Length == 1 && FileSafety.BytesHash(raws[0]) == expected, "SOURCE_VERSION_MISMATCH");
        var metadata = originals[0];
        var raw = raws[0];
        var header = raw.AsSpan(0, metadata.HeaderSize).ToArray();
        var exports = metadata.Exports.Select(e => raw.AsSpan(metadata.HeaderSize + (int)e.Offset, (int)e.Size).ToArray()).ToArray();
        var originalExports = exports.Select(d => d.ToArray()).ToArray();
        var guids = GraphGuids(exports[4]);
        Require(metadata.Names[18] == "ddsd.宋小武.开笼子" && metadata.Names[19] == "ddsd.宋小武.死亡", "TAG_SCHEMA_MISMATCH");

        var rewardGate = exports[7];
        Require(Convert.ToHexStringLower(rewardGate.AsSpan(0, 8)) == "00040b0405020105" &&
                ReadU32(rewardGate, 8) == 1 && ReadU32(rewardGate, 12) == 18 &&
                ReadU32(rewardGate, 20) == 1 && ReadU32(rewardGate, 24) == 19, "REWARD_GATE_MISMATCH");
        exports[7] = Concat(rewardGate.AsSpan(0, 20).ToArray(), U32(0), rewardGate.AsSpan(32).ToArray());

        Require(ReadU32(exports[12], 8) == 2 && ReadU32(exports[12], 12) == 18 && ReadU32(exports[12], 20) == 19, "RECOVERY_GATE_MISMATCH");
        exports[12] = ReplaceOnce(exports[12], Concat(new byte[] { 5 }, guids[39]), Concat(new byte[] { 5 }, guids[34]));
        exports[33] = ReplaceOnce(exports[33], Concat(U32(1), guids[9]), Concat(U32(2), guids[9], guids[13]));
        exports[38] = ReplaceOnce(exports[38], Concat(U32(1), guids[13]), U32(0));

        var sequence = exports[5];
        var edges = new List<(uint Id, int At)>();
        foreach (var id in new uint[] { 8, 9, 10, 11, 12, 13, 14, 15 })
        {
            var at = IndexOfBytes(sequence, Concat(new byte[] { 5 }, guids[id]));
            Require(at >= 9, "SEQUENCE_EDGE_MISSING");
            edges.Add((id, at - 9));
        }
        edges.Sort((a, b) => a.At.CompareTo(b.At));
        var mapCountAt = edges[0].At - 4;
        var cut = edges.First(e => e.Id == 12).At;
        Require(ReadU32(sequence, mapCountAt) == 8, "SEQUENCE_SCHEMA_MISMATCH");
        exports[5] = Concat(sequence.AsSpan(0, cut).ToArray(), sequence.AsSpan(cut + 34).ToArray());
        WriteU32(exports[5], mapCountAt, 7);
        exports[11] = ReplaceOnce(exports[11], Concat(U32(1), guids[6]), U32(0));

        var before = guids[34];
        var after = SHA256.HashData(Encoding.UTF8.GetBytes("songXiaoWuRegret/0.1.0/restore-idle")).AsSpan(0, 16).ToArray();
        var occurrences = 0;
        for (int eIdx = 0; eIdx < exports.Length; eIdx++)
        {
            var copy = exports[eIdx].ToArray();
            var at = IndexOfBytes(copy, before, 0);
            while (at >= 0)
            {
                Buffer.BlockCopy(after, 0, copy, at, 16);
                occurrences++;
                at = IndexOfBytes(copy, before, at + 16);
            }
            exports[eIdx] = copy;
        }
        Require(occurrences >= 5, "GUID_REFERENCE_MISMATCH");

        var changed = new List<Dictionary<string, object?>>();
        for (int index = 0; index < exports.Length; index++)
        {
            if (!exports[index].AsSpan().SequenceEqual(originalExports[index]))
            {
                changed.Add(new Dictionary<string, object?>
                {
                    ["export"] = index + 1,
                    ["type"] = metadata.Exports[index].Type,
                    ["beforeBytes"] = originalExports[index].Length,
                    ["afterBytes"] = exports[index].Length,
                    ["originalSha256"] = FileSafety.BytesHash(originalExports[index]),
                    ["patchedSha256"] = FileSafety.BytesHash(exports[index]),
                });
            }
        }
        var patched = Assemble(raw, metadata, header, exports, 0);
        return (new[] { patched }, new List<Dictionary<string, object?>>
        {
            new()
            {
                ["path"] = metadata.Path,
                ["originalSha256"] = expected,
                ["patchedSha256"] = FileSafety.BytesHash(patched),
                ["changedExports"] = changed,
                ["guidChanges"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["export"] = 34,
                        ["original"] = Convert.ToHexStringLower(before),
                        ["patched"] = Convert.ToHexStringLower(after),
                        ["occurrences"] = occurrences,
                    }
                },
                ["headerBytesAdded"] = 0,
            }
        });
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new InvalidOperationException(code);
    }

    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static void WriteU32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    private static byte[] U32(uint value) { var b = new byte[4]; WriteU32(b, 0, value); return b; }
    private static byte[] FName(uint index) => Concat(U32(index), U32(0));

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = arrays.Sum(a => a.Length);
        var res = new byte[total];
        var pos = 0;
        foreach (var a in arrays)
        {
            Buffer.BlockCopy(a, 0, res, pos, a.Length);
            pos += a.Length;
        }
        return res;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle, int start = 0)
    {
        if (start > haystack.Length - needle.Length) return -1;
        var idx = haystack.AsSpan(start).IndexOf(needle);
        return idx < 0 ? -1 : start + idx;
    }

    private static byte[] ReplaceOnce(byte[] data, byte[] before, byte[] after)
    {
        var at = IndexOfBytes(data, before, 0);
        Require(at >= 0 && IndexOfBytes(data, before, at + before.Length) < 0, "ANCHOR_MISMATCH");
        return Concat(data.AsSpan(0, at).ToArray(), after, data.AsSpan(at + before.Length).ToArray());
    }

    private static (uint[] Observe, uint[] Block, byte[] Tail) TagArrays(byte[] data)
    {
        Require(Convert.ToHexStringLower(data.AsSpan(0, 4)) == "00040b04", "GATE_SCHEMA_MISMATCH");
        var at = 8;
        uint[] ReadArr()
        {
            var n = ReadU32(data, at);
            at += 4;
            var res = new uint[n];
            for (int i = 0; i < n; i++, at += 8)
            {
                Require(ReadU32(data, at + 4) == 0, "TAG_NUMBER_MISMATCH");
                res[i] = ReadU32(data, at);
            }
            return res;
        }
        var observe = ReadArr();
        var block = ReadArr();
        return (observe, block, data.AsSpan(at).ToArray());
    }

    private static byte[] Gate(byte[] data, uint[] observe, uint[] block)
    {
        var prior = TagArrays(data);
        var parts = new List<byte[]> { data.AsSpan(0, 8).ToArray(), U32((uint)observe.Length) };
        parts.AddRange(observe.Select(FName));
        parts.Add(U32((uint)block.Length));
        parts.AddRange(block.Select(FName));
        parts.Add(prior.Tail);
        return Concat(parts.ToArray());
    }

    private static Dictionary<uint, byte[]> GraphGuids(byte[] data)
    {
        var count = (int)ReadU32(data, 24);
        Require(28 + count * 20 + 4 == data.Length, "FLOW_MAP_SCHEMA_MISMATCH");
        var res = new Dictionary<uint, byte[]>();
        for (int at = 28; at < 28 + count * 20; at += 20)
            res[ReadU32(data, at + 16)] = data.AsSpan(at, 16).ToArray();
        return res;
    }

    private static (byte[] Header, int Index, int Delta) AppendName(
        byte[] header, OriginalPackageMetadata metadata, byte[] donor, OriginalPackageMetadata donorMetadata, string name)
    {
        const int at = 60;
        var count = (int)ReadU32(header, at);
        var strings = (int)ReadU32(header, at + 4);
        Require(ReadU32(header, 0) == 0 && count == metadata.Names.Length &&
                BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(at + 8, 8)) == 0xc1640000UL, "NAME_BATCH_VERSION_MISMATCH");
        var sourceIndex = Array.IndexOf(donorMetadata.Names, name);
        Require(sourceIndex >= 0 && !metadata.Names.Contains(name), "NAME_APPEND_MISMATCH");
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(0x8000 | name.Length));
        var batch = Concat(
            U32((uint)(count + 1)),
            U32((uint)(strings + nameBytes.Length)),
            header.AsSpan(at + 8, 8).ToArray(),
            header.AsSpan(at + 16, count * 8).ToArray(),
            donor.AsSpan(76 + sourceIndex * 8, 8).ToArray(),
            header.AsSpan(at + 16 + count * 8, count * 2).ToArray(),
            length,
            header.AsSpan(at + 16 + count * 10, strings).ToArray(),
            nameBytes);
        var oldEnd = at + 16 + count * 10 + strings;
        var oldPadding = (int)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(oldEnd, 8));
        var padding = (8 - (at + batch.Length + 8) % 8) % 8;
        var padBytes = new byte[8 + padding];
        BinaryPrimitives.WriteUInt64LittleEndian(padBytes.AsSpan(0, 8), (ulong)padding);
        var result = Concat(header.AsSpan(0, at).ToArray(), batch, padBytes, header.AsSpan(oldEnd + 8 + oldPadding).ToArray());
        var delta = result.Length - header.Length;
        Require(delta % 8 == 0, "HEADER_ALIGNMENT_MISMATCH");
        foreach (var off in new[] { 4, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56 })
            WriteU32(result, off, (uint)(ReadU32(header, off) + delta));
        return (result, count, delta);
    }

    private static byte[] Assemble(byte[] raw, OriginalPackageMetadata metadata, byte[] header, byte[][] exports, int shift)
    {
        var order = metadata.Exports.Select((entry, index) => (entry, index)).OrderBy(x => x.entry.Offset).ToArray();
        long oldOffset = 0, offset = 0;
        var parts = new List<byte[]> { header };
        foreach (var (entry, index) in order)
        {
            Require(entry.Offset == oldOffset, "NONCONTIGUOUS_EXPORT_DATA");
            var data = exports[index];
            var table = metadata.ExportOffset + shift + index * 72;
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(table, 8), (ulong)offset);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(table + 8, 8), (ulong)data.Length);
            parts.Add(data);
            oldOffset += entry.Size;
            offset += data.Length;
        }
        Require(metadata.HeaderSize + oldOffset == raw.Length, "UNEXPECTED_TRAILING_DATA");
        return Concat(parts.ToArray());
    }
}
