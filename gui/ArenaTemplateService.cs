using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhantomBladeModManager;

internal static class ArenaTemplateService
{
    private const int SaveSizeLimit = 128 * 1024 * 1024;
    private static readonly byte[] SaveMagic = "PBZS"u8.ToArray();
    private static readonly byte[] BlockMagic = { 80, 66, 90, 83, 1, 1, 0, 0 };
    private static readonly string[] SupportedBuilds = { "26367565", "26266538" };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OodleDecompressDelegate(
        byte[] source, long sourceLength, byte[] target, long targetLength,
        int fuzzSafe, int crc, int verbosity, IntPtr baseAddress, long baseSize,
        IntPtr callback, IntPtr callbackData, IntPtr scratch, long scratchSize, int threadPhase);

    private static OodleDecompressDelegate? _oodleDecompress;

    public static string OverrideDirectoryPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            FileSafety.Fail("invalid_path", ("label", "LOCALAPPDATA"));
        return FileSafety.ValidatePath(Path.Combine(localAppData, "PBZArena", "Templates"), "arena template dir");
    }

    public static string OverrideFilePath() => Path.Combine(OverrideDirectoryPath(), "high-cycle-profile.json");

    public static string BuiltinFilePath(string root) =>
        FileSafety.ValidatePath(
            Path.Combine(root, "mods", "features", "phantom-blade-training", "data", "high-cycle-template.json"),
            "builtin arena template");

    public static async Task<Dictionary<string, object?>> ExecuteAsync(
        string root, string game, string? target, Dictionary<string, string>? options, bool dryRun)
    {
        var builtinPath = BuiltinFilePath(root);
        var overrideDir = OverrideDirectoryPath();
        var overridePath = OverrideFilePath();
        var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (options != null && options.TryGetValue("reset", out var resetVal) &&
            string.Equals(resetVal, "true", StringComparison.OrdinalIgnoreCase))
        {
            if (FileSafety.IsGameRunning()) FileSafety.Fail("game_running");
            var existed = FileSafety.GetNodeInfoIfExists(overridePath, "override template") is FileInfo;
            if (existed && !dryRun)
                FileSafety.RemoveTreeIfExists(overridePath, new[] { localRoot });
            var builtinNode = JsonNode.Parse(await File.ReadAllTextAsync(builtinPath, Encoding.UTF8))?.AsObject()
                ?? throw new ManagerException("arena_template_invalid", new() { ["target"] = builtinPath });
            return SummarizeTemplate("reset", "builtin", builtinPath, overridePath, false, dryRun, builtinNode, FileSafety.FileHash(builtinPath), null);
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            var hasOverride = FileSafety.GetNodeInfoIfExists(overridePath, "override template") is FileInfo;
            var activePath = hasOverride ? overridePath : builtinPath;
            var node = JsonNode.Parse(await File.ReadAllTextAsync(activePath, Encoding.UTF8))?.AsObject()
                ?? throw new ManagerException("arena_template_invalid", new() { ["target"] = activePath });
            ValidateTemplateObject(node, activePath);
            return SummarizeTemplate(
                "inspect",
                hasOverride ? "override" : "builtin",
                activePath,
                overridePath,
                hasOverride,
                dryRun,
                node,
                FileSafety.FileHash(activePath),
                null);
        }

        if (FileSafety.IsGameRunning()) FileSafety.Fail("game_running");
        var inputPath = FileSafety.ValidatePath(target, "template source");
        if (FileSafety.GetNodeInfoIfExists(inputPath, "template source") is not FileInfo)
            FileSafety.Fail("expected_file", ("target", inputPath));

        var skeleton = JsonNode.Parse(await File.ReadAllTextAsync(builtinPath, Encoding.UTF8))?.AsObject()
            ?? throw new ManagerException("arena_template_invalid", new() { ["target"] = builtinPath });

        var rawBytes = await File.ReadAllBytesAsync(inputPath);
        string? selectedSlot = null;
        JsonObject sourceSlot;

        if (rawBytes.Length >= 6 && rawBytes.AsSpan(0, 4).SequenceEqual(SaveMagic))
        {
            var requestedSlot = options != null && options.TryGetValue("slot", out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
            var key = await ReadSaveKeyAsync(game);
            (selectedSlot, sourceSlot) = ExtractBestSlotFromContainer(rawBytes, key, root, game, requestedSlot, inputPath);
        }
        else
        {
            try
            {
                sourceSlot = JsonNode.Parse(rawBytes)?.AsObject()
                    ?? throw new ManagerException("arena_template_invalid", new() { ["target"] = inputPath });
            }
            catch (ManagerException) { throw; }
            catch
            {
                FileSafety.Fail("arena_template_invalid", ("target", inputPath));
                return null!;
            }
        }

        var sanitized = SanitizeIntoTemplate(skeleton, sourceSlot, inputPath);
        ValidateTemplateObject(sanitized, inputPath);

        var serialized = sanitized.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = FileSafety.JsonOptions.Encoder,
        }) + "\n";
        var sha256 = FileSafety.StringHash(serialized);

        if (!dryRun)
        {
            Directory.CreateDirectory(overrideDir);
            FileSafety.WriteTextAtomic(overridePath, serialized, new[] { localRoot });
        }

        return SummarizeTemplate("imported", "override", overridePath, overridePath, true, dryRun, sanitized, sha256, selectedSlot);
    }

    private static Dictionary<string, object?> SummarizeTemplate(
        string action, string source, string activePath, string overridePath,
        bool hasOverride, bool dryRun, JsonObject template, string sha256, string? slotName)
    {
        var sys = template["PBZGameSystem"]!.AsObject();
        var inv = template["Inventory"]!.AsObject();
        return new Dictionary<string, object?>
        {
            ["action"] = action,
            ["source"] = source,
            ["activePath"] = activePath,
            ["overridePath"] = overridePath,
            ["hasOverride"] = hasOverride,
            ["dryRun"] = dryRun,
            ["selectedSlot"] = slotName,
            ["sha256"] = sha256,
            ["cycleCount"] = sys["CycleCount"]?.GetValue<int>() ?? 7,
            ["difficulty"] = sys["DifficultModeByte"]?.GetValue<int>() ?? 4,
            ["mainWeaponCount"] = inv["MainWeaponInfos"]?.AsObject().Count ?? 0,
            ["phantomWeaponCount"] = inv["ShaChiWeaponInfo"]?.AsObject().Count ?? 0,
            ["accessoryCount"] = inv["AccessoryInfos"]?.AsObject().Count ?? 0,
            ["medicineCount"] = inv["MedicineInfos"]?.AsObject().Count ?? 0,
        };
    }

    private static JsonObject SanitizeIntoTemplate(JsonObject skeleton, JsonObject slot, string target)
    {
        if (slot["Inventory"] is not JsonObject srcInv ||
            srcInv["MainWeaponInfos"] is not JsonObject mainInfos || mainInfos.Count == 0 ||
            srcInv["ShaChiWeaponInfo"] is not JsonObject phantomInfos)
        {
            FileSafety.Fail("arena_template_invalid", ("target", target));
            return null!;
        }

        var dstInv = skeleton["Inventory"]!.AsObject();
        foreach (var key in new[]
        {
            "MainWeaponInfos", "ShaChiWeaponInfo", "AccessoryInfos", "MedicineInfos",
            "AccessoryIDs", "MainWeapons", "ShaqiWeapons", "TopMedicine", "BottomMedicine",
            "CurrentWeaponIndex", "CurrentMainWeaponID", "CurrentShaqiWeaponIndex"
        })
        {
            if (srcInv[key] != null)
                dstInv[key] = srcInv[key]!.DeepClone();
        }

        // Preserve permanent stat-cap items while keeping story/quest inventory stripped.
        if (srcInv["InventoryInfos"] is JsonObject srcItems && dstInv["InventoryInfos"] is JsonObject dstItems)
        {
            foreach (var statItem in new[] { "Item_001", "Item_002", "Item_Traversing" })
            {
                if (srcItems[statItem] != null)
                    dstItems[statItem] = srcItems[statItem]!.DeepClone();
            }
        }

        // Normalize equipped weapon arrays against owned records.
        EnsureValidEquippedWeapons(dstInv, main: true);
        EnsureValidEquippedWeapons(dstInv, main: false);

        if (slot["AbilityNode_RenewalHun"] is JsonObject hunNodes)
            skeleton["AbilityNode_RenewalHun"] = hunNodes.DeepClone();
        if (slot["AbilityNode_RenewalHun_Climax"] is JsonObject climaxNodes)
            skeleton["AbilityNode_RenewalHun_Climax"] = climaxNodes.DeepClone();

        if (slot["PBZGameSystem"] is JsonObject srcSys)
        {
            var dstSys = skeleton["PBZGameSystem"]!.AsObject();
            if (srcSys["CycleCount"] != null)
                dstSys["CycleCount"] = Math.Clamp(srcSys["CycleCount"]!.GetValue<int>(), 0, 7);
            if (srcSys["DifficultModeByte"] != null)
                dstSys["DifficultModeByte"] = Math.Clamp(srcSys["DifficultModeByte"]!.GetValue<int>(), 0, 4);
        }

        return skeleton;
    }

    private static void EnsureValidEquippedWeapons(JsonObject inv, bool main)
    {
        var infos = inv[main ? "MainWeaponInfos" : "ShaChiWeaponInfo"]!.AsObject();
        var listKey = main ? "MainWeapons" : "ShaqiWeapons";
        var valid = new List<string>();
        if (inv[listKey] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var id = item?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(id) && id != "None" && infos.ContainsKey(id) && !valid.Contains(id, StringComparer.Ordinal))
                {
                    valid.Add(id);
                    if (valid.Count == 2) break;
                }
            }
        }
        if (valid.Count == 0 && infos.Count > 0)
        {
            foreach (var kv in infos)
            {
                valid.Add(kv.Key);
                if (valid.Count == 2) break;
            }
        }
        var nextArr = new JsonArray();
        foreach (var id in valid) nextArr.Add(id);
        inv[listKey] = nextArr;
        var idxKey = main ? "CurrentWeaponIndex" : "CurrentShaqiWeaponIndex";
        var curIdx = inv[idxKey]?.GetValue<int>() ?? 0;
        var clamped = valid.Count == 0 ? 0 : Math.Clamp(curIdx, 0, valid.Count - 1);
        inv[idxKey] = clamped;
        if (main && valid.Count > 0)
            inv["CurrentMainWeaponID"] = valid[clamped];
    }

    private static void ValidateTemplateObject(JsonObject node, string target)
    {
        if (node["PBZGameSystem"] is not JsonObject sys ||
            node["Inventory"] is not JsonObject inv ||
            inv["MainWeaponInfos"] is not JsonObject mainInfos || mainInfos.Count == 0 ||
            inv["ShaChiWeaponInfo"] is not JsonObject ||
            inv["MainWeapons"] is not JsonArray mainEquipped || mainEquipped.Count == 0)
        {
            FileSafety.Fail("arena_template_invalid", ("target", target));
            return;
        }
        var cycle = sys["CycleCount"]?.GetValue<int>() ?? -1;
        var diff = sys["DifficultModeByte"]?.GetValue<int>() ?? -1;
        if (cycle is < 0 or > 7 || diff is < 0 or > 4)
            FileSafety.Fail("arena_template_invalid", ("target", target));
    }

    private static (string SlotName, JsonObject Slot) ExtractBestSlotFromContainer(
        byte[] bytes, byte[] key, string root, string game, string? requestedSlot, string sourcePath)
    {
        void RequireFormat(bool cond)
        {
            if (!cond) FileSafety.Fail("arena_template_invalid", ("target", sourcePath));
        }

        RequireFormat(bytes.Length is >= 48 and <= SaveSizeLimit && bytes.AsSpan(0, 4).SequenceEqual(SaveMagic));
        RequireFormat(bytes[4] == 3 && bytes[5] == 1);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        int directorySize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        RequireFormat(count is > 0 and <= 1000 && directorySize >= 36 && directorySize <= bytes.Length - 12);

        byte[] directory = OpenAesBlock(bytes.AsSpan(12, directorySize), key, sourcePath);
        RequireFormat(directory.Length >= 12 && directory.AsSpan(0, 12).SequenceEqual(bytes.AsSpan(0, 12)));

        using var reader = new BinaryReader(new MemoryStream(directory), Encoding.UTF8);
        reader.BaseStream.Position = 12;
        var blocks = new List<(string Name, byte Codec, int PlainSize, byte[] Data)>();
        int expectedOffset = 0;
        for (int i = 0; i < count; i++)
        {
            int length = reader.ReadUInt16();
            RequireFormat(length is > 0 and <= 512 && reader.BaseStream.Length - reader.BaseStream.Position >= length + 14);
            string name = new UTF8Encoding(false, true).GetString(reader.ReadBytes(length));
            byte codec = reader.ReadByte();
            byte sealedFlag = reader.ReadByte();
            int size = reader.ReadInt32();
            int offset = reader.ReadInt32();
            int storedSize = reader.ReadInt32();
            RequireFormat(codec <= 2 && sealedFlag == 1 && size is > 0 and <= SaveSizeLimit);
            RequireFormat(offset == expectedOffset && storedSize >= 36 && 12L + directorySize + offset + storedSize <= bytes.Length);
            blocks.Add((name, codec, size, bytes.AsSpan(12 + directorySize + offset, storedSize).ToArray()));
            expectedOffset = checked(offset + storedSize);
        }

        JsonObject? DecodeSlot((string Name, byte Codec, int PlainSize, byte[] Data) block)
        {
            byte[] packed = OpenAesBlock(block.Data, key, sourcePath);
            byte[] plain;
            switch (block.Codec)
            {
                case 0:
                    plain = packed;
                    break;
                case 1:
                    using (var stream = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress))
                    {
                        plain = new byte[block.PlainSize];
                        stream.ReadExactly(plain);
                    }
                    break;
                case 2:
                    var oodle = ResolveOodle(root, game);
                    plain = new byte[block.PlainSize];
                    long decoded = oodle(packed, packed.Length, plain, plain.Length, 1, 1, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
                    RequireFormat(decoded == plain.Length);
                    break;
                default:
                    return null;
            }
            return JsonNode.Parse(plain)?.AsObject();
        }

        if (!string.IsNullOrEmpty(requestedSlot))
        {
            var match = blocks.FirstOrDefault(b => string.Equals(b.Name, requestedSlot, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(match.Name))
                FileSafety.Fail("arena_slot_not_found", ("slot", requestedSlot));
            var parsed = DecodeSlot(match);
            if (parsed == null || parsed["Inventory"] is not JsonObject)
                FileSafety.Fail("arena_slot_not_found", ("slot", requestedSlot));
            return (match.Name, parsed!);
        }

        (string Name, JsonObject Obj, int Cycle, int Weapons)? best = null;
        foreach (var b in blocks)
        {
            if (string.Equals(b.Name, "AdvancedSaveGame", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(b.Name, "ArenaCheckpoint", StringComparison.OrdinalIgnoreCase))
                continue;
            JsonObject? obj;
            try { obj = DecodeSlot(b); }
            catch { continue; }
            if (obj?["Inventory"] is not JsonObject inv ||
                inv["MainWeaponInfos"] is not JsonObject mainW || mainW.Count == 0 ||
                obj["PBZGameSystem"] is not JsonObject sys)
                continue;
            int cycle = sys["CycleCount"]?.GetValue<int>() ?? 0;
            int weapons = mainW.Count + (inv["ShaChiWeaponInfo"]?.AsObject().Count ?? 0);
            if (best == null || cycle > best.Value.Cycle || (cycle == best.Value.Cycle && weapons > best.Value.Weapons))
                best = (b.Name, obj, cycle, weapons);
        }

        if (best == null)
            FileSafety.Fail("arena_slot_not_found", ("slot", "auto"));
        return (best!.Value.Name, best.Value.Obj);
    }

    private static byte[] OpenAesBlock(ReadOnlySpan<byte> block, byte[] key, string target)
    {
        if (block.Length < 36 || !block[..8].SequenceEqual(BlockMagic))
            FileSafety.Fail("arena_template_invalid", ("target", target));
        var plain = new byte[block.Length - 36];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(block.Slice(8, 12), block[36..], block.Slice(20, 16), plain);
            return plain;
        }
        catch
        {
            FileSafety.Fail("arena_template_invalid", ("target", target));
            return null!;
        }
    }

    private static OodleDecompressDelegate ResolveOodle(string root, string game)
    {
        if (_oodleDecompress != null) return _oodleDecompress;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "oo2core_9_win64.dll"),
            Path.Combine(root, "mods", "features", "phantom-blade-map", ".local-runtime", "pak-tools", "oo2core_9_win64.dll"),
            Path.Combine(root, "mods", "remedies", "phantom-blade-chaos-reset", "dist", "PhantomBladeQuestManager-current", "oo2core_9_win64.dll"),
            Path.Combine(game, "Engine", "Binaries", "ThirdParty", "Oodle", "Win64", "oo2core_9_win64.dll"),
        };
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            if (NativeLibrary.TryLoad(candidate, out var handle) &&
                NativeLibrary.TryGetExport(handle, "OodleLZ_Decompress", out var fn))
            {
                _oodleDecompress = Marshal.GetDelegateForFunctionPointer<OodleDecompressDelegate>(fn);
                return _oodleDecompress;
            }
        }
        if (NativeLibrary.TryLoad("oo2core_9_win64.dll", out var sysHandle) &&
            NativeLibrary.TryGetExport(sysHandle, "OodleLZ_Decompress", out var sysFn))
        {
            _oodleDecompress = Marshal.GetDelegateForFunctionPointer<OodleDecompressDelegate>(sysFn);
            return _oodleDecompress;
        }
        FileSafety.Fail("build_tool_missing", ("tool", "oo2core_9_win64.dll"), ("variable", "PATH"));
        return null!;
    }

    private static async Task<byte[]> ReadSaveKeyAsync(string game)
    {
        var buildId = ManagerEngine.ReadInstalledGameBuildId(game) ?? "";
        if (!SupportedBuilds.Contains(buildId, StringComparer.Ordinal))
            FileSafety.Fail("incompatible_build", ("expected", "26367565"), ("actual", buildId));

        var exePath = FileSafety.ValidatePath(Path.Combine(game, "platform", "steam", "PBZ.exe"), "PBZ.exe");
        if (FileSafety.GetNodeInfoIfExists(exePath, "PBZ.exe") is not FileInfo)
            FileSafety.Fail("invalid_game");

        await using var file = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        async Task<byte[]> ReadSliceAsync(long offset, int length)
        {
            var data = new byte[length];
            file.Position = offset;
            await file.ReadExactlyAsync(data);
            return data;
        }

        var dos = await ReadSliceAsync(0, 64);
        int pe = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(60));
        var coff = await ReadSliceAsync(pe, 24);
        if (!coff.AsSpan(0, 4).SequenceEqual("PE\0\0"u8))
            FileSafety.Fail("incompatible_build", ("expected", "26367565"), ("actual", buildId));

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20));
        var optional = await ReadSliceAsync(pe + 24, optionalSize);
        long imageBase = BinaryPrimitives.ReadInt64LittleEndian(optional.AsSpan(24));
        var sections = await ReadSliceAsync(pe + 24 + optionalSize, sectionCount * 40);

        async Task<byte[]> ReadAtVaAsync(long va, int length)
        {
            long rva = va - imageBase;
            for (int i = 0; i < sectionCount; i++)
            {
                int start = BinaryPrimitives.ReadInt32LittleEndian(sections.AsSpan(i * 40 + 12));
                int size = BinaryPrimitives.ReadInt32LittleEndian(sections.AsSpan(i * 40 + 16));
                int offset = BinaryPrimitives.ReadInt32LittleEndian(sections.AsSpan(i * 40 + 20));
                if (rva >= start && rva + length <= (long)start + size)
                    return await ReadSliceAsync(offset + rva - start, length);
            }
            FileSafety.Fail("incompatible_build", ("expected", "26367565"), ("actual", buildId));
            return null!;
        }

        var (va0, va1, va2, va3) = buildId == "26367565"
            ? (0x14693ae1aL + 0x58ff0aeL, 0x14693ae28L + 0x58ff080L, 0x14693ae37L + 0x58ff081L, 0x14693ae46L + 0x58ff092L)
            : (0x1468ff5baL + 0x58c918eL, 0x1468ff5c8L + 0x58c9160L, 0x1468ff5d7L + 0x58c9161L, 0x1468ff5e6L + 0x58c9172L);

        var a = await ReadAtVaAsync(va0, 16);
        var b = await ReadAtVaAsync(va1, 16);
        var c = await ReadAtVaAsync(va2, 16);
        var d = await ReadAtVaAsync(va3, 16);
        var key = new byte[32];
        for (int i = 0; i < 16; i++)
        {
            key[i] = (byte)(a[i] ^ b[i]);
            key[i + 16] = (byte)(c[i] ^ d[i]);
        }
        return key;
    }
}
